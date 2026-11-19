// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorConnectionState.cs
//
// Purpose:
// Enumerates the TCP/TLS connector connection lifecycle distinct from registry
// session state and operator attachment ownership.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Connector;

/// <summary>
/// Lifecycle state of a <see cref="ConnectorConnection"/> after accept.
/// </summary>
/// <remarks>
/// Orthogonal to <see cref="TechConnect.Server.Sessions.ConnectorSessionState"/> (operator attach)
/// and to registry presence. Administrative disconnect and dispose transition through
/// <see cref="Disconnecting"/> into <see cref="Disconnected"/>.
/// </remarks>
internal enum ConnectorConnectionState
{
    /// <summary>Reserved for pre-authentication tracking; current construction starts at <see cref="Authenticated"/>.</summary>
    Authenticating = 0,

    /// <summary>mTLS handshake succeeded and the connection is tracked for protocol use.</summary>
    Authenticated = 1,

    /// <summary>Teardown has been committed (<see cref="ConnectorConnection.MarkDisconnecting"/> or dispose begun).</summary>
    Disconnecting = 2,

    /// <summary>Resources disposed; connection must not be reused.</summary>
    Disconnected = 3
}
