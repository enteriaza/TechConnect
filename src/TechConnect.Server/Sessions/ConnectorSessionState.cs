// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionState.cs
//
// Purpose:
// Distinguishes a Hello-registered connector session with no operator from one
// that currently has an SSH operator attached for serial forwarding.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// Operator-attachment view of a registered connector session in the registry.
/// </summary>
/// <remarks>
/// This is not the TCP/TLS connection lifecycle (<see cref="TechConnect.Server.Connector.ConnectorConnectionState"/>).
/// Registry presence still implies the connector is connected; attachment is orthogonal.
/// </remarks>
internal enum ConnectorSessionState
{
    /// <summary>Registered after successful Hello; no operator attached.</summary>
    Connected = 0,

    /// <summary>An SSH operator is attached for serial terminal forwarding.</summary>
    Attached = 1
}
