// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    InMemoryAuditEventStore.cs
//
// Purpose:
// Thread-safe in-memory audit event ring buffer used for local history.
//
// -----------------------------------------------------------------------------
using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Audit;

/// <summary>
/// Bounded, thread-safe in-memory audit trail. Oldest events are evicted when capacity is exceeded.
/// History is discarded when the process exits.
/// </summary>
internal sealed class InMemoryAuditEventStore : IAuditEventWriter, IAuditEventReader
{
    /// <summary>Default ring capacity when options do not specify <c>MaximumEvents</c>.</summary>
    public const int DefaultMaximumEvents = 10_000;

    /// <summary>Default max events returned for CLI <c>show audit</c>-style listings.</summary>
    public const int DefaultCliLimit = 50;

    /// <summary>Max stored description length after sanitization.</summary>
    private const int MaxDescriptionLength = 240;

    /// <summary>Max length for username/session/endpoint-style fields after sanitization.</summary>
    private const int MaxFieldLength = 128;

    /// <summary>Max detail dictionary entries retained per event.</summary>
    private const int MaxDetailEntries = 8;

    /// <summary>Max length for each detail value after sanitization.</summary>
    private const int MaxDetailValueLength = 96;

    /// <summary>Serializes all reads and writes to the in-memory list and eviction flag.</summary>
    private readonly object _gate = new();

    /// <summary>Chronological event buffer (oldest at index 0); trimmed to <c>_maximumEvents</c>.</summary>
    private readonly List<AuditEvent> _events;

    /// <summary>Clock used for <see cref="AuditEvent.TimestampUtc"/>.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Logger for non-throwing write failures.</summary>
    private readonly ILogger<InMemoryAuditEventStore> _logger;

    /// <summary>Optional test hook run after the unlocked enabled check and before taking <c>_gate</c>.</summary>
    private readonly Action? _beforeWriteLock;

    /// <summary>When false, writes are no-ops and queries return empty.</summary>
    private volatile bool _enabled;

    /// <summary>Maximum retained events; oldest are removed when exceeded. Changed only under <c>_gate</c>.</summary>
    private volatile int _maximumEvents;

    /// <summary>Next monotonic sequence number assigned under <c>_gate</c>.</summary>
    private long _nextSequence = 1;

    /// <summary>Set when at least one oldest event has been dropped due to capacity.</summary>
    private bool _hasEvicted;

    /// <summary>
    /// Initializes from <see cref="TechConnectServerOptions.Audit"/> (enabled flag and capacity).
    /// </summary>
    /// <param name="options">Server options containing audit settings.</param>
    /// <param name="timeProvider">Clock for event timestamps.</param>
    /// <param name="logger">Logger for write failures.</param>
    public InMemoryAuditEventStore(
        IOptions<TechConnectServerOptions> options,
        TimeProvider timeProvider,
        ILogger<InMemoryAuditEventStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var audit = options.Value.Audit ?? new AuditOptions();
        _enabled = audit.Enabled;
        _maximumEvents = audit.MaximumEvents;
        _events = new List<AuditEvent>(Math.Min(_maximumEvents, 256));
    }

    /// <summary>Test constructor with explicit capacity.</summary>
    /// <param name="enabled">Initial enabled flag.</param>
    /// <param name="maximumEvents">Initial capacity.</param>
    /// <param name="timeProvider">Clock for event timestamps.</param>
    /// <param name="logger">Logger for write failures.</param>
    /// <param name="beforeWriteLock">Optional hook run between the unlocked enabled check and the lock (tests order races).</param>
    internal InMemoryAuditEventStore(
        bool enabled,
        int maximumEvents,
        TimeProvider timeProvider,
        ILogger<InMemoryAuditEventStore> logger,
        Action? beforeWriteLock = null)
    {
        if (maximumEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        }

        _enabled = enabled;
        _maximumEvents = maximumEvents;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _beforeWriteLock = beforeWriteLock;
        _events = new List<AuditEvent>(Math.Min(maximumEvents, 256));
    }

    /// <summary>Whether audit recording is enabled; when false, APIs are no-ops / empty.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>
    /// Whether the ring has ever dropped an oldest event because capacity was exceeded.
    /// </summary>
    /// <remarks>
    /// Becomes <see langword="true"/> the first time <see cref="TryWrite"/> removes an entry
    /// to stay within <see cref="MaximumEvents"/>, and remains true for the process lifetime.
    /// Thread-safe under <c>_gate</c>.
    /// </remarks>
    public bool HasEvictedEvents
    {
        get
        {
            lock (_gate)
            {
                return _hasEvicted;
            }
        }
    }

    /// <summary>
    /// Current number of retained events (0…<see cref="MaximumEvents"/>).
    /// </summary>
    /// <remarks>Thread-safe under <c>_gate</c>; does not include historically evicted events.</remarks>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>Configured maximum retained events for this store instance.</summary>
    public int MaximumEvents => _maximumEvents;

    /// <summary>
    /// Applies a validated audit enablement flag and retention limit to the running store.
    /// </summary>
    /// <remarks>
    /// Called only after a validated configuration has been persisted. Shrinking the limit
    /// evicts the oldest retained events immediately; disabling stops recording but keeps
    /// already-retained events in memory (hidden while disabled).
    /// </remarks>
    /// <param name="audit">Validated audit options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="audit"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="AuditOptions.MaximumEvents"/> is less than 1.</exception>
    public void ApplyOptions(AuditOptions audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (audit.MaximumEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(audit));
        }

        lock (_gate)
        {
            _maximumEvents = audit.MaximumEvents;
            if (_events.Count > _maximumEvents)
            {
                _events.RemoveRange(0, _events.Count - _maximumEvents);
                _hasEvicted = true;
            }

            _enabled = audit.Enabled;
        }
    }

    /// <summary>
    /// Appends a sanitized audit event when enabled; never throws for ordinary failures.
    /// </summary>
    /// <param name="type">Event type classification.</param>
    /// <param name="outcome">Success/failure outcome.</param>
    /// <param name="description">Human-readable description (sanitized/truncated).</param>
    /// <param name="context">Optional actor/session/detail fields.</param>
    public void TryWrite(
        AuditEventType type,
        AuditEventOutcome outcome,
        string description,
        AuditEventContext? context = null)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            _beforeWriteLock?.Invoke();
            lock (_gate)
            {
                if (!_enabled)
                {
                    return;
                }

                var evt = new AuditEvent
                {
                    Sequence = _nextSequence++,
                    TimestampUtc = _timeProvider.GetUtcNow().ToUniversalTime(),
                    EventType = type,
                    Outcome = outcome,
                    ActorUsername = SanitizeOptional(context?.ActorUsername, MaxFieldLength),
                    RequestedUsername = SanitizeOptional(context?.RequestedUsername, MaxFieldLength),
                    SessionId = SanitizeOptional(context?.SessionId, MaxFieldLength),
                    ConnectorIdentity = SanitizeOptional(context?.ConnectorIdentity, MaxFieldLength),
                    RemoteEndpoint = SanitizeOptional(context?.RemoteEndpoint, MaxFieldLength),
                    Description = SanitizeRequired(description, MaxDescriptionLength),
                    Details = SanitizeDetails(context?.Details)
                };

                _events.Add(evt);
                while (_events.Count > _maximumEvents)
                {
                    _events.RemoveAt(0);
                    _hasEvicted = true;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit event write failed type={EventType}", type);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxCount"/> newest events (newest first), or empty when disabled.
    /// </summary>
    public IReadOnlyList<AuditEvent> GetRecent(int maxCount)
    {
        if (!_enabled || maxCount <= 0)
        {
            return Array.Empty<AuditEvent>();
        }

        lock (_gate)
        {
            return SnapshotNewestFirst(_events, maxCount);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxCount"/> newest events matching <paramref name="sessionId"/> (ordinal-ignore-case).
    /// </summary>
    public IReadOnlyList<AuditEvent> GetBySessionId(string sessionId, int maxCount)
    {
        if (!_enabled || maxCount <= 0 || string.IsNullOrWhiteSpace(sessionId))
        {
            return Array.Empty<AuditEvent>();
        }

        var needle = sessionId.Trim();
        lock (_gate)
        {
            var matches = new List<AuditEvent>();
            for (var i = _events.Count - 1; i >= 0 && matches.Count < maxCount; i--)
            {
                var evt = _events[i];
                if (string.Equals(evt.SessionId, needle, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(evt);
                }
            }

            return matches.Count == 0
                ? Array.Empty<AuditEvent>()
                : new ReadOnlyCollection<AuditEvent>(matches);
        }
    }

    /// <summary>
    /// Returns up to <paramref name="maxCount"/> newest events whose actor or requested username matches.
    /// </summary>
    public IReadOnlyList<AuditEvent> GetByUsername(string username, int maxCount)
    {
        if (!_enabled || maxCount <= 0 || string.IsNullOrWhiteSpace(username))
        {
            return Array.Empty<AuditEvent>();
        }

        var needle = OperatorUsernameRules.NormalizeKey(username);
        lock (_gate)
        {
            var matches = new List<AuditEvent>();
            for (var i = _events.Count - 1; i >= 0 && matches.Count < maxCount; i--)
            {
                var evt = _events[i];
                if (UsernameMatches(evt.ActorUsername, needle) ||
                    UsernameMatches(evt.RequestedUsername, needle))
                {
                    matches.Add(evt);
                }
            }

            return matches.Count == 0
                ? Array.Empty<AuditEvent>()
                : new ReadOnlyCollection<AuditEvent>(matches);
        }
    }

    /// <summary>Compares a stored username to a normalized needle using operator username rules.</summary>
    private static bool UsernameMatches(string? value, string normalizedNeedle) =>
        !string.IsNullOrEmpty(value) &&
        OperatorUsernameRules.Comparer.Equals(OperatorUsernameRules.NormalizeKey(value), normalizedNeedle);

    /// <summary>Copies the newest <paramref name="maxCount"/> events into a read-only snapshot (newest first).</summary>
    private static IReadOnlyList<AuditEvent> SnapshotNewestFirst(List<AuditEvent> events, int maxCount)
    {
        if (events.Count == 0)
        {
            return Array.Empty<AuditEvent>();
        }

        var take = Math.Min(maxCount, events.Count);
        var snapshot = new AuditEvent[take];
        for (var i = 0; i < take; i++)
        {
            snapshot[i] = events[events.Count - 1 - i];
        }

        return new ReadOnlyCollection<AuditEvent>(snapshot);
    }

    /// <summary>Sanitizes a required string field, substituting <c>(none)</c> when empty after stripping.</summary>
    private static string SanitizeRequired(string? value, int maxLength)
    {
        var sanitized = SanitizeOptional(value, maxLength);
        return string.IsNullOrEmpty(sanitized) ? "(none)" : sanitized;
    }

    /// <summary>
    /// Trims and strips C0/C1/DEL controls from an optional field; returns null when empty.
    /// </summary>
    private static string? SanitizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, maxLength)];
        var written = 0;
        foreach (var ch in value.Trim())
        {
            if (written >= maxLength)
            {
                break;
            }

            // Strip C0/C1 controls and DEL to prevent terminal injection in CLI output.
            if (ch < '\u0020' || (ch >= '\u007F' && ch <= '\u009F'))
            {
                continue;
            }

            buffer[written++] = ch;
        }

        return written == 0 ? null : new string(buffer[..written]);
    }

    /// <summary>
    /// Sanitizes detail key/value pairs with entry and length caps; returns null when none remain.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? SanitizeDetails(
        IReadOnlyDictionary<string, string>? details)
    {
        if (details is null || details.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in details)
        {
            if (map.Count >= MaxDetailEntries)
            {
                break;
            }

            var key = SanitizeOptional(pair.Key, 48);
            var value = SanitizeOptional(pair.Value, MaxDetailValueLength);
            if (key is null || value is null)
            {
                continue;
            }

            map[key] = value;
        }

        return map.Count == 0
            ? null
            : new ReadOnlyDictionary<string, string>(map);
    }
}
