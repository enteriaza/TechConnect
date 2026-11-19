// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    NocAttachmentState.cs
//
// Purpose:
// Enumerates whether an authorized NOC engineer is attached to the field
// session, driven by server OperatorStatus frames.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Whether an authorized NOC engineer is attached to this field session.
/// </summary>
/// <remarks>
/// Independent of transport connectivity; reconnect does not imply NOC resumption.
/// Driven by server <c>OperatorStatus</c> frames — never inferred from serial traffic.
/// </remarks>
public enum NocAttachmentState
{
    /// <summary>
    /// Remote session inactive / disconnected; no NOC attachment applies.
    /// </summary>
    None = 0,

    /// <summary>
    /// Remote connected; no operator attached yet.
    /// </summary>
    Waiting = 1,

    /// <summary>
    /// Operator attached; terminal mode inactive.
    /// </summary>
    Attached = 2,

    /// <summary>
    /// Operator attached and in terminal mode.
    /// </summary>
    Active = 3
}
