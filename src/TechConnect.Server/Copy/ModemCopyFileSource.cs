// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ModemCopyFileSource.cs
//
// Purpose:
// Starts concurrent XMODEM/YMODEM receive on the operator SSH terminal and
// exposes only file payload bytes as a streaming ICopyFileSource.
//
// -----------------------------------------------------------------------------

using System.IO.Pipelines;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Copy;

/// <summary>
/// Receives XMODEM/YMODEM from the operator SSH terminal and exposes payload as a stream.
/// Receive runs concurrently so payload bytes can be written to serial without buffering the whole file.
/// </summary>
/// <remarks>
/// Framing/control bytes stay on the SSH terminal path; only file payload is written
/// through <see cref="Content"/>. <see cref="ContentLength"/> is null at open time
/// for both schemes. Classic XMODEM has no standard file-size metadata (padding is
/// retained). YMODEM may trim using size inside <see cref="YmodemReceiver"/> when
/// the header advertises a size, but that length is not surfaced here as
/// <see cref="ContentLength"/>.
/// </remarks>
internal sealed class ModemCopyFileSource : ICopyFileSource
{
    /// <summary>Pipe reader stream of payload bytes.</summary>
    private readonly Stream _stream;

    /// <summary>Advertised length when known; always null for this opener.</summary>
    private readonly long? _length;

    /// <summary>
    /// Creates a modem source wrapping a payload pipe reader.
    /// </summary>
    /// <param name="displayName">Display label such as <c>xmodem:</c> or <c>ymodem:</c>.</param>
    /// <param name="length">Optional content length (unused by current open path).</param>
    /// <param name="stream">Readable payload stream.</param>
    private ModemCopyFileSource(string displayName, long? length, Stream stream)
    {
        DisplayName = displayName;
        _length = length;
        _stream = stream;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public long? ContentLength => _length;

    /// <inheritdoc />
    public Stream Content => _stream;

    /// <summary>
    /// Prompts the operator, starts background receive, and returns a streaming source.
    /// </summary>
    /// <param name="kind"><see cref="CopySourceKind.Xmodem"/> or <see cref="CopySourceKind.Ymodem"/>.</param>
    /// <param name="readByteAsync">Reads one byte from the SSH terminal (or null on EOF).</param>
    /// <param name="writeAsync">Writes control/ACK bytes back to the SSH terminal.</param>
    /// <param name="writeTextAsync">Writes operator instructions as text on the SSH session.</param>
    /// <param name="logger">Logger for receive failures.</param>
    /// <param name="cancellationToken">Cancels the prompt/receive.</param>
    /// <returns>Source whose <see cref="Content"/> yields payload as it arrives.</returns>
    /// <exception cref="ArgumentNullException">A required callback is null.</exception>
    public static async Task<ICopyFileSource> OpenAsync(
        CopySourceKind kind,
        Func<CancellationToken, Task<byte?>> readByteAsync,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> writeAsync,
        Func<string, CancellationToken, Task> writeTextAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readByteAsync);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentNullException.ThrowIfNull(writeTextAsync);

        var label = kind == CopySourceKind.Ymodem ? "YMODEM" : "XMODEM";
        await writeTextAsync(
                $"Begin {label} upload from your SSH terminal client now.\r\n" +
                "TechConnect is receiving on this SSH session; only file payload bytes are written to serial.\r\n" +
                "A compatible terminal client with XMODEM/YMODEM support is required.\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 256 * 1024, resumeWriterThreshold: 128 * 1024));
        long? length = null;
        var display = label.ToLowerInvariant() + ":";

        if (kind == CopySourceKind.Ymodem)
        {
            // Start YMODEM in background; ContentLength remains unknown to the transfer layer
            // until the stream completes (padding trim uses size inside the receiver).
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        var y = new YmodemReceiver(readByteAsync, writeAsync);
                        await y.ReceiveAsync(pipe.Writer.AsStream(), cancellationToken).ConfigureAwait(false);
                        await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "YMODEM receive failed");
                        await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                    }
                },
                CancellationToken.None);

            return new ModemCopyFileSource(display, length, pipe.Reader.AsStream());
        }

        _ = Task.Run(
            async () =>
            {
                try
                {
                    var x = new XmodemReceiver(readByteAsync, writeAsync);
                    await x.ReceiveAsync(pipe.Writer.AsStream(), cancellationToken).ConfigureAwait(false);
                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "XMODEM receive failed");
                    await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        return new ModemCopyFileSource(display, length, pipe.Reader.AsStream());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
