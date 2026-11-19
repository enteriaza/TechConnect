// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    XmodemReceiver.cs
//
// Purpose:
// Receives classic XMODEM (checksum/CRC, 128-byte and 1K blocks) from the SSH
// terminal and emits only file payload bytes, retaining final-block padding.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Copy;

/// <summary>
/// XMODEM receiver (checksum and CRC-16, 128-byte and 1K blocks).
/// Emits only file payload bytes — never framing, padding is retained for classic XMODEM
/// (no file-size metadata); callers that need trim should use YMODEM.
/// </summary>
/// <remarks>
/// Classic XMODEM lacks standard file-size metadata, so the final block's padding
/// (often <c>0x1A</c>) is forwarded unchanged because those bytes may be legitimate
/// payload. Control bytes (ACK/NAK/CAN/<c>C</c>) are written only to the SSH
/// terminal callbacks, not into the payload destination.
/// </remarks>
internal sealed class XmodemReceiver
{
    /// <summary>Start of 128-byte header.</summary>
    public const byte SoH = 0x01;

    /// <summary>Start of 1024-byte (1K) header.</summary>
    public const byte Stx = 0x02;

    /// <summary>End of transmission.</summary>
    public const byte Eot = 0x04;

    /// <summary>Positive acknowledgment.</summary>
    public const byte Ack = 0x06;

    /// <summary>Negative acknowledgment / checksum handshake probe.</summary>
    public const byte Nak = 0x15;

    /// <summary>Cancel.</summary>
    public const byte Can = 0x18;

    /// <summary>CRC-mode handshake probe (<c>'C'</c>).</summary>
    public const byte C = (byte)'C';

    /// <summary>Maximum consecutive errors before canceling the transfer.</summary>
    public const int MaxRetries = 10;

    /// <summary>Per-byte read timeout during a block (milliseconds).</summary>
    public const int ByteTimeoutMs = 3000;

    /// <summary>Handshake probe timeout waiting for the first block header (milliseconds).</summary>
    public const int HandshakeTimeoutMs = 3000;

    /// <summary>Reads one byte from the sender (SSH terminal), or null on EOF.</summary>
    private readonly Func<CancellationToken, Task<byte?>> _readByteAsync;

    /// <summary>Writes control bytes back to the sender.</summary>
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _writeAsync;

    /// <summary>
    /// Creates a receiver bound to SSH terminal byte I/O callbacks.
    /// </summary>
    /// <param name="readByteAsync">Async single-byte reader.</param>
    /// <param name="writeAsync">Async writer for ACK/NAK/CAN/<c>C</c>.</param>
    /// <exception cref="ArgumentNullException">A callback is null.</exception>
    public XmodemReceiver(
        Func<CancellationToken, Task<byte?>> readByteAsync,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeAsync)
    {
        _readByteAsync = readByteAsync ?? throw new ArgumentNullException(nameof(readByteAsync));
        _writeAsync = writeAsync ?? throw new ArgumentNullException(nameof(writeAsync));
    }

    /// <summary>
    /// Receives an XMODEM transfer, writing only data-block payloads to <paramref name="payloadDestination"/>.
    /// </summary>
    /// <param name="payloadDestination">Destination for raw file payload bytes (including padding).</param>
    /// <param name="cancellationToken">Cancels the receive loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payloadDestination"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Timeout, framing error, cancel, or integrity failure.</exception>
    public async Task ReceiveAsync(
        Stream payloadDestination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadDestination);

        var useCrc = await HandshakeAsync(cancellationToken).ConfigureAwait(false);
        byte expectedBlock = 1;
        var consecutiveErrors = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (header is null)
            {
                consecutiveErrors++;
                if (consecutiveErrors > MaxRetries)
                {
                    await CancelAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("% XMODEM receive timed out.");
                }

                await WriteControlAsync(useCrc ? C : Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (header.Value == Can)
            {
                // Second CAN confirms cancel.
                var second = await ReadByteWithTimeoutAsync(500, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("% XMODEM transfer cancelled by sender.");
            }

            if (header.Value == Eot)
            {
                await WriteControlAsync(Ack, cancellationToken).ConfigureAwait(false);
                return;
            }

            int blockSize;
            if (header.Value == SoH)
            {
                blockSize = 128;
            }
            else if (header.Value == Stx)
            {
                blockSize = 1024;
            }
            else
            {
                consecutiveErrors++;
                if (consecutiveErrors > MaxRetries)
                {
                    await CancelAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("% XMODEM framing error.");
                }

                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var blk = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken).ConfigureAwait(false);
            var blkComp = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (blk is null || blkComp is null)
            {
                consecutiveErrors++;
                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if ((byte)(blk.Value ^ 0xFF) != blkComp.Value)
            {
                consecutiveErrors++;
                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var data = new byte[blockSize];
            var filled = 0;
            while (filled < blockSize)
            {
                var b = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (b is null)
                {
                    break;
                }

                data[filled++] = b.Value;
            }

            if (filled != blockSize)
            {
                consecutiveErrors++;
                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            bool integrityOk;
            if (useCrc)
            {
                var crcHi = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                var crcLo = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                if (crcHi is null || crcLo is null)
                {
                    integrityOk = false;
                }
                else
                {
                    var expected = (ushort)((crcHi.Value << 8) | crcLo.Value);
                    integrityOk = Crc16(data) == expected;
                }
            }
            else
            {
                var sum = await ReadByteWithTimeoutAsync(ByteTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                integrityOk = sum is not null && Checksum(data) == sum.Value;
            }

            if (!integrityOk)
            {
                consecutiveErrors++;
                if (consecutiveErrors > MaxRetries)
                {
                    await CancelAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("% XMODEM integrity check failed.");
                }

                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (blk.Value == expectedBlock)
            {
                await payloadDestination.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                expectedBlock++;
                consecutiveErrors = 0;
                await WriteControlAsync(Ack, cancellationToken).ConfigureAwait(false);
            }
            else if (blk.Value == (byte)(expectedBlock - 1))
            {
                // Duplicate — ACK and ignore payload.
                consecutiveErrors = 0;
                await WriteControlAsync(Ack, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                consecutiveErrors++;
                await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Negotiates CRC (<c>C</c>) then falls back to checksum (NAK); stashes the first header byte.
    /// </summary>
    /// <param name="cancellationToken">Cancels handshake probes.</param>
    /// <returns>True when CRC mode was negotiated; false for checksum mode.</returns>
    /// <exception cref="InvalidOperationException">Sender never responded with a block header.</exception>
    private async Task<bool> HandshakeAsync(CancellationToken cancellationToken)
    {
        // Prefer CRC: send 'C' several times, then fall back to NAK (checksum).
        for (var i = 0; i < 6; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteControlAsync(C, cancellationToken).ConfigureAwait(false);
            var b = await ReadByteWithTimeoutAsync(HandshakeTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (b is SoH or Stx or Eot or Can)
            {
                // First block header already consumed — push back via single-byte pending not available.
                // Caller loop expects to read header next; we re-process by writing NAK/C incorrectly.
                // Instead: stash in a one-byte pushback.
                _pushback = b;
                return true;
            }
        }

        for (var i = 0; i < MaxRetries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteControlAsync(Nak, cancellationToken).ConfigureAwait(false);
            var b = await ReadByteWithTimeoutAsync(HandshakeTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (b is SoH or Stx or Eot or Can)
            {
                _pushback = b;
                return false;
            }
        }

        throw new InvalidOperationException(
            "% XMODEM sender did not respond. Use a terminal client that supports XMODEM upload.");
    }

    /// <summary>Single-byte pushback used after handshake consumes the first header.</summary>
    private byte? _pushback;

    /// <summary>
    /// Reads one byte with a timeout, honoring pushback; timeout returns null without canceling the parent token.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="cancellationToken">Parent cancellation.</param>
    /// <returns>Byte value, or null on timeout/EOF.</returns>
    private async Task<byte?> ReadByteWithTimeoutAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (_pushback is { } pb)
        {
            _pushback = null;
            return pb;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await _readByteAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a single control byte to the SSH terminal.
    /// </summary>
    /// <param name="value">Control byte.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Task that completes when the write finishes.</returns>
    private Task WriteControlAsync(byte value, CancellationToken cancellationToken) =>
        _writeAsync(new[] { value }, cancellationToken);

    /// <summary>
    /// Sends two CAN bytes to abort the sender; write failures are ignored.
    /// </summary>
    /// <param name="cancellationToken">Cancels the cancel write.</param>
    private async Task CancelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writeAsync(new byte[] { Can, Can }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Computes the classic XMODEM 8-bit additive checksum of a data block.
    /// </summary>
    /// <param name="data">Block payload.</param>
    /// <returns>Checksum byte.</returns>
    internal static byte Checksum(ReadOnlySpan<byte> data)
    {
        var sum = 0;
        foreach (var b in data)
        {
            sum = (sum + b) & 0xFF;
        }

        return (byte)sum;
    }

    /// <summary>
    /// Computes the XMODEM CRC-16 (poly 0x1021) of a data block.
    /// </summary>
    /// <param name="data">Block payload.</param>
    /// <returns>CRC-16 value.</returns>
    internal static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (ushort)((crc << 1) ^ 0x1021);
                }
                else
                {
                    crc <<= 1;
                }
            }
        }

        return crc;
    }
}
