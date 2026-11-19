// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    FrameReader.cs
//
// Purpose:
// Provides asynchronous reading and validation of TechConnect protocol frames
// from an unowned byte stream, including fragmented and multi-frame reads.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;

namespace TechConnect.Protocol.Framing;

/// <summary>
/// Asynchronous frame reader over an unowned <see cref="Stream"/>.
/// </summary>
/// <remarks>
/// <para>
/// The reader does not dispose the underlying stream; the caller retains ownership
/// and lifetime responsibility (typically an authenticated <c>SslStream</c>).
/// </para>
/// <para>
/// A clean EOF before any byte of a new frame causes <see cref="ReadAsync"/> to
/// return <see langword="null"/>. EOF mid-header or mid-payload raises
/// <see cref="ProtocolTruncatedFrameException"/>. Unknown message types,
/// unsupported versions, and oversized payloads are fatal
/// <see cref="ProtocolException"/> subclasses; after any fatal framing error the
/// connection must be terminated without attempting resynchronization.
/// </para>
/// <para>
/// Concurrent calls to <see cref="ReadAsync"/> on the same instance are not
/// supported. The reusable header buffer is instance state.
/// </para>
/// </remarks>
public sealed class FrameReader
{
    /// <summary>
    /// Underlying byte stream from which frames are read; not owned by this reader.
    /// </summary>
    private readonly Stream _stream;

    /// <summary>
    /// Payload size limits applied after the header is parsed and before payload allocation.
    /// </summary>
    private readonly ProtocolLimits _limits;

    /// <summary>
    /// Reusable buffer that receives the fixed 6-byte frame header on each read.
    /// </summary>
    private readonly byte[] _header = new byte[ProtocolConstants.HeaderSize];

    /// <summary>
    /// Initializes a new reader over the specified stream.
    /// </summary>
    /// <param name="stream">Byte stream supplying framed data; not disposed by this instance.</param>
    /// <param name="limits">
    /// Optional payload limits. When <see langword="null"/>,
    /// <see cref="ProtocolLimits.Default"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public FrameReader(Stream stream, ProtocolLimits? limits = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _limits = limits ?? ProtocolLimits.Default;
    }

    /// <summary>
    /// Reads the next complete frame from the stream.
    /// </summary>
    /// <param name="cancellationToken">Token that can cancel the underlying stream reads.</param>
    /// <returns>
    /// The decoded frame, or <see langword="null"/> when a clean EOF occurs before
    /// any byte of a new frame header.
    /// </returns>
    /// <exception cref="ProtocolUnsupportedVersionException">
    /// The header protocol version is not <see cref="ProtocolConstants.ProtocolVersion"/>.
    /// </exception>
    /// <exception cref="ProtocolUnknownMessageTypeException">
    /// The header message-type byte is not a defined <see cref="MessageType"/>.
    /// </exception>
    /// <exception cref="ProtocolPayloadTooLargeException">
    /// The declared payload length exceeds the limit for that message type.
    /// Raised before the payload is allocated or read.
    /// </exception>
    /// <exception cref="ProtocolTruncatedFrameException">
    /// The stream ended after a frame had partially begun (mid-header or mid-payload).
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled during a read.
    /// </exception>
    /// <remarks>
    /// Fragmented underlying reads are reassembled until the full header and, when
    /// present, the full payload are obtained. Multiple frames buffered in a single
    /// underlying read are consumed one frame per call. Payload ownership is
    /// transferred to the caller via <see cref="ProtocolFrame.Payload"/>.
    /// </remarks>
    public async ValueTask<ProtocolFrame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var headerRead = await ReadExactAsync(_header, allowCleanEof: true, cancellationToken)
            .ConfigureAwait(false);
        if (!headerRead)
        {
            return null;
        }

        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(0, 4));
        var messageTypeValue = _header[4];
        var protocolVersion = _header[5];

        if (protocolVersion != ProtocolConstants.ProtocolVersion)
        {
            throw new ProtocolUnsupportedVersionException(protocolVersion);
        }

        if (!Enum.IsDefined(typeof(MessageType), messageTypeValue))
        {
            throw new ProtocolUnknownMessageTypeException(messageTypeValue);
        }

        var messageType = (MessageType)messageTypeValue;
        var maxAllowed = _limits.GetMaxPayloadBytes(messageType);
        if (payloadLength > (uint)maxAllowed)
        {
            throw new ProtocolPayloadTooLargeException(payloadLength, maxAllowed);
        }

        var payload = payloadLength == 0
            ? []
            : new byte[payloadLength];

        if (payloadLength > 0)
        {
            var payloadRead = await ReadExactAsync(payload, allowCleanEof: false, cancellationToken)
                .ConfigureAwait(false);
            if (!payloadRead)
            {
                throw new ProtocolTruncatedFrameException(
                    $"End of stream after reading a complete header but before the {payloadLength}-byte payload completed.");
            }
        }

        return new ProtocolFrame(messageType, protocolVersion, payload);
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes from the stream, looping across partial reads.
    /// </summary>
    /// <param name="buffer">Destination memory that must be filled completely on success.</param>
    /// <param name="allowCleanEof">
    /// When <see langword="true"/>, a zero-byte read with no prior bytes for this buffer
    /// returns <see langword="false"/> (clean EOF). When <see langword="false"/>, any
    /// unexpected EOF raises <see cref="ProtocolTruncatedFrameException"/>.
    /// </param>
    /// <param name="cancellationToken">Token that can cancel the underlying stream read.</param>
    /// <returns>
    /// <see langword="true"/> when the buffer was filled; <see langword="false"/> only when
    /// <paramref name="allowCleanEof"/> is <see langword="true"/> and EOF occurred before
    /// any byte of the buffer was read.
    /// </returns>
    /// <exception cref="ProtocolTruncatedFrameException">
    /// The stream ended after one or more bytes of <paramref name="buffer"/> had already
    /// been read, or at the start of a required (non-clean-EOF) read.
    /// </exception>
    private async ValueTask<bool> ReadExactAsync(
        Memory<byte> buffer,
        bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0 && allowCleanEof)
                {
                    return false;
                }

                throw new ProtocolTruncatedFrameException(
                    offset == 0
                        ? "End of stream while expecting frame data."
                        : $"End of stream after reading {offset} of {buffer.Length} expected byte(s).");
            }

            offset += read;
        }

        return true;
    }
}
