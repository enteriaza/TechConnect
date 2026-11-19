// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionSnapshotAssembler.cs
//
// Purpose:
// Builds ConnectorSessionSnapshot projections for CLI display without holding
// the session registry lock across attachment, serial cache, or baud lookups.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Attachment;

namespace TechConnect.Server.Sessions;

/// <summary>
/// Assembles display snapshots without holding the registry lock across other components.
/// </summary>
/// <remarks>
/// Observations from independent components may differ slightly in time — acceptable for CLI.
/// Does not mutate registry, attachment, cache, or baud tracker state.
/// </remarks>
internal static class ConnectorSessionSnapshotAssembler
{
    /// <summary>
    /// Builds a snapshot for a single registry session.
    /// </summary>
    /// <param name="session">Live registry session to project.</param>
    /// <param name="attachments">Attachment manager for operator/terminal fields.</param>
    /// <param name="serialCache">Serial configuration cache.</param>
    /// <param name="pendingBaud">Outstanding SetBaud tracker.</param>
    /// <param name="utcNow">Clock used to compute <see cref="ConnectorSessionSnapshot.ConnectionDuration"/>.</param>
    /// <returns>Immutable display snapshot with <c>ConnectionState</c> set to <c>Connected</c>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static ConnectorSessionSnapshot Build(
        ConnectorSession session,
        OperatorAttachmentManager attachments,
        ConnectorSerialConfigurationCache serialCache,
        PendingBaudRequestTracker pendingBaud,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(serialCache);
        ArgumentNullException.ThrowIfNull(pendingBaud);

        attachments.TryGetBySessionId(session.SessionId, out var attachment);
        serialCache.TryGet(session.SessionId, out var serial);
        pendingBaud.TryGetByConnectorSessionId(session.SessionId, out var pending);

        var attached = attachment is not null;
        var serialReported = serial is not null;

        return new ConnectorSessionSnapshot
        {
            SessionId = session.SessionId,
            ConnectorIdentity = session.ConnectorIdentity,
            Hostname = session.Hostname,
            ApplicationVersion = session.ApplicationVersion,
            RemoteEndpoint = session.RemoteEndpoint,
            TlsProtocol = session.TlsProtocol,
            ConnectedAtUtc = session.ConnectedAtUtc,
            LastActivityUtc = session.LastActivityUtc,
            ConnectionDuration = utcNow >= session.ConnectedAtUtc
                ? utcNow - session.ConnectedAtUtc
                : TimeSpan.Zero,
            ConnectionState = "Connected",
            OperatorAttached = attached,
            OperatorUsername = attached ? attachment!.Operator?.Username : null,
            AttachedAtUtc = attached ? attachment!.AttachedAtUtc : null,
            TerminalActive = attached && attachment!.IsTerminalActive,
            SerialConfigurationReported = serialReported,
            PortName = serialReported ? serial!.PortName ?? session.PortName : session.PortName,
            BaudRate = serialReported ? serial!.BaudRate : null,
            DataBits = serialReported ? serial!.DataBits : null,
            Parity = serialReported ? serial!.Parity : null,
            StopBits = serialReported ? serial!.StopBits : null,
            Handshake = serialReported ? serial!.Handshake : null,
            SerialReportedAtUtc = serialReported ? serial!.ReportedAtUtc : null,
            BaudRequestPending = pending is not null,
            PendingBaudRate = pending?.RequestedBaudRate
        };
    }

    /// <summary>
    /// Builds snapshots for all sessions currently listed by the registry.
    /// </summary>
    /// <param name="registry">Session registry; <see cref="ConnectorSessionRegistry.List"/> is copied under its lock first.</param>
    /// <param name="attachments">Attachment manager.</param>
    /// <param name="serialCache">Serial configuration cache.</param>
    /// <param name="pendingBaud">Outstanding SetBaud tracker.</param>
    /// <param name="utcNow">Clock for duration computation.</param>
    /// <returns>One snapshot per listed session, or an empty array.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is null.</exception>
    public static IReadOnlyList<ConnectorSessionSnapshot> BuildAll(
        ConnectorSessionRegistry registry,
        OperatorAttachmentManager attachments,
        ConnectorSerialConfigurationCache serialCache,
        PendingBaudRequestTracker pendingBaud,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(registry);
        // List() returns a copy under a short lock — then assemble outside that lock.
        var sessions = registry.List();
        if (sessions.Count == 0)
        {
            return Array.Empty<ConnectorSessionSnapshot>();
        }

        var snapshots = new ConnectorSessionSnapshot[sessions.Count];
        for (var i = 0; i < sessions.Count; i++)
        {
            snapshots[i] = Build(sessions[i], attachments, serialCache, pendingBaud, utcNow);
        }

        return snapshots;
    }
}
