// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    YmodemReceiver.cs
//
// Purpose:
// Receives YMODEM (block-0 header + CRC data blocks), forwards payload only,
// trims padding when size is known, and rejects multi-file batches.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace TechConnect.Server.Copy;

/// <summary>
/// YMODEM receiver (batch header block 0 + CRC data blocks). Forwards only file payload bytes,
/// trimming final-block padding when file size is known. Rejects multi-file batches.
/// </summary>
/// <remarks>
/// When block 0 advertises a numeric size (and it is at most
/// <see cref="MaxAdvertisedFileSize"/>), only that many payload bytes are written;
/// trailing block padding is discarded. When size is missing or non-numeric, full
/// data blocks are forwarded (padding retained). An empty batch or a second file
/// header after EOT fails the transfer. Successful payload delivery here still
/// does not prove device firmware acceptance.
/// </remarks>
internal sealed class YmodemReceiver
{
    /// <summary>Reads one byte from the sender (SSH terminal), or null on EOF.</summary>
    private readonly Func<CancellationToken, Task<byte?>> _readByteAsync;

    /// <summary>Writes control bytes back to the sender.</summary>
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _writeAsync;

    /// <summary>
    /// Creates a YMODEM receiver bound to SSH terminal byte I/O callbacks.
    /// </summary>
    /// <param name="readByteAsync">Async single-byte reader.</param>
    /// <param name="writeAsync">Async writer for ACK/NAK/CAN/<c>C</c>.</param>
    /// <exception cref="ArgumentNullException">A callback is null.</exception>
    public YmodemReceiver(
        Func<CancellationToken, Task<byte?>> readByteAsync,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeAsync)
    {
        _readByteAsync = readByteAsync ?? throw new ArgumentNullException(nameof(readByteAsync));
        _writeAsync = writeAsync ?? throw new ArgumentNullException(nameof(writeAsync));
    }

    /// <summary>Filename parsed from block 0 when present.</summary>
    public string? Filename { get; private set; }

    /// <summary>
    /// File size parsed from block 0 when present and numeric; used to trim padding.
    /// </summary>
    public long? FileSize { get; private set; }

    /// <summary>
    /// Receives a single-file YMODEM batch into <paramref name="payloadDestination"/>.
    /// </summary>
    /// <param name="payloadDestination">Destination for file payload bytes only.</param>
    /// <param name="cancellationToken">Cancels the receive loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payloadDestination"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Empty batch, missing header, oversized size, timeout, cancel, or multi-file batch.
    /// </exception>
    public async Task ReceiveAsync(Stream payloadDestination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payloadDestination);

        var header = await HandshakeHeaderAsync(cancellationToken).ConfigureAwait(false);

        if (header.IsEmptyBatch)
        {
            await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("% YMODEM batch contained no file.");
        }

        if (header.BlockNumber != 0 || string.IsNullOrEmpty(header.Filename))
        {
            await CancelAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("% YMODEM expected block 0 file header.");
        }

        // Bound advertised size so a malicious header cannot imply unbounded remaining work
        // beyond what packet I/O already limits. We still stream; we never allocate by size.
        if (header.FileSize is > MaxAdvertisedFileSize)
        {
            await CancelAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("% YMODEM file size is unreasonably large.");
        }

        Filename = header.Filename;
        FileSize = header.FileSize;
        await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
        await WriteControlAsync(XmodemReceiver.C, cancellationToken).ConfigureAwait(false);

        long remaining = FileSize ?? long.MaxValue;
        byte expected = 1;
        var consecutiveErrors = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = await ReadBlockAsync(expected, cancellationToken).ConfigureAwait(false);
            if (block is null)
            {
                consecutiveErrors++;
                if (consecutiveErrors > XmodemReceiver.MaxRetries)
                {
                    await CancelAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException("% YMODEM receive timed out.");
                }

                await WriteControlAsync(XmodemReceiver.Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (block.Value.IsEot)
            {
                await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
                // End-of-batch: expect empty block 0; reject additional files.
                await WriteControlAsync(XmodemReceiver.C, cancellationToken).ConfigureAwait(false);
                var next = await ReadBlockAsync(0, cancellationToken).ConfigureAwait(false);
                if (next is { IsEmptyBatch: true })
                {
                    await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (next is { IsEmptyBatch: false, Filename: not null })
                {
                    await CancelAsync(cancellationToken).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        "% YMODEM multi-file batches are not supported for serial copy.");
                }

                // Tolerant: if sender stops after single EOT without empty block 0.
                return;
            }

            if (block.Value.BlockNumber != expected &&
                block.Value.BlockNumber != (byte)(expected - 1))
            {
                consecutiveErrors++;
                await WriteControlAsync(XmodemReceiver.Nak, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (block.Value.BlockNumber == (byte)(expected - 1))
            {
                await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var payload = block.Value.Data;
            if (FileSize is not null)
            {
                var take = (int)Math.Min(payload.Length, remaining);
                if (take > 0)
                {
                    await payloadDestination.WriteAsync(payload.AsMemory(0, take), cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= take;
                }
            }
            else
            {
                await payloadDestination.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            expected++;
            consecutiveErrors = 0;
            await WriteControlAsync(XmodemReceiver.Ack, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Upper bound on YMODEM block-0 advertised size (1 GiB). Prevents absurd metadata.</summary>
    public const long MaxAdvertisedFileSize = 1L << 30;

    /// <summary>Optional multi-byte pushback queue (currently unused by writers; reserved for reads).</summary>
    private readonly Queue<byte> _pushback = new();

    /// <summary>
    /// Probes with <c>C</c> until block 0 arrives or retries are exhausted.
    /// </summary>
    /// <param name="cancellationToken">Cancels handshake.</param>
    /// <returns>Parsed header block.</returns>
    /// <exception cref="InvalidOperationException">Sender did not respond.</exception>
    private async Task<Block> HandshakeHeaderAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < XmodemReceiver.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteControlAsync(XmodemReceiver.C, cancellationToken).ConfigureAwait(false);
            var header = await ReadBlockAsync(expectBlock: 0, cancellationToken).ConfigureAwait(false);
            if (header is not null)
            {
                return header.Value;
            }
        }

        throw new InvalidOperationException(
            "% YMODEM sender did not respond. Use a terminal client that supports YMODEM upload.");
    }

    /// <summary>
    /// Reads one SOH/STX data block (CRC), EOT, or returns null on framing/timeout/CRC failure.
    /// </summary>
    /// <param name="expectBlock">
    /// Expected block number; when 0 and the wire block is 0, parses a YMODEM header.
    /// </param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Parsed block, EOT sentinel, or null to trigger NAK/retry.</returns>
    /// <exception cref="InvalidOperationException">Sender sent CAN.</exception>
    private async Task<Block?> ReadBlockAsync(byte expectBlock, CancellationToken cancellationToken)
    {
        var header = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        if (header.Value == XmodemReceiver.Can)
        {
            throw new InvalidOperationException("% YMODEM transfer cancelled by sender.");
        }

        if (header.Value == XmodemReceiver.Eot)
        {
            return Block.EotBlock();
        }

        int blockSize;
        if (header.Value == XmodemReceiver.SoH)
        {
            blockSize = 128;
        }
        else if (header.Value == XmodemReceiver.Stx)
        {
            blockSize = 1024;
        }
        else
        {
            return null;
        }

        var blk = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var blkComp = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (blk is null || blkComp is null || (byte)(blk.Value ^ 0xFF) != blkComp.Value)
        {
            return null;
        }

        var data = new byte[blockSize];
        for (var i = 0; i < blockSize; i++)
        {
            var b = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
                .ConfigureAwait(false);
            if (b is null)
            {
                return null;
            }

            data[i] = b.Value;
        }

        var crcHi = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        var crcLo = await ReadByteWithTimeoutAsync(XmodemReceiver.ByteTimeoutMs, cancellationToken)
            .ConfigureAwait(false);
        if (crcHi is null || crcLo is null)
        {
            return null;
        }

        var expectedCrc = (ushort)((crcHi.Value << 8) | crcLo.Value);
        if (XmodemReceiver.Crc16(data) != expectedCrc)
        {
            return null;
        }

        if (expectBlock == 0 && blk.Value == 0)
        {
            return Block.FromHeader(data);
        }

        return new Block(blk.Value, data, isEot: false, isEmptyBatch: false, filename: null, fileSize: null);
    }

    /// <summary>
    /// Reads one byte with timeout, draining the pushback queue first.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds.</param>
    /// <param name="cancellationToken">Parent cancellation.</param>
    /// <returns>Byte value, or null on timeout/EOF.</returns>
    private async Task<byte?> ReadByteWithTimeoutAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        if (_pushback.Count > 0)
        {
            return _pushback.Dequeue();
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
            await _writeAsync(new byte[] { XmodemReceiver.Can, XmodemReceiver.Can }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Parsed YMODEM/XMODEM-style block or transfer sentinel.
    /// </summary>
    private readonly struct Block
    {
        /// <summary>
        /// Initializes a block descriptor.
        /// </summary>
        /// <param name="blockNumber">Wire block number.</param>
        /// <param name="data">Block payload bytes.</param>
        /// <param name="isEot">True when this represents EOT.</param>
        /// <param name="isEmptyBatch">True when block 0 is an empty end-of-batch header.</param>
        /// <param name="filename">Filename from block 0 when applicable.</param>
        /// <param name="fileSize">Advertised size from block 0 when parsed.</param>
        public Block(
            byte blockNumber,
            byte[] data,
            bool isEot,
            bool isEmptyBatch,
            string? filename,
            long? fileSize)
        {
            BlockNumber = blockNumber;
            Data = data;
            IsEot = isEot;
            IsEmptyBatch = isEmptyBatch;
            Filename = filename;
            FileSize = fileSize;
        }

        /// <summary>Wire block number.</summary>
        public byte BlockNumber { get; }

        /// <summary>Raw block data (128 or 1024 bytes for data blocks).</summary>
        public byte[] Data { get; }

        /// <summary>True when the sender sent EOT.</summary>
        public bool IsEot { get; }

        /// <summary>True when block 0 is all zeros / empty batch terminator.</summary>
        public bool IsEmptyBatch { get; }

        /// <summary>Filename from a file header block, if any.</summary>
        public string? Filename { get; }

        /// <summary>Parsed size from a file header block, if any.</summary>
        public long? FileSize { get; }

        /// <summary>Creates an EOT sentinel block.</summary>
        /// <returns>Block with <see cref="IsEot"/> set.</returns>
        public static Block EotBlock() => new(0, [], true, false, null, null);

        /// <summary>
        /// Parses a YMODEM block-0 header payload into filename/size or empty-batch.
        /// </summary>
        /// <param name="data">128- or 1024-byte header payload.</param>
        /// <returns>Header block; non-numeric size leaves <see cref="FileSize"/> null.</returns>
        public static Block FromHeader(byte[] data)
        {
            if (data.All(b => b == 0))
            {
                return new Block(0, data, false, true, null, null);
            }

            var nul = Array.IndexOf(data, (byte)0);
            if (nul <= 0)
            {
                return new Block(0, data, false, true, null, null);
            }

            var name = Encoding.ASCII.GetString(data, 0, nul);
            long? size = null;
            var restStart = nul + 1;
            if (restStart < data.Length)
            {
                var end = restStart;
                while (end < data.Length && data[end] is not 0 and not (byte)' ')
                {
                    end++;
                }

                var sizeText = Encoding.ASCII.GetString(data, restStart, end - restStart);
                if (long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed >= 0)
                {
                    size = parsed;
                }
            }

            return new Block(0, data, false, false, name, size);
        }
    }
}
