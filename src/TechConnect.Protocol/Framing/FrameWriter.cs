// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    FrameWriter.cs
//
// Purpose:
// Provides asynchronous writing of TechConnect protocol frames to an unowned
// byte stream, serializing concurrent writers so headers and payloads never
// interleave.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;

namespace TechConnect.Protocol.Framing;

/// <summary>
/// Asynchronous frame writer over an unowned <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// Concurrent <see cref="WriteAsync(MessageType, ReadOnlyMemory{byte}, CancellationToken)"/>
/// calls are serialized with an internal gate so a frame's header and payload are
/// never interleaved with another frame. The writer does not dispose the
/// underlying stream.
/// </para>
/// <para>
/// Disposing this instance releases the write gate only; the caller's stream
/// remains open. After disposal, write attempts throw <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
public sealed class FrameWriter : IAsyncDisposable
{
    /// <summary>
    /// Underlying byte stream that receives framed output; not owned by this writer.
    /// </summary>
    private readonly Stream _stream;

    /// <summary>
    /// Payload size limits checked before any bytes are written.
    /// </summary>
    private readonly ProtocolLimits _limits;

    /// <summary>
    /// Gate that serializes concurrent frame writes so headers and payloads never interleave.
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>
    /// Reusable buffer used to assemble the fixed 6-byte frame header for each write.
    /// </summary>
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];

    /// <summary>
    /// Indicates whether <see cref="DisposeAsync"/> has released the write gate.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new writer over the specified stream.
    /// </summary>
    /// <param name="stream">Byte stream that receives framed data; not disposed by this instance.</param>
    /// <param name="limits">
    /// Optional payload limits. When <see langword="null"/>,
    /// <see cref="ProtocolLimits.Default"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public FrameWriter(Stream stream, ProtocolLimits? limits = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _limits = limits ?? ProtocolLimits.Default;
    }

    /// <summary>
    /// Writes a complete frame (header plus optional payload) to the stream.
    /// </summary>
    /// <param name="messageType">Message type written into the frame header.</param>
    /// <param name="payload">
    /// Payload bytes to transmit. The memory is read during the call and is not
    /// retained after the write completes. Empty payloads produce a header-only frame.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that can cancel waiting for the write gate or the underlying stream write.
    /// Cancellation may leave a partially written frame on the stream.
    /// </param>
    /// <returns>A task that completes when the frame has been written and flushed.</returns>
    /// <exception cref="ObjectDisposedException">The writer has been disposed.</exception>
    /// <exception cref="ProtocolUnknownMessageTypeException">
    /// <paramref name="messageType"/> is not a defined <see cref="MessageType"/> value.
    /// </exception>
    /// <exception cref="ProtocolPayloadTooLargeException">
    /// <paramref name="payload"/> exceeds the limit for <paramref name="messageType"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled.
    /// </exception>
    /// <remarks>
    /// The header encodes payload length as a big-endian <see cref="uint"/>,
    /// the message type byte, and <see cref="ProtocolConstants.ProtocolVersion"/>.
    /// The stream is flushed after each successful frame write.
    /// </remarks>
    public async Task WriteAsync(
        MessageType messageType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Enum.IsDefined(messageType))
        {
            throw new ProtocolUnknownMessageTypeException((byte)messageType);
        }

        var maxAllowed = _limits.GetMaxPayloadBytes(messageType);
        if (payload.Length > maxAllowed)
        {
            throw new ProtocolPayloadTooLargeException((uint)payload.Length, maxAllowed);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(0, 4), (uint)payload.Length);
            _header[4] = (byte)messageType;
            _header[5] = ProtocolConstants.ProtocolVersion;

            await _stream.WriteAsync(_header.AsMemory(0, ProtocolConstants.HeaderSize), cancellationToken)
                .ConfigureAwait(false);

            if (!payload.IsEmpty)
            {
                await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Writes a complete frame using a byte-array payload.
    /// </summary>
    /// <param name="messageType">Message type written into the frame header.</param>
    /// <param name="payload">Payload bytes; wrapped as memory and not retained after the write.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written and flushed.</returns>
    /// <remarks>
    /// Delegates to <see cref="WriteAsync(MessageType, ReadOnlyMemory{byte}, CancellationToken)"/>.
    /// A <see langword="null"/> <paramref name="payload"/> causes a
    /// <see cref="NullReferenceException"/> when converted to memory.
    /// </remarks>
    public Task WriteAsync(
        MessageType messageType,
        byte[] payload,
        CancellationToken cancellationToken = default) =>
        WriteAsync(messageType, payload.AsMemory(), cancellationToken);

    /// <summary>
    /// Releases the write gate and marks the writer disposed without disposing the stream.
    /// </summary>
    /// <returns>A completed value task.</returns>
    /// <remarks>
    /// Idempotent: subsequent calls are no-ops. The underlying stream remains usable
    /// by the caller.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writeGate.Dispose();
        // Stream is not owned.
        return ValueTask.CompletedTask;
    }
}
