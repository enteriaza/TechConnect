// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteSessionState.cs
//
// Purpose:
// Enumerates remote transport / session lifecycle states independent of the
// physical serial port lifecycle.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Remote transport / session lifecycle.
/// </summary>
/// <remarks>
/// Independent of the physical serial port lifecycle tracked by
/// <see cref="SerialLinkStatus"/> and the UI aggregate <see cref="ConnectionStatus"/>.
/// </remarks>
public enum RemoteSessionState
{
    /// <summary>
    /// No remote session workers are active.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// Initial remote connect attempt is in progress.
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// Waiting for the remote service to become available (pre-session / idle wait).
    /// </summary>
    WaitingForRemoteService = 2,

    /// <summary>
    /// Automatic reconnect after a prior session drop.
    /// </summary>
    Reconnecting = 3,

    /// <summary>
    /// Transport connected; waiting for NOC attachment.
    /// </summary>
    ConnectedWaitingForNoc = 4,

    /// <summary>
    /// NOC terminal session is active on the remote side.
    /// </summary>
    NocSessionActive = 5,

    /// <summary>
    /// Disconnect/teardown of the remote session is in progress.
    /// </summary>
    Disconnecting = 6,

    /// <summary>
    /// Remote session has entered a terminal error state for this cycle.
    /// </summary>
    Error = 7
}
