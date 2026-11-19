// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorAttachmentManager.cs
//
// Purpose:
// Owns in-memory operator↔connector attachment mappings, attach/detach policy,
// and device-RX enqueue into the attached operator's outbound queue.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Attachment;

/// <summary>
/// Outcome of an attach attempt against <see cref="OperatorAttachmentManager"/>.
/// </summary>
internal enum AttachmentStatus
{
    /// <summary>Attachment created and indexed under both operator and connector session keys.</summary>
    Attached = 0,

    /// <summary>No registered connector session matched the requested session ID.</summary>
    SessionNotFound = 1,

    /// <summary>
    /// Session exists but its current <see cref="ConnectorSession.ConnectionId"/> no longer
    /// matches the connection ID supplied at attach time.
    /// </summary>
    ConnectorUnavailable = 2,

    /// <summary>Another operator already holds the connector session (reject-new; no takeover).</summary>
    ConnectorAlreadyAttached = 3,

    /// <summary>This operator SSH session is already attached to a connector.</summary>
    OperatorAlreadyAttached = 4
}

/// <summary>
/// Result object for <see cref="OperatorAttachmentManager.TryAttach"/>.
/// </summary>
internal sealed class AttachmentResult
{
    /// <summary>
    /// Initializes a result with status, optional attachment, and optional error text.
    /// </summary>
    /// <param name="status">Attach outcome.</param>
    /// <param name="attachment">Live attachment when <paramref name="status"/> is <see cref="AttachmentStatus.Attached"/>.</param>
    /// <param name="error">Operator-facing failure text when attach failed.</param>
    private AttachmentResult(AttachmentStatus status, OperatorAttachment? attachment, string? error)
    {
        Status = status;
        Attachment = attachment;
        Error = error;
    }

    /// <summary>Attach outcome status.</summary>
    public AttachmentStatus Status { get; }

    /// <summary>Created attachment when successful; otherwise null.</summary>
    public OperatorAttachment? Attachment { get; }

    /// <summary>Failure message for CLI display when unsuccessful.</summary>
    public string? Error { get; }

    /// <summary>True when <see cref="Status"/> is <see cref="AttachmentStatus.Attached"/>.</summary>
    public bool Succeeded => Status == AttachmentStatus.Attached;

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <param name="attachment">Newly owned attachment instance.</param>
    /// <returns>Result with <see cref="AttachmentStatus.Attached"/>.</returns>
    public static AttachmentResult Ok(OperatorAttachment attachment) =>
        new(AttachmentStatus.Attached, attachment, null);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <param name="status">Non-success status.</param>
    /// <param name="error">Operator-facing error text.</param>
    /// <returns>Result with null <see cref="Attachment"/>.</returns>
    public static AttachmentResult Fail(AttachmentStatus status, string error) =>
        new(status, null, error);
}

/// <summary>
/// Authoritative in-memory attachment ownership.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees: at most one operator per connector session, and at most one connector
/// per operator SSH session. Attach/detach are atomic under a single lock. Reject-new;
/// no takeover.
/// </para>
/// <para>
/// Detach cancels <see cref="OperatorAttachment.DetachCts"/> and completes
/// <see cref="OperatorAttachment.ToOperator"/>, and restores the registry session
/// state to <see cref="ConnectorSessionState.Connected"/> when the connection ID still
/// matches. It does not dispose the attachment object (CLI owns dispose) and does not
/// terminate the connector mTLS connection.
/// </para>
/// <para>
/// <see cref="TryEnqueueDeviceRx"/> releases the manager lock before touching the queue
/// so connector RX is not serialized behind unrelated attach/detach work beyond the
/// brief lookup.
/// </para>
/// </remarks>
internal sealed class OperatorAttachmentManager
{
    /// <summary>Serializes attach/detach/lookup mutations of both maps.</summary>
    private readonly object _gate = new();

    /// <summary>Attachments keyed by connector session ID (case-insensitive).</summary>
    private readonly Dictionary<string, OperatorAttachment> _bySessionId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attachments keyed by operator SSH session ID (ordinal).</summary>
    private readonly Dictionary<string, OperatorAttachment> _byOperatorSessionId =
        new(StringComparer.Ordinal);

    /// <summary>Registry used to validate sessions and update <see cref="ConnectorSession.State"/>.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Provides <see cref="OperatorAttachment.AttachedAtUtc"/> timestamps.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a manager bound to the given session registry and clock.
    /// </summary>
    /// <param name="sessionRegistry">Registry of Hello-registered connector sessions.</param>
    /// <param name="timeProvider">Clock for attach timestamps.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public OperatorAttachmentManager(ConnectorSessionRegistry sessionRegistry, TimeProvider timeProvider)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Number of currently attached operator↔connector pairs.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bySessionId.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to attach an operator SSH session to a registered connector session.
    /// </summary>
    /// <param name="operatorSessionId">Operator SSH session key.</param>
    /// <param name="sessionId">Connector session ID to attach.</param>
    /// <param name="connectionId">
    /// Expected current connection ID; must match the registry entry or attach fails as unavailable.
    /// </param>
    /// <param name="authenticatedOperator">Optional authenticated operator identity snapshot.</param>
    /// <param name="operatorOutboundBytes">Outbound queue budget for the new attachment.</param>
    /// <returns>Success with attachment, or a failed <see cref="AttachmentResult"/>.</returns>
    /// <exception cref="ArgumentException">A required ID is null/whitespace.</exception>
    /// <remarks>
    /// On success sets the registry session state to <see cref="ConnectorSessionState.Attached"/>.
    /// </remarks>
    public AttachmentResult TryAttach(
        string operatorSessionId,
        string sessionId,
        string connectionId,
        AuthenticatedOperator? authenticatedOperator = null,
        int operatorOutboundBytes = OperatorAttachment.DefaultOperatorOutboundBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (_byOperatorSessionId.ContainsKey(operatorSessionId))
            {
                return AttachmentResult.Fail(
                    AttachmentStatus.OperatorAlreadyAttached,
                    "This SSH session is already attached to a connector. Use 'disconnect' to release first.");
            }

            if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
            {
                return AttachmentResult.Fail(
                    AttachmentStatus.SessionNotFound,
                    $"% Session '{sessionId}' not found.");
            }

            if (!string.Equals(session.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                return AttachmentResult.Fail(
                    AttachmentStatus.ConnectorUnavailable,
                    "% Connector connection is no longer available.");
            }

            if (_bySessionId.ContainsKey(session.SessionId))
            {
                return AttachmentResult.Fail(
                    AttachmentStatus.ConnectorAlreadyAttached,
                    "% Connector is already attached by another operator.");
            }

            var attachment = new OperatorAttachment(
                operatorSessionId,
                session.SessionId,
                connectionId,
                session.Hostname,
                _timeProvider.GetUtcNow(),
                authenticatedOperator,
                operatorOutboundBytes);

            _bySessionId[session.SessionId] = attachment;
            _byOperatorSessionId[operatorSessionId] = attachment;
            session.State = ConnectorSessionState.Attached;
            return AttachmentResult.Ok(attachment);
        }
    }

    /// <summary>
    /// Looks up the attachment for a connector session ID.
    /// </summary>
    /// <param name="sessionId">Connector session ID (case-insensitive).</param>
    /// <param name="attachment">Matching attachment when found.</param>
    /// <returns><see langword="true"/> when an attachment exists.</returns>
    public bool TryGetBySessionId(string sessionId, out OperatorAttachment? attachment)
    {
        lock (_gate)
        {
            return _bySessionId.TryGetValue(sessionId, out attachment);
        }
    }

    /// <summary>
    /// Looks up the attachment for an operator SSH session ID.
    /// </summary>
    /// <param name="operatorSessionId">Operator session key (ordinal).</param>
    /// <param name="attachment">Matching attachment when found.</param>
    /// <returns><see langword="true"/> when an attachment exists.</returns>
    public bool TryGetByOperatorSessionId(string operatorSessionId, out OperatorAttachment? attachment)
    {
        lock (_gate)
        {
            return _byOperatorSessionId.TryGetValue(operatorSessionId, out attachment);
        }
    }

    /// <summary>
    /// Enqueues device RX for the attached operator when terminal mode is active.
    /// </summary>
    /// <param name="sessionId">Connector session that produced the RX.</param>
    /// <param name="data">
    /// Caller-owned device payload (typically a <c>FrameReader</c> SerialData array).
    /// On <see cref="DeviceRxEnqueueResult.Enqueued"/> the outbound queue takes ownership and
    /// the caller must not mutate or reuse the array. On any other result the caller retains
    /// ownership (and may discard).
    /// </param>
    /// <returns>
    /// <see cref="DeviceRxEnqueueResult.Enqueued"/> on success;
    /// <see cref="DeviceRxEnqueueResult.NoAttachment"/> when none;
    /// <see cref="DeviceRxEnqueueResult.Suppressed"/> when attached but not in terminal mode;
    /// <see cref="DeviceRxEnqueueResult.Overflow"/> when the outbound queue refused the chunk
    /// (caller should detach for backpressure).
    /// </returns>
    /// <remarks>
    /// Overflow → caller should detach. No attachment / CLI-only attachment → discard quietly.
    /// Does not terminate the connector connection. Uses ownership transfer into
    /// <see cref="BoundedByteQueue"/> to avoid a second payload copy (PERF-SRV-01).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    public DeviceRxEnqueueResult TryEnqueueDeviceRx(string sessionId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        OperatorAttachment? attachment;
        lock (_gate)
        {
            if (!_bySessionId.TryGetValue(sessionId, out attachment))
            {
                return DeviceRxEnqueueResult.NoAttachment;
            }
        }

        if (data.Length == 0)
        {
            return DeviceRxEnqueueResult.Enqueued;
        }

        // Attached but at CLI (after Ctrl+]): do not deliver RX or fill the outbound queue.
        if (!attachment.IsTerminalActive)
        {
            return DeviceRxEnqueueResult.Suppressed;
        }

        attachment.AddBytesFromDevice(data.Length);
        if (attachment.ToOperator.TryEnqueueOwned(data))
        {
            return DeviceRxEnqueueResult.Enqueued;
        }

        attachment.IncrementOverflows();
        return DeviceRxEnqueueResult.Overflow;
    }

    /// <summary>
    /// Detaches by operator SSH session ID.
    /// </summary>
    /// <param name="operatorSessionId">Operator session key.</param>
    /// <param name="reason">Detach reason recorded on the attachment.</param>
    /// <param name="attachment">Detached attachment when found.</param>
    /// <returns><see langword="true"/> when an attachment was removed.</returns>
    public bool TryDetachByOperatorSessionId(
        string operatorSessionId,
        AttachmentDetachReason reason,
        out OperatorAttachment? attachment)
    {
        lock (_gate)
        {
            if (!_byOperatorSessionId.TryGetValue(operatorSessionId, out attachment))
            {
                return false;
            }

            Remove_NoLock(attachment, reason);
            return true;
        }
    }

    /// <summary>
    /// Detaches by connector session ID.
    /// </summary>
    /// <param name="sessionId">Connector session ID.</param>
    /// <param name="reason">Detach reason recorded on the attachment.</param>
    /// <param name="attachment">Detached attachment when found.</param>
    /// <returns><see langword="true"/> when an attachment was removed.</returns>
    public bool TryDetachBySessionId(
        string sessionId,
        AttachmentDetachReason reason,
        out OperatorAttachment? attachment)
    {
        lock (_gate)
        {
            if (!_bySessionId.TryGetValue(sessionId, out attachment))
            {
                return false;
            }

            Remove_NoLock(attachment, reason);
            return true;
        }
    }

    /// <summary>
    /// Detaches the attachment whose <see cref="OperatorAttachment.ConnectionId"/> matches.
    /// </summary>
    /// <param name="connectionId">TCP connection ID (ordinal match).</param>
    /// <param name="reason">Detach reason recorded on the attachment.</param>
    /// <param name="attachment">Detached attachment when found.</param>
    /// <returns><see langword="true"/> when an attachment was removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is null/whitespace.</exception>
    /// <remarks>
    /// Used on connector disconnect so only the matching connection's attachment is released.
    /// </remarks>
    public bool TryDetachByConnectionId(
        string connectionId,
        AttachmentDetachReason reason,
        out OperatorAttachment? attachment)
    {
        attachment = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            foreach (var candidate in _bySessionId.Values)
            {
                if (string.Equals(candidate.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    attachment = candidate;
                    Remove_NoLock(candidate, reason);
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Detaches every attachment (for example during listener shutdown).
    /// </summary>
    /// <param name="reason">Reason applied to each attachment; defaults to <see cref="AttachmentDetachReason.Shutdown"/>.</param>
    public void ClearAll(AttachmentDetachReason reason = AttachmentDetachReason.Shutdown)
    {
        lock (_gate)
        {
            var all = _bySessionId.Values.ToArray();
            foreach (var attachment in all)
            {
                Remove_NoLock(attachment, reason);
            }
        }
    }

    /// <summary>
    /// Removes both index entries, restores session state when safe, cancels detach, and completes the queue.
    /// </summary>
    /// <param name="attachment">Attachment being removed; must already be present in the maps.</param>
    /// <param name="reason">Reason recorded via <see cref="OperatorAttachment.MarkDetachReason"/>.</param>
    /// <remarks>Caller must hold the manager gate. Does not dispose <paramref name="attachment"/>.</remarks>
    private void Remove_NoLock(OperatorAttachment attachment, AttachmentDetachReason reason)
    {
        attachment.MarkDetachReason(reason);
        _bySessionId.Remove(attachment.SessionId);
        _byOperatorSessionId.Remove(attachment.OperatorSessionId);

        if (_sessionRegistry.TryGetBySessionId(attachment.SessionId, out var session) && session is not null)
        {
            if (string.Equals(session.ConnectionId, attachment.ConnectionId, StringComparison.Ordinal))
            {
                session.State = ConnectorSessionState.Connected;
            }
        }

        try
        {
            if (!attachment.DetachCts.IsCancellationRequested)
            {
                attachment.DetachCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        attachment.ToOperator.Complete();
    }
}

/// <summary>
/// Result of attempting to enqueue device RX for an attached operator.
/// </summary>
internal enum DeviceRxEnqueueResult
{
    /// <summary>Bytes accepted into the operator outbound queue (or empty payload treated as success).</summary>
    Enqueued = 0,

    /// <summary>No operator is attached to the connector session; RX discarded.</summary>
    NoAttachment = 1,

    /// <summary>Outbound queue refused the chunk; caller should detach for backpressure.</summary>
    Overflow = 2,

    /// <summary>Attached but not in terminal mode — RX discarded for operator delivery.</summary>
    Suppressed = 3
}
