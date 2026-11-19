// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorAttachment.cs
//
// Purpose:
// Holds per-attachment identity, detach signaling, operator outbound queue, and
// byte counters for one active operator SSH session bound to one connector session.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Attachment;

/// <summary>
/// One active operator↔connector attachment.
/// </summary>
/// <remarks>
/// <para>
/// Authoritative ownership (which operator/session pairs are attached) lives in
/// <see cref="OperatorAttachmentManager"/>. This type owns the detach
/// <see cref="CancellationTokenSource"/>, the outbound <see cref="BoundedByteQueue"/>,
/// and counters; disposing cancels detach, completes and disposes the queue, and
/// disposes the CTS.
/// </para>
/// <para>
/// Detaching via the manager cancels and completes the queue but does not dispose
/// this instance; the CLI/SSH session path owns final <see cref="Dispose"/>.
/// Attachment release is distinct from connector mTLS teardown.
/// </para>
/// <para>
/// Counters and <see cref="IsTerminalActive"/> use interlocked/volatile access and
/// are safe for concurrent updates. <see cref="MarkDetachReason"/> is not synchronized
/// beyond the manager's lock when called from detach paths.
/// </para>
/// </remarks>
internal sealed class OperatorAttachment : IDisposable
{
    /// <summary>
    /// Default operator outbound budget (device RX awaiting SSH write).
    /// Overflow detaches the slow operator rather than blocking the connector reader.
    /// </summary>
    public const int DefaultOperatorOutboundBytes = 256 * 1024;

    /// <summary>Cumulative device→server RX bytes observed for this attachment.</summary>
    private long _bytesFromDevice;

    /// <summary>Cumulative bytes successfully written toward the operator SSH session.</summary>
    private long _bytesToOperator;

    /// <summary>Cumulative operator→server TX bytes observed for this attachment.</summary>
    private long _bytesFromOperator;

    /// <summary>Cumulative bytes successfully written toward the connector/device.</summary>
    private long _bytesToDevice;

    /// <summary>Times <see cref="BoundedByteQueue.TryEnqueue"/> refused due to overflow.</summary>
    private long _operatorQueueOverflows;

    /// <summary>
    /// Creates a new attachment for the given operator and connector session.
    /// </summary>
    /// <param name="operatorSessionId">SSH operator session key used by the manager maps.</param>
    /// <param name="sessionId">Registered connector session ID.</param>
    /// <param name="connectionId">TCP connection ID that currently owns the session.</param>
    /// <param name="hostname">Optional connector-reported hostname snapshot.</param>
    /// <param name="attachedAtUtc">UTC time recorded as <see cref="AttachedAtUtc"/>.</param>
    /// <param name="authenticatedOperator">
    /// Optional authenticated operator identity (Phase 3+); may be null in older call sites.
    /// </param>
    /// <param name="operatorOutboundBytes">
    /// Byte budget for <see cref="ToOperator"/>; defaults to <see cref="DefaultOperatorOutboundBytes"/>.
    /// </param>
    public OperatorAttachment(
        string operatorSessionId,
        string sessionId,
        string connectionId,
        string? hostname,
        DateTimeOffset attachedAtUtc,
        AuthenticatedOperator? authenticatedOperator = null,
        int operatorOutboundBytes = DefaultOperatorOutboundBytes)
    {
        AttachmentId = Guid.NewGuid().ToString("N")[..12];
        OperatorSessionId = operatorSessionId;
        SessionId = sessionId;
        ConnectionId = connectionId;
        Hostname = hostname;
        AttachedAtUtc = attachedAtUtc;
        Operator = authenticatedOperator;
        DetachCts = new CancellationTokenSource();
        ToOperator = new BoundedByteQueue(operatorOutboundBytes);
    }

    /// <summary>Short opaque ID for logs/audit (12 hex chars from a Guid).</summary>
    public string AttachmentId { get; }

    /// <summary>Operator SSH session identifier used as the manager's operator key.</summary>
    public string OperatorSessionId { get; }

    /// <summary>Authenticated SSH operator identity for this attachment (Phase 3+).</summary>
    public AuthenticatedOperator? Operator { get; }

    /// <summary>Registered connector session ID this attachment targets.</summary>
    public string SessionId { get; }

    /// <summary>Connector TCP connection ID validated at attach time.</summary>
    public string ConnectionId { get; }

    /// <summary>Hostname snapshot from the session at attach time, if any.</summary>
    public string? Hostname { get; }

    /// <summary>UTC timestamp when the attachment was created.</summary>
    public DateTimeOffset AttachedAtUtc { get; }

    /// <summary>
    /// Canceled when the attachment is detached or disposed; forwarding loops should observe it.
    /// </summary>
    public CancellationTokenSource DetachCts { get; }

    /// <summary>Bounded queue of device RX awaiting delivery to the operator SSH session.</summary>
    public BoundedByteQueue ToOperator { get; }

    /// <summary>
    /// True while the operator SSH session is in raw serial terminal mode.
    /// When false (CLI mode with attachment kept), device RX is not delivered to SSH.
    /// </summary>
    public bool IsTerminalActive
    {
        get => Volatile.Read(ref _terminalActive) != 0;
        set => Volatile.Write(ref _terminalActive, value ? 1 : 0);
    }

    /// <summary>
    /// First detach reason recorded for this attachment, or <see langword="null"/> if still attached.
    /// </summary>
    public AttachmentDetachReason? DetachReason { get; private set; }

    /// <summary>Backing store for <see cref="IsTerminalActive"/> (0 = inactive, non-zero = active).</summary>
    private int _terminalActive;

    /// <summary>Bytes received from the device for this attachment.</summary>
    public long BytesFromDevice => Interlocked.Read(ref _bytesFromDevice);

    /// <summary>Bytes delivered toward the operator.</summary>
    public long BytesToOperator => Interlocked.Read(ref _bytesToOperator);

    /// <summary>Bytes received from the operator.</summary>
    public long BytesFromOperator => Interlocked.Read(ref _bytesFromOperator);

    /// <summary>Bytes delivered toward the device/connector.</summary>
    public long BytesToDevice => Interlocked.Read(ref _bytesToDevice);

    /// <summary>Operator outbound queue overflow count.</summary>
    public long OperatorQueueOverflows => Interlocked.Read(ref _operatorQueueOverflows);

    /// <summary>Adds to <see cref="BytesFromDevice"/>.</summary>
    /// <param name="count">Non-negative byte count to add.</param>
    public void AddBytesFromDevice(int count) => Interlocked.Add(ref _bytesFromDevice, count);

    /// <summary>Adds to <see cref="BytesToOperator"/>.</summary>
    /// <param name="count">Non-negative byte count to add.</param>
    public void AddBytesToOperator(int count) => Interlocked.Add(ref _bytesToOperator, count);

    /// <summary>Adds to <see cref="BytesFromOperator"/>.</summary>
    /// <param name="count">Non-negative byte count to add.</param>
    public void AddBytesFromOperator(int count) => Interlocked.Add(ref _bytesFromOperator, count);

    /// <summary>Adds to <see cref="BytesToDevice"/>.</summary>
    /// <param name="count">Non-negative byte count to add.</param>
    public void AddBytesToDevice(int count) => Interlocked.Add(ref _bytesToDevice, count);

    /// <summary>Increments <see cref="OperatorQueueOverflows"/>.</summary>
    public void IncrementOverflows() => Interlocked.Increment(ref _operatorQueueOverflows);

    /// <summary>
    /// Records the detach reason if one has not already been set.
    /// </summary>
    /// <param name="reason">Reason for release; ignored when <see cref="DetachReason"/> is already set.</param>
    public void MarkDetachReason(AttachmentDetachReason reason)
    {
        DetachReason ??= reason;
    }

    /// <summary>Dispose gate; 0 = live, 1 = disposed.</summary>
    private int _disposed;

    /// <summary>
    /// Cancels <see cref="DetachCts"/>, completes and disposes <see cref="ToOperator"/>, and disposes the CTS.
    /// </summary>
    /// <remarks>Idempotent. Does not unregister from <see cref="OperatorAttachmentManager"/>.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!DetachCts.IsCancellationRequested)
            {
                DetachCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }

        ToOperator.Complete();
        ToOperator.Dispose();
        DetachCts.Dispose();
    }
}
