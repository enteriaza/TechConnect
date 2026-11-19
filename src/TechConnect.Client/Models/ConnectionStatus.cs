// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ConnectionStatus.cs
//
// Purpose:
// Aggregate UX lifecycle enum centred on the physical serial session for the
// main form, separate from RemoteSessionState.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Aggregate UX lifecycle centred on the physical serial session.
/// </summary>
/// <remarks>
/// Owned by the WinForms UI layer. Remote TCP/mTLS session state is tracked separately via
/// <see cref="RemoteSessionState"/>. Values such as
/// <see cref="ConnectedWaitingForNoc"/> combine serial-open with coarse remote UX.
/// </remarks>
public enum ConnectionStatus
{
    /// <summary>
    /// No active technician connect cycle; serial session is idle.
    /// </summary>
    Disconnected = 0,

    /// <summary>
    /// Opening the physical serial port (and optionally starting remote coordination).
    /// </summary>
    Connecting = 1,

    /// <summary>
    /// Serial open; waiting for an authorized NOC attachment.
    /// </summary>
    ConnectedWaitingForNoc = 2,

    /// <summary>
    /// Serial open and NOC terminal session considered active for UX.
    /// </summary>
    ConnectedNocSessionActive = 3,

    /// <summary>
    /// Technician-initiated or fault-driven disconnect in progress.
    /// </summary>
    Disconnecting = 4,

    /// <summary>
    /// Terminal error state for the UX connect cycle.
    /// </summary>
    Error = 5
}
