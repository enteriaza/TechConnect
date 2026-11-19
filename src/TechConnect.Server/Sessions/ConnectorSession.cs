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

    /// <summary>UTC time of last framed protocol activity for this connection, if tracked.</summary>
    public DateTimeOffset? LastActivityUtc { get; set; }

    /// <summary>Negotiated TLS protocol name string recorded at registration, if any.</summary>
    public string? TlsProtocol { get; init; }

    /// <summary>
    /// Attachment state: <see cref="ConnectorSessionState.Connected"/> or
    /// <see cref="ConnectorSessionState.Attached"/>.
    /// </summary>
    public ConnectorSessionState State { get; set; } = ConnectorSessionState.Connected;
}
