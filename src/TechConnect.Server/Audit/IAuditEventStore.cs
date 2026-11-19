// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IAuditEventStore.cs
//
// Purpose:
// Contracts for writing audit events, reading recent/filtered history, and
// carrying optional context fields into stored records.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Audit;

/// <summary>
/// Best-effort sink for application audit events.
/// </summary>
internal interface IAuditEventWriter
{
    /// <summary>
    /// <see langword="true"/> when writes are retained; <see langword="false"/> for no-op writers.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>Best-effort write. Must not throw to callers under normal conditions.</summary>
    /// <param name="type">Event type discriminator.</param>
    /// <param name="outcome">Outcome classification.</param>
    /// <param name="description">Human-readable description (no secrets).</param>
    /// <param name="context">Optional actor/session/details context.</param>
    void TryWrite(AuditEventType type, AuditEventOutcome outcome, string description, AuditEventContext? context = null);
}

/// <summary>
/// Read API over retained audit events (newest-first snapshots).
/// </summary>
internal interface IAuditEventReader
{
    /// <summary>
    /// <see langword="true"/> when the store retains events for reading.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// <see langword="true"/> when the store has dropped older events due to capacity.
    /// </summary>
    bool HasEvictedEvents { get; }

    /// <summary>
    /// Current number of retained events.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Configured maximum retained events before eviction.
    /// </summary>
    int MaximumEvents { get; }

    /// <summary>Newest first. Returns an immutable snapshot.</summary>
    /// <param name="maxCount">Maximum events to return.</param>
    /// <returns>Snapshot list; empty when none.</returns>
    IReadOnlyList<AuditEvent> GetRecent(int maxCount);

    /// <summary>
    /// Newest-first events matching <paramref name="sessionId"/>.
    /// </summary>
    /// <param name="sessionId">Session id to filter on.</param>
    /// <param name="maxCount">Maximum events to return.</param>
    /// <returns>Immutable snapshot.</returns>
    IReadOnlyList<AuditEvent> GetBySessionId(string sessionId, int maxCount);

    /// <summary>
    /// Newest-first events matching <paramref name="username"/> (actor or requested).
    /// </summary>
    /// <param name="username">Username to filter on.</param>
    /// <param name="maxCount">Maximum events to return.</param>
    /// <returns>Immutable snapshot.</returns>
    IReadOnlyList<AuditEvent> GetByUsername(string username, int maxCount);
}

/// <summary>
/// Optional contextual fields copied onto an <see cref="AuditEvent"/> at write time.
/// </summary>
internal sealed class AuditEventContext
{
    /// <summary>
    /// Authenticated actor username when known.
    /// </summary>
    public string? ActorUsername { get; init; }

    /// <summary>
    /// Username requested during authentication attempts.
    /// </summary>
    public string? RequestedUsername { get; init; }

    /// <summary>
    /// Session correlation id when relevant.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Connector identity when relevant.
    /// </summary>
    public string? ConnectorIdentity { get; init; }

    /// <summary>
    /// Remote endpoint string when relevant.
    /// </summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>
    /// Small non-sensitive metadata bag.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
