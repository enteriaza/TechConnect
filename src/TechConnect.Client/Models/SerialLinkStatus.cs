// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialLinkStatus.cs
//
// Purpose:
// Enumerates physical serial link lifecycle states for session snapshots,
// independent of remote transport state.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Physical serial link lifecycle status.
/// </summary>
/// <remarks>
/// Independent of <see cref="RemoteSessionState"/>. Used in
/// <see cref="RemoteSessionSnapshot.SerialStatus"/>.
/// </remarks>
public enum SerialLinkStatus
{
    /// <summary>
    /// No open serial port.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// Serial open is in progress.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// Serial port is open and the RX path is expected to be active.
    /// </summary>
    Connected = 2,

    /// <summary>
    /// Serial close is in progress.
    /// </summary>
    Disconnecting = 3,

    /// <summary>
    /// Serial path has faulted.
    /// </summary>
    Error = 4
}
