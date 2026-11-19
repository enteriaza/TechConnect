// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuditEvent.cs
//
// Purpose:
// Immutable application-level audit record stored in-memory for operator
// history (not tamper-proof or durable).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Audit;

/// <summary>
/// Immutable application-level audit record. In-memory only; not tamper-proof or persistent.
/// </summary>
/// <remarks>
/// Do not place secrets or credentials in <see cref="Description"/> or <see cref="Details"/>.
/// Instances are safe to share across threads once constructed.
/// </remarks>
internal sealed class AuditEvent
{
    /// <summary>
    /// Monotonic sequence number assigned by the store (newest has higher values).
    /// </summary>
    public required long Sequence { get; init; }

    /// <summary>
    /// UTC timestamp when the event was recorded.
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Event type discriminator.
    /// </summary>
    public required AuditEventType EventType { get; init; }

    /// <summary>
    /// Coarse outcome classification.
    /// </summary>
    public required AuditEventOutcome Outcome { get; init; }

    /// <summary>
    /// Authenticated actor username when known.
    /// </summary>
    public string? ActorUsername { get; init; }

    /// <summary>
    /// Username requested during authentication when it may differ from the actor.
    /// </summary>
    public string? RequestedUsername { get; init; }

    /// <summary>
    /// Connector or operator session id when relevant.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Connector identity string when relevant.
    /// </summary>
    public string? ConnectorIdentity { get; init; }

    /// <summary>
    /// Remote endpoint string when relevant (no credentials).
    /// </summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>
    /// Human-readable event description for history display.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>Small non-sensitive metadata (e.g. command name, baud rate).</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }
}
