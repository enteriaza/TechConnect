// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TftpCopyFileSource.cs
//
// Purpose:
// Minimal RFC 1350 TFTP RRQ client that streams DATA block payloads into a pipe
// without knowing total file size up front.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Copy;

/// <summary>
/// Minimal TFTP RRQ client (RFC 1350). Streams DATA payload blocks without loading the whole file.
/// </summary>
/// <remarks>
/// <see cref="ContentLength"/> is always null — TFTP has no standard file-size metadata.
/// Transfer uses octet mode, 512-byte blocks, ACK retransmit on timeout, and treats
/// a short final DATA block (or zero-length block after an exact multiple) as EOF.
/// Hostnames are resolved via the OS DNS resolver for this outbound file source only.
/// </remarks>
internal sealed class TftpCopyFileSource : ICopyFileSource
{
    /// <summary>RFC 1350 default block size (bytes of payload per DATA packet).</summary>
    public const int DefaultBlockSize = 512;

    /// <summary>Default TFTP UDP port.</summary>
    public const int DefaultPort = 69;

    /// <summary>Maximum receive/retransmit attempts per expected block.</summary>
    public const int MaxRetries = 5;

    /// <summary>Per-attempt receive timeout in milliseconds.</summary>
    public const int TimeoutMilliseconds = 3000;

    /// <summary>Pipe reader exposed as <see cref="Content"/>.</summary>
    private readonly Stream _stream;

    /// <summary>
    /// Creates a source wrapping a pipe reader stream populated by a background download.
    /// </summary>
    /// <param name="displayName">Credential-free display URI.</param>
    /// <param name="stream">Readable payload stream.</param>
    private TftpCopyFileSource(string displayName, Stream stream)
    {
        DisplayName = displayName;
        _stream = stream;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>
    /// Always null; classic TFTP does not advertise total file size.
    /// </summary>
    public long? ContentLength => null;

    /// <inheritdoc />
    public Stream Content => _stream;

    /// <summary>
    /// Starts a background TFTP RRQ download and returns a streaming source immediately.
    /// </summary>
    /// <param name="source">Parsed TFTP source with host and filename path.</param>
    /// <param name="logger">Logger for timeout/failure warnings.</param>
    /// <param name="cancellationToken">Cancels the download loop.</param>
    /// <returns>Source whose <see cref="Content"/> yields payload bytes as they arrive.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException">Kind is not TFTP or host is missing.</exception>
    /// <exception cref="InvalidOperationException">Filename missing or host resolution failed.</exception>
    public static async Task<ICopyFileSource> OpenAsync(
        CopySourceUri source,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind != CopySourceKind.Tftp || string.IsNullOrWhiteSpace(source.Host))
        {
            throw new ArgumentException("TFTP source with host required.", nameof(source));
        }

        var filename = (source.Path ?? string.Empty).TrimStart('/');
        if (string.IsNullOrWhiteSpace(filename))
        {
            throw new InvalidOperationException("% TFTP source filename is required.");
        }

        var port = source.Port ?? DefaultPort;
        var pipe = new System.IO.Pipelines.Pipe();
        var server = new IPEndPoint(IPAddress.Parse(await ResolveHostAsync(source.Host, cancellationToken)), port);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await DownloadAsync(server, filename, pipe.Writer, logger, cancellationToken)
                        .ConfigureAwait(false);
                    await pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
            },
            CancellationToken.None);

        return new TftpCopyFileSource(source.DisplayUri, pipe.Reader.AsStream());
    }

    /// <summary>
    /// Performs the RRQ/DATA/ACK exchange, writing only DATA payloads to <paramref name="writer"/>.
    /// </summary>
    /// <param name="server">Initial server endpoint (port typically 69).</param>
    /// <param name="filename">Remote filename (octet mode).</param>
    /// <param name="writer">Pipe writer for payload bytes.</param>
    /// <param name="logger">Logger for timeouts.</param>
    /// <param name="cancellationToken">Cancels the loop.</param>
    /// <exception cref="InvalidOperationException">Timeout, ERROR opcode, or malformed packet.</exception>
    private static async Task DownloadAsync(
        IPEndPoint server,
        string filename,
        System.IO.Pipelines.PipeWriter writer,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var udp = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6
            ? IPAddress.IPv6Any
            : IPAddress.Any, 0));

        var rrq = BuildRrq(filename);
        EndPoint remote = new IPEndPoint(server.Address, server.Port);
        await udp.SendToAsync(rrq, SocketFlags.None, remote, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[4 + DefaultBlockSize];
        ushort expectedBlock = 1;
        var transferRemote = remote;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var received = 0;
            Exception? lastError = null;
            for (var attempt = 0; attempt < MaxRetries; attempt++)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeoutMilliseconds);
                    EndPoint from = new IPEndPoint(
                        server.AddressFamily == AddressFamily.InterNetworkV6
                            ? IPAddress.IPv6Any
                            : IPAddress.Any,
                        0);
                    var result = await udp
                        .ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, from, timeoutCts.Token)
                        .ConfigureAwait(false);
                    received = result.ReceivedBytes;
                    transferRemote = result.RemoteEndPoint;
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Retransmit last request: RRQ for block 1, else ACK expectedBlock-1
                    if (expectedBlock == 1)
                    {
                        await udp.SendToAsync(rrq, SocketFlags.None, remote, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        var ack = BuildAck((ushort)(expectedBlock - 1));
                        await udp.SendToAsync(ack, SocketFlags.None, transferRemote, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    lastError = new TimeoutException("TFTP receive timed out.");
                }
            }

            if (lastError is not null)
            {
                logger.LogWarning(lastError, "TFTP transfer timed out for {File}", filename);
                throw new InvalidOperationException("% TFTP transfer timed out.", lastError);
            }

            if (received < 4)
            {
                throw new InvalidOperationException("% Invalid TFTP packet.");
            }

            var opcode = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
            if (opcode == 5) // ERROR
            {
                var msg = Encoding.ASCII.GetString(buffer, 4, Math.Max(0, received - 5));
                throw new InvalidOperationException("% TFTP server error: " + Sanitize(msg));
            }

            if (opcode != 3) // DATA
            {
                throw new InvalidOperationException("% Unexpected TFTP opcode.");
            }

            var block = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
            if (block != expectedBlock)
            {
                // Duplicate — ACK again and continue.
                var dupAck = BuildAck(block);
                await udp.SendToAsync(dupAck, SocketFlags.None, transferRemote, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            var payloadLen = received - 4;
            if (payloadLen > 0)
            {
                var mem = writer.GetMemory(payloadLen);
                buffer.AsSpan(4, payloadLen).CopyTo(mem.Span);
                writer.Advance(payloadLen);
                var flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (flush.IsCompleted)
                {
                }
            }

            var ackPkt = BuildAck(block);
            await udp.SendToAsync(ackPkt, SocketFlags.None, transferRemote, cancellationToken)
                .ConfigureAwait(false);

            if (payloadLen < DefaultBlockSize)
            {
                return;
            }

            expectedBlock++;
        }
    }

    /// <summary>
    /// Builds an RRQ packet requesting <paramref name="filename"/> in octet mode.
    /// </summary>
    /// <param name="filename">Remote filename.</param>
    /// <returns>Wire-format RRQ bytes.</returns>
    private static byte[] BuildRrq(string filename)
    {
        var nameBytes = Encoding.ASCII.GetBytes(filename);
        var mode = Encoding.ASCII.GetBytes("octet");
        var packet = new byte[2 + nameBytes.Length + 1 + mode.Length + 1];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 1); // RRQ
        nameBytes.CopyTo(packet.AsSpan(2));
        packet[2 + nameBytes.Length] = 0;
        mode.CopyTo(packet.AsSpan(3 + nameBytes.Length));
        packet[^1] = 0;
        return packet;
    }

    /// <summary>
    /// Builds an ACK packet for the given block number.
    /// </summary>
    /// <param name="block">Block number to acknowledge.</param>
    /// <returns>Four-byte ACK packet.</returns>
    private static byte[] BuildAck(ushort block)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), block);
        return packet;
    }

    /// <summary>
    /// Resolves a TFTP host name or returns a literal IP string unchanged.
    /// </summary>
    /// <param name="host">Host name or IP literal.</param>
    /// <param name="cancellationToken">Cancels DNS lookup.</param>
    /// <returns>IP address string suitable for <see cref="IPAddress.Parse(string)"/>.</returns>
    /// <exception cref="InvalidOperationException">DNS returned no addresses.</exception>
    /// <remarks>
    /// Uses <see cref="System.Net.Dns.GetHostAddressesAsync(string, CancellationToken)"/> —
    /// outbound file-source resolution only, not TechConnect NOC diagnostic DNS.
    /// Prefers IPv4 when available.
    /// </remarks>
    private static async Task<string> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        // TFTP hostnames: use System.Net.Dns — this is outbound file source resolution,
        // not TechConnect NOC diagnostic DNS. Documented as using OS resolver for TFTP hosts only.
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)
            .ConfigureAwait(false);
        var pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                   ?? addresses.FirstOrDefault()
                   ?? throw new InvalidOperationException("% Unable to resolve TFTP host.");
        return pick.ToString();
    }

    /// <summary>
    /// Strips non-printable characters and truncates TFTP ERROR messages for operator display.
    /// </summary>
    /// <param name="message">Raw ASCII error text from the server.</param>
    /// <returns>Printable message at most 120 characters.</returns>
    private static string Sanitize(string message)
    {
        var cleaned = new string(message.Where(c => c is >= ' ' and <= '~').ToArray());
        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
