// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    NoOpAuditEventWriter.cs
//
// Purpose:
// No-op audit writer used when audit persistence is disabled or omitted from DI
// (including unit tests).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Audit;

/// <summary>
/// No-op writer used when tests omit DI registration or audit writing is disabled.
/// </summary>
/// <remarks>
/// <see cref="IsEnabled"/> is always <see langword="false"/>. <see cref="TryWrite"/> discards all events.
/// Thread-safe (stateless).
/// </remarks>
internal sealed class NoOpAuditEventWriter : IAuditEventWriter
{
    /// <summary>
    /// Always <see langword="false"/> for the no-op writer.
    /// </summary>
    public bool IsEnabled => false;

    /// <summary>
    /// Discards the event without storing or throwing.
    /// </summary>
    /// <param name="type">Event type (ignored).</param>
    /// <param name="outcome">Outcome (ignored).</param>
    /// <param name="description">Description (ignored).</param>
    /// <param name="context">Optional context (ignored).</param>
    public void TryWrite(
        AuditEventType type,
        AuditEventOutcome outcome,
        string description,
        AuditEventContext? context = null)
    {
    }
}
