// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSession.cs
//
// Purpose:
// Mutable in-memory record for a Hello-registered connector, keyed by session ID
// and bound to the validated mTLS client certificate thumbprint as identity.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// Registered connector session. Identity is the validated mTLS client certificate thumbprint.
/// </summary>
/// <remarks>
/// Created by <see cref="ConnectorSessionRegistry.TryRegister"/>. Unregister removes the
/// session from the registry; it does not by itself dispose the underlying
/// <see cref="TechConnect.Server.Connector.ConnectorConnection"/>.
/// <see cref="State"/> is updated by <see cref="TechConnect.Server.Attachment.OperatorAttachmentManager"/>
/// on attach/detach. <see cref="LastActivityUtc"/> is updated by protocol activity touches.
/// </remarks>
internal sealed class ConnectorSession
{
    /// <summary>Allocated 8-character lowercase hex session ID for CLI use.</summary>
    public required string SessionId { get; init; }

    /// <summary>Validated client certificate thumbprint (SHA-1 hex, uppercase).</summary>
    public required string ConnectorIdentity { get; init; }

    /// <summary>Internal TCP connection ID; used for exact cleanup.</summary>
    public required string ConnectionId { get; init; }

    /// <summary>Remote endpoint string observed at registration, if any.</summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>Hostname reported in Hello, trimmed; null when absent/whitespace.</summary>
    public string? Hostname { get; init; }

    /// <summary>Application version reported in Hello, if any.</summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>Serial port name reported in Hello, if any.</summary>
    public string? PortName { get; init; }

    /// <summary>UTC time when the session was registered.</summary>
    public DateTimeOffset ConnectedAtUtc { get; init; }

    /// <summary>
    /// UTC ticks of last framed protocol activity (<see cref="DateTimeOffset.UtcTicks"/>),
    /// or 0 when unset. Updated without the registry gate on the connector RX hot path (PERF-SRV-02).
    /// </summary>
    private long _lastActivityUtcTicks;

    /// <summary>UTC time of last framed protocol activity for this connection, if tracked.</summary>
    /// <remarks>
    /// Stored as UTC ticks with <see cref="Volatile"/> / <see cref="Interlocked"/> so activity
    /// touches do not require <see cref="ConnectorSessionRegistry"/>'s global lock (PERF-SRV-02).
    /// <see cref="ObserveActivity"/> advances the timestamp monotonically under concurrent writers.
    /// Display/snapshot readers may observe a slightly stale value.
    /// </remarks>
    public DateTimeOffset? LastActivityUtc
    {
        get
        {
            var ticks = Volatile.Read(ref _lastActivityUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        set => Volatile.Write(ref _lastActivityUtcTicks, value?.UtcTicks ?? 0);
    }

    /// <summary>
    /// Records protocol activity at <paramref name="utcNow"/> if it is strictly newer than the
    /// current <see cref="LastActivityUtc"/>.
    /// </summary>
    /// <param name="utcNow">Candidate activity timestamp (UTC).</param>
    /// <remarks>
    /// Safe for concurrent callers (connector RX hot path and <c>TouchActivity</c>). Never moves
    /// the stored timestamp backwards.
    /// </remarks>
    public void ObserveActivity(DateTimeOffset utcNow)
    {
        var ticks = utcNow.UtcTicks;
        if (ticks <= 0)
        {
            return;
        }

        while (true)
        {
            var current = Volatile.Read(ref _lastActivityUtcTicks);
            if (ticks <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastActivityUtcTicks, ticks, current) == current)
            {
                return;
            }
        }
    }

    /// <summary>Negotiated TLS protocol name string recorded at registration, if any.</summary>
    public string? TlsProtocol { get; init; }

    /// <summary>
    /// Attachment state: <see cref="ConnectorSessionState.Connected"/> or
    /// <see cref="ConnectorSessionState.Attached"/>.
    /// </summary>
    public ConnectorSessionState State { get; set; } = ConnectorSessionState.Connected;
}
