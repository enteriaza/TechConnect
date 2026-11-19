// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CachedSerialConfiguration.cs
//
// Purpose:
// Caches connector-reported serial port settings for operator CLI display without
// inventing defaults when the connector has not yet reported.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// Connector-reported serial configuration cached for operator display.
/// </summary>
/// <remarks>
/// Never invent defaults; unknown until the connector reports (Hello seed when baud ≥ 1,
/// SerialConfiguration frames, or successful SetBaudResult). Cleared when the session
/// is removed from the registry / on reconnect.
/// </remarks>
internal sealed class CachedSerialConfiguration
{
    /// <summary>Connector session ID this cache entry belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Port name from cache or Hello, if known.</summary>
    public string? PortName { get; init; }

    /// <summary>Baud rate last reported by the connector.</summary>
    public required int BaudRate { get; init; }

    /// <summary>Data bits last reported by the connector.</summary>
    public required int DataBits { get; init; }

    /// <summary>Parity name last reported by the connector.</summary>
    public required string Parity { get; init; }

    /// <summary>Stop bits name last reported by the connector.</summary>
    public required string StopBits { get; init; }

    /// <summary>Handshake name last reported by the connector.</summary>
    public required string Handshake { get; init; }

    /// <summary>UTC time when this configuration was last written into the cache.</summary>
    public required DateTimeOffset ReportedAtUtc { get; init; }
}

/// <summary>
/// In-memory cache of last confirmed serial configuration per connector session.
/// </summary>
/// <remarks>
/// Thread-safe under a private gate. Cleared on session removal / reconnect (new registration).
/// Does not own connector or attachment lifetime.
/// </remarks>
internal sealed class ConnectorSerialConfigurationCache
{
    /// <summary>Serializes cache mutations.</summary>
    private readonly object _gate = new();

    /// <summary>Entries keyed by session ID (case-insensitive).</summary>
    private readonly Dictionary<string, CachedSerialConfiguration> _bySessionId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Inserts or replaces the configuration for <see cref="CachedSerialConfiguration.SessionId"/>.
    /// </summary>
    /// <param name="configuration">Non-null configuration snapshot to store.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    public void Set(CachedSerialConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_gate)
        {
            _bySessionId[configuration.SessionId] = configuration;
        }
    }

    /// <summary>
    /// Looks up the cached configuration for a session.
    /// </summary>
    /// <param name="sessionId">Connector session ID.</param>
    /// <param name="configuration">Cached entry when found.</param>
    /// <returns><see langword="true"/> when an entry exists.</returns>
    public bool TryGet(string sessionId, out CachedSerialConfiguration? configuration)
    {
        lock (_gate)
        {
            return _bySessionId.TryGetValue(sessionId, out configuration);
        }
    }

    /// <summary>
    /// Removes the cache entry for a session if present.
    /// </summary>
    /// <param name="sessionId">Connector session ID to remove.</param>
    public void Remove(string sessionId)
    {
        lock (_gate)
        {
            _bySessionId.Remove(sessionId);
        }
    }

    /// <summary>Removes all cached configurations (for example on listener shutdown).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _bySessionId.Clear();
        }
    }
}
