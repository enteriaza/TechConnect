// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteSessionSnapshot.cs
//
// Purpose:
// Point-in-time UI/status snapshot combining serial link, remote session, NOC
// attachment, delivery stats, and last remote error text.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Point-in-time snapshot of remote session status for UI and diagnostics.
/// </summary>
/// <remarks>
/// Published by <see cref="Abstractions.IRemoteSessionService"/>; instances are
/// immutable after construction. Does not include serial payloads.
/// </remarks>
public sealed class RemoteSessionSnapshot
{
    /// <summary>
    /// Physical serial link lifecycle status.
    /// </summary>
    public required SerialLinkStatus SerialStatus { get; init; }

    /// <summary>
    /// Remote transport / session lifecycle status.
    /// </summary>
    public required RemoteSessionState RemoteStatus { get; init; }

    /// <summary>
    /// NOC engineer attachment state for this field session.
    /// </summary>
    public required NocAttachmentState NocStatus { get; init; }

    /// <summary>
    /// Locally generated field-side correlation id. Not a server session id.
    /// </summary>
    public string? ClientSessionId { get; init; }

    /// <summary>
    /// Server-assigned session ID for the current mTLS registration. Cleared on disconnect.
    /// </summary>
    public string? ServerSessionId { get; init; }

    /// <summary>
    /// Serial→remote delivery pipeline counters.
    /// </summary>
    public required RemoteDeliveryStats Delivery { get; init; }

    /// <summary>
    /// Most recent remote error text suitable for UI (never secrets or payloads), or null.
    /// </summary>
    public string? LastRemoteError { get; init; }
}
