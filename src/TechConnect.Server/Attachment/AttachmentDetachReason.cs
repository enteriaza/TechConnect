// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AttachmentDetachReason.cs
//
// Purpose:
// Enumerates why an operator↔connector attachment was released so callers can
// audit, cancel forwarding, and choose whether the SSH session or connector
// connection should continue.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Attachment;

/// <summary>
/// Reason recorded when an <see cref="OperatorAttachment"/> is detached from the
/// <see cref="OperatorAttachmentManager"/>.
/// </summary>
/// <remarks>
/// Detach releases attachment ownership only. It does not by itself tear down the
/// mTLS connector connection or the operator SSH session; those lifecycles are
/// owned by the connector listener and SSH host respectively. The first reason
/// written via <see cref="OperatorAttachment.MarkDetachReason"/> wins.
/// </remarks>
internal enum AttachmentDetachReason
{
    /// <summary>Operator explicitly released the attachment (for example CLI <c>disconnect</c>).</summary>
    OperatorRequest = 0,

    /// <summary>Operator SSH session ended while still attached.</summary>
    OperatorDisconnect = 1,

    /// <summary>Connector TCP/TLS connection closed or was torn down; attachment released in the listener finally path.</summary>
    ConnectorDisconnect = 2,

    /// <summary>
    /// Device RX could not be enqueued because the operator outbound
    /// <see cref="BoundedByteQueue"/> was full; the slow operator is detached rather than
    /// blocking the connector reader.
    /// </summary>
    OperatorBackpressure = 3,

    /// <summary>Unrecoverable failure while forwarding bytes between operator and connector.</summary>
    FatalForwardingFailure = 4,

    /// <summary>Server or connector listener shutdown cleared all attachments.</summary>
    Shutdown = 5,

    /// <summary>Stale attachment cleaned up after a replacement or superseded mapping.</summary>
    ReplacedCleanup = 6
}
