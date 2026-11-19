// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CopyTransferService.cs
//
// Purpose:
// Streams raw binary payload from an opened copy source into the existing serial
// attachment transport without attach/detach or device-acceptance claims.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TechConnect.Protocol;
using TechConnect.Server.Attachment;
using TechConnect.Server.Connector;

namespace TechConnect.Server.Copy;

/// <summary>
/// Streams raw payload bytes from a copy source into the existing serial attachment transport.
/// Does not attach, detach, reconfigure serial, or terminate the connector.
/// </summary>
/// <remarks>
/// Successful completion means bytes were delivered through the connector serial
/// write path (driver output queue drained per write). It does not prove device
/// firmware acceptance or flash success. Progress callbacks are throttled to
/// roughly every 500 ms plus a final report. Errors that may contain credentialed
/// URLs are sanitized before returning to the operator.
/// </remarks>
internal sealed class CopyTransferService
{
    /// <summary>Default read buffer size when streaming from the source (8192 bytes).</summary>
    public const int DefaultChunkSize = 8192;

    /// <summary>Per-attachment exclusive-copy registry.</summary>
    private readonly ActiveCopyRegistry _activeCopies;

    /// <summary>Structured logger; must not receive credentialed URLs.</summary>
    private readonly ILogger<CopyTransferService> _logger;

    /// <summary>
    /// Creates a transfer service bound to the shared active-copy registry.
    /// </summary>
    /// <param name="activeCopies">Registry ensuring one copy per attachment.</param>
    /// <param name="logger">Logger for transfer outcomes.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public CopyTransferService(ActiveCopyRegistry activeCopies, ILogger<CopyTransferService> logger)
    {
        _activeCopies = activeCopies ?? throw new ArgumentNullException(nameof(activeCopies));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the source stream to completion and writes each chunk to serial.
    /// </summary>
    /// <param name="source">Opened binary file source.</param>
    /// <param name="attachment">Operator attachment whose session id gates exclusivity.</param>
    /// <param name="connection">
    /// Connector connection used by the default serial writer; may be unused when
    /// <paramref name="writeSerialAsync"/> is supplied (tests).
    /// </param>
    /// <param name="operatorSessionId">Operator claiming the active-copy slot.</param>
    /// <param name="reportProgressAsync">
    /// Optional progress callback (<c>bytesWritten</c>, <c>contentLength</c>, token).
    /// </param>
    /// <param name="cancellationToken">Cancels mid-transfer; releases the registry in finally.</param>
    /// <param name="writeSerialAsync">
    /// Optional override for serial writes; when null, uses <see cref="WriteSerialChunkAsync"/>.
    /// </param>
    /// <returns>
    /// A <see cref="CopyTransferResult"/> describing success, cancel, or failure.
    /// Success messages explicitly disclaim device-level acceptance.
    /// </returns>
    public async Task<CopyTransferResult> TransferAsync(
        ICopyFileSource source,
        OperatorAttachment attachment,
        ConnectorConnection connection,
        string operatorSessionId,
        Func<long, long?, CancellationToken, Task>? reportProgressAsync,
        CancellationToken cancellationToken,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task>? writeSerialAsync = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorSessionId);
        if (writeSerialAsync is null)
        {
            ArgumentNullException.ThrowIfNull(connection);
        }

        if (!_activeCopies.TryBegin(attachment.SessionId, operatorSessionId))
        {
            return new CopyTransferResult
            {
                Success = false,
                Message = "% A copy operation is already active for this serial attachment."
            };
        }

        writeSerialAsync ??= (data, ct) => WriteSerialChunkAsync(attachment, connection!, data, ct);

        var sw = Stopwatch.StartNew();
        long written = 0;
        try
        {
            var buffer = new byte[DefaultChunkSize];
            var stream = source.Content;
            var lastReport = Stopwatch.StartNew();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await writeSerialAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;

                if (reportProgressAsync is not null && lastReport.ElapsedMilliseconds >= 500)
                {
                    await reportProgressAsync(written, source.ContentLength, cancellationToken)
                        .ConfigureAwait(false);
                    lastReport.Restart();
                }
            }

            if (reportProgressAsync is not null)
            {
                await reportProgressAsync(written, source.ContentLength, cancellationToken)
                    .ConfigureAwait(false);
            }

            sw.Stop();
            _logger.LogInformation(
                "Copy transfer completed. SessionId={SessionId} Source={Source} Bytes={Bytes} DurationMs={DurationMs}",
                attachment.SessionId,
                source.DisplayName,
                written,
                sw.ElapsedMilliseconds);

            return new CopyTransferResult
            {
                Success = true,
                Message =
                    $"Copy completed: {written} payload byte(s) delivered to the connector serial writer " +
                    $"in {FormatDuration(sw.Elapsed)}. " +
                    "This confirms end-to-end delivery through mTLS into the connector serial write path " +
                    "(driver output queue drained per write), not device-level acceptance or flash success.",
                BytesWrittenToSerialTransport = written,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new CopyTransferResult
            {
                Success = false,
                Cancelled = true,
                Message = $"% Copy cancelled after {written} byte(s) written to the serial transport.",
                BytesWrittenToSerialTransport = written,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException ex)
        {
            // Nested timeout / unrelated cancel — treat as failure, still release the registry.
            sw.Stop();
            _logger.LogWarning(
                ex,
                "Copy transfer aborted. SessionId={SessionId} Source={Source} BytesWritten={Bytes}",
                attachment.SessionId,
                source.DisplayName,
                written);
            return new CopyTransferResult
            {
                Success = false,
                Message = "% Copy failed.",
                BytesWrittenToSerialTransport = written,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "Copy transfer failed. SessionId={SessionId} Source={Source} BytesWritten={Bytes}",
                attachment.SessionId,
                source.DisplayName,
                written);
            var safe = SanitizeError(ex);
            return new CopyTransferResult
            {
                Success = false,
                Message = safe,
                BytesWrittenToSerialTransport = written,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            _activeCopies.End(attachment.SessionId, operatorSessionId);
        }
    }

    /// <summary>
    /// Writes through the existing protocol session without detaching on failure.
    /// </summary>
    /// <param name="attachment">Attachment whose byte counters are updated.</param>
    /// <param name="connection">Live connector protocol session.</param>
    /// <param name="data">Raw binary chunk; empty writes are no-ops.</param>
    /// <param name="cancellationToken">Cancels the protocol write.</param>
    /// <remarks>
    /// Bytes are treated as opaque binary — never as text. Counters are updated
    /// around the connector protocol <c>WriteSerialDataAsync</c> call.
    /// </remarks>
    public static async Task WriteSerialChunkAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        attachment.AddBytesFromOperator(data.Length);
        await connection.Protocol.WriteSerialDataAsync(data, cancellationToken).ConfigureAwait(false);
        attachment.AddBytesToDevice(data.Length);
    }

    /// <summary>
    /// Formats a duration for the operator success message.
    /// </summary>
    /// <param name="duration">Elapsed transfer time.</param>
    /// <returns>Milliseconds string when under 1 s; otherwise seconds.</returns>
    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:0} ms"
            : $"{duration.TotalSeconds:0.###} s";

    /// <summary>
    /// Maps exceptions to operator-safe messages without leaking credentialed URLs.
    /// </summary>
    /// <param name="ex">Caught failure.</param>
    /// <returns>
    /// Sanitized <c>%...</c> message for known operator errors; otherwise <c>% Copy failed.</c>
    /// </returns>
    private static string SanitizeError(Exception ex)
    {
        // Never surface exception messages that may contain URLs with credentials.
        if (ex is InvalidOperationException ioe &&
            !string.IsNullOrWhiteSpace(ioe.Message) &&
            ioe.Message.StartsWith('%'))
        {
            return CopyUrlRedaction.SanitizeCommandLine(ioe.Message);
        }

        return "% Copy failed.";
    }
}
