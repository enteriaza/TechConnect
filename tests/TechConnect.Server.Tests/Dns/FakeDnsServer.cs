// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FakeDnsServer.cs
//
// Purpose:
// In-process UDP/TCP fake DNS server for ConfiguredDnsResolver tests.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Dns;

/// <summary>
/// Parsed inbound DNS query presented to the fake server handler.
/// </summary>
internal sealed class FakeDnsQuery
{
    /// <summary>Transaction id from the query header.</summary>
    public required ushort TransactionId { get; init; }

    /// <summary>Normalized query name.</summary>
    public required string Name { get; init; }

    /// <summary>Queried record type.</summary>
    public required DnsRecordType Type { get; init; }

    /// <summary>Whether the query arrived over TCP (vs UDP).</summary>
    public required bool ViaTcp { get; init; }
}

/// <summary>
/// Scripted reply behaviour for <see cref="FakeDnsServer"/> handlers.
/// </summary>
internal sealed class FakeDnsReply
{
    /// <summary>RCODE to encode (default NOERROR).</summary>
    public DnsResponseCode ResponseCode { get; init; } = DnsResponseCode.NoError;

    /// <summary>When true on UDP, set the TC bit to force client TCP fallback.</summary>
    public bool TruncateUdp { get; init; }

    /// <summary>When true, send no response (client timeout path).</summary>
    public bool Silent { get; init; }

    /// <summary>Close the TCP connection without sending a DNS response (TCP fallback failure).</summary>
    public bool DropTcp { get; init; }

    /// <summary>When true, encode a mismatched transaction id.</summary>
    public bool WrongTransactionId { get; init; }

    /// <summary>When true, return non-DNS garbage bytes.</summary>
    public bool MalformedGarbage { get; init; }

    /// <summary>When true, compress answer names with pointers (default).</summary>
    public bool CompressAnswerName { get; init; } = true;

    /// <summary>Optional delay before responding.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>A/AAAA addresses to include in the answer section.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>PTR hostname to include for reverse replies.</summary>
    public string? PtrHostname { get; init; }
}

/// <summary>Loopback UDP/TCP DNS server for unit tests (never external).</summary>
/// <remarks>
/// Owns UDP/TCP listen sockets and background accept/receive loops until
/// <see cref="DisposeAsync"/>. Used only with <see cref="ConfiguredDnsResolver"/> test ctor ports.
/// </remarks>
internal sealed class FakeDnsServer : IAsyncDisposable
{
    /// <summary>Handler that maps each query to a scripted reply.</summary>
    private readonly Func<FakeDnsQuery, FakeDnsReply> _handler;

    /// <summary>Cancellation source that stops background loops on dispose.</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Owned UDP listen/receive socket.</summary>
    private readonly Socket _udp;

    /// <summary>Owned TCP listen socket.</summary>
    private readonly Socket _tcp;

    /// <summary>Background UDP receive loop.</summary>
    private readonly Task _udpLoop;

    /// <summary>Background TCP accept loop.</summary>
    private readonly Task _tcpLoop;

    /// <summary>
    /// Initializes a started fake server over already-bound sockets.
    /// </summary>
    /// <param name="address">Bound address.</param>
    /// <param name="port">Bound port.</param>
    /// <param name="udp">Owned UDP socket.</param>
    /// <param name="tcp">Owned TCP listen socket.</param>
    /// <param name="handler">Query handler.</param>
    private FakeDnsServer(
        IPAddress address,
        int port,
        Socket udp,
        Socket tcp,
        Func<FakeDnsQuery, FakeDnsReply> handler)
    {
        Address = address;
        Port = port;
        _udp = udp;
        _tcp = tcp;
        _handler = handler;
        _udpLoop = Task.Run(() => UdpLoopAsync(_cts.Token));
        _tcpLoop = Task.Run(() => TcpLoopAsync(_cts.Token));
    }

    /// <summary>Bound listen address.</summary>
    public IPAddress Address { get; }

    /// <summary>Bound listen port (ephemeral when started with port 0).</summary>
    public int Port { get; }

    /// <summary>
    /// Starts a fake DNS server on an ephemeral port for <paramref name="address"/>.
    /// </summary>
    /// <param name="address">Listen address (typically loopback).</param>
    /// <param name="handler">Query handler.</param>
    /// <returns>Started server; dispose to stop.</returns>
    public static FakeDnsServer Start(IPAddress address, Func<FakeDnsQuery, FakeDnsReply> handler) =>
        Start(address, port: 0, handler);

    /// <summary>
    /// Starts a fake DNS server on the specified port.
    /// </summary>
    /// <param name="address">Listen address.</param>
    /// <param name="port">Port (0 = ephemeral).</param>
    /// <param name="handler">Query handler.</param>
    /// <returns>Started server; dispose to stop.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="handler"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is out of range.</exception>
    public static FakeDnsServer Start(IPAddress address, int port, Func<FakeDnsQuery, FakeDnsReply> handler)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(handler);
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        var tcp = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        tcp.Bind(new IPEndPoint(address, port));
        tcp.Listen(8);
        var boundPort = ((IPEndPoint)tcp.LocalEndPoint!).Port;

        var udp = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(address, boundPort));

        return new FakeDnsServer(address, boundPort, udp, tcp, handler);
    }

    /// <summary>
    /// Cancels background loops, disposes listen sockets, and waits for loop tasks to finish.
    /// </summary>
    /// <returns>A task that completes when shutdown finishes (loop exceptions are swallowed).</returns>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _udp.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            _tcp.Dispose();
        }
        catch
        {
            // ignored
        }

        try
        {
            await Task.WhenAll(_udpLoop, _tcpLoop).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
    }

    /// <summary>Receives UDP queries and sends scripted replies until cancelled.</summary>
    /// <param name="cancellationToken">Shutdown token.</param>
    private async Task UdpLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[DnsWireCodec.MaxUdpPayload];
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(Address, 0);
                var result = await _udp
                    .ReceiveFromAsync(buffer.AsMemory(), SocketFlags.None, remote, cancellationToken)
                    .ConfigureAwait(false);
                var request = buffer.AsSpan(0, result.ReceivedBytes).ToArray();
                if (!TryParseQuery(request, out var query))
                {
                    continue;
                }

                query = new FakeDnsQuery
                {
                    TransactionId = query.TransactionId,
                    Name = query.Name,
                    Type = query.Type,
                    ViaTcp = false
                };
                var reply = _handler(query);
                if (reply.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(reply.Delay, cancellationToken).ConfigureAwait(false);
                }

                if (reply.Silent)
                {
                    continue;
                }

                var response = BuildResponse(request, query, reply, forUdp: true);
                await _udp
                    .SendToAsync(response, SocketFlags.None, result.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Accepts TCP clients and handles length-prefixed DNS queries until cancelled.</summary>
    /// <param name="cancellationToken">Shutdown token.</param>
    private async Task TcpLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;
            try
            {
                client = await _tcp.AcceptAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleTcpClientAsync(client, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Reads one framed DNS query from <paramref name="client"/> and writes a framed reply.</summary>
    /// <param name="client">Accepted TCP client socket (disposed by this method).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task HandleTcpClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                var lengthBuf = new byte[2];
                await ReadExactAsync(client, lengthBuf, cancellationToken).ConfigureAwait(false);
                var length = (lengthBuf[0] << 8) | lengthBuf[1];
                if (length is < 12 or > DnsWireCodec.MaxTcpMessage)
                {
                    return;
                }

                var request = new byte[length];
                await ReadExactAsync(client, request, cancellationToken).ConfigureAwait(false);
                if (!TryParseQuery(request, out var query))
                {
                    return;
                }

                query = new FakeDnsQuery
                {
                    TransactionId = query.TransactionId,
                    Name = query.Name,
                    Type = query.Type,
                    ViaTcp = true
                };
                var reply = _handler(query);
                if (reply.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(reply.Delay, cancellationToken).ConfigureAwait(false);
                }

                if (reply.Silent || reply.DropTcp)
                {
                    return;
                }

                var response = BuildResponse(request, query, reply, forUdp: false);
                var framed = new byte[2 + response.Length];
                framed[0] = (byte)(response.Length >> 8);
                framed[1] = (byte)(response.Length & 0xFF);
                response.CopyTo(framed.AsSpan(2));
                await client.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="socket"/>.</summary>
    /// <param name="socket">Connected socket.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket.ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("TCP closed.");
            }

            offset += read;
        }
    }

    /// <summary>Parses a minimal DNS query header + question into a <see cref="FakeDnsQuery"/>.</summary>
    /// <param name="message">Raw DNS message.</param>
    /// <param name="query">Parsed query on success.</param>
    /// <returns><see langword="true"/> when the question section was parsed.</returns>
    private static bool TryParseQuery(ReadOnlySpan<byte> message, out FakeDnsQuery query)
    {
        query = null!;
        if (message.Length < 12)
        {
            return false;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message[..2]);
        var qd = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2));
        if (qd != 1)
        {
            return false;
        }

        var offset = 12;
        if (!TryReadName(message, ref offset, out var name))
        {
            return false;
        }

        if (offset + 4 > message.Length)
        {
            return false;
        }

        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
        query = new FakeDnsQuery
        {
            TransactionId = id,
            Name = name,
            Type = type,
            ViaTcp = false
        };
        return true;
    }

    /// <summary>Builds a DNS response message for <paramref name="query"/> according to <paramref name="reply"/>.</summary>
    /// <returns>Response bytes, possibly truncated/malformed per reply flags.</returns>
    private static byte[] BuildResponse(
        ReadOnlySpan<byte> request,
        FakeDnsQuery query,
        FakeDnsReply reply,
        bool forUdp)
    {
        if (reply.MalformedGarbage)
        {
            return [0x00, 0x01, 0x02];
        }

        var answers = new List<byte[]>();
        if (reply.ResponseCode == DnsResponseCode.NoError)
        {
            if (query.Type is DnsRecordType.A or DnsRecordType.Aaaa)
            {
                foreach (var address in reply.Addresses)
                {
                    if (query.Type == DnsRecordType.A &&
                        address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        answers.Add(BuildAddressRecord(query, address, reply.CompressAnswerName));
                    }
                    else if (query.Type == DnsRecordType.Aaaa &&
                             address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        answers.Add(BuildAddressRecord(query, address, reply.CompressAnswerName));
                    }
                }
            }
            else if (query.Type == DnsRecordType.Ptr && !string.IsNullOrWhiteSpace(reply.PtrHostname))
            {
                answers.Add(BuildPtrRecord(query, reply.PtrHostname!, reply.CompressAnswerName));
            }
        }

        // Question section copied from request (from offset 12 through type+class).
        var qEnd = 12;
        var tmp = qEnd;
        _ = TryReadName(request, ref tmp, out _);
        qEnd = tmp + 4;
        var question = request.Slice(12, qEnd - 12).ToArray();

        var answerBytes = answers.SelectMany(a => a).ToArray();
        var packet = new byte[12 + question.Length + answerBytes.Length];
        var txid = reply.WrongTransactionId
            ? (ushort)(query.TransactionId ^ 0xFFFF)
            : query.TransactionId;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), txid);

        // QR=1, RD=1, RA=1, optionally TC
        ushort flags = 0x8180;
        if (forUdp && reply.TruncateUdp)
        {
            flags |= 0x0200;
        }

        flags = (ushort)((flags & 0xFFF0) | ((int)reply.ResponseCode & 0xF));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        var anCount = forUdp && reply.TruncateUdp ? 0 : answers.Count;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), (ushort)anCount);
        question.CopyTo(packet.AsSpan(12));
        if (anCount > 0)
        {
            answerBytes.CopyTo(packet.AsSpan(12 + question.Length));
        }
        else if (forUdp && reply.TruncateUdp)
        {
            // Header + question only (TC set) — force TCP fallback.
            return packet.AsSpan(0, 12 + question.Length).ToArray();
        }

        return packet;
    }

    /// <summary>Builds one A/AAAA answer RR for <paramref name="address"/>.</summary>
    /// <param name="query">Original query (for name/type).</param>
    /// <param name="address">Answer address.</param>
    /// <param name="compress">Whether to use a compression pointer to the question name.</param>
    /// <returns>RR octets.</returns>
    private static byte[] BuildAddressRecord(FakeDnsQuery query, IPAddress address, bool compress)
    {
        var rdata = address.GetAddressBytes();
        var name = compress ? new byte[] { 0xC0, 0x0C } : EncodeName(query.Name);
        var rr = new byte[name.Length + 10 + rdata.Length];
        name.CopyTo(rr.AsSpan(0));
        var o = name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o, 2), (ushort)query.Type);
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o + 2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(rr.AsSpan(o + 4, 4), 60);
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o + 8, 2), (ushort)rdata.Length);
        rdata.CopyTo(rr.AsSpan(o + 10));
        return rr;
    }

    /// <summary>Builds one PTR answer RR for <paramref name="ptrHostname"/>.</summary>
    /// <param name="query">Original query.</param>
    /// <param name="ptrHostname">PTR target name.</param>
    /// <param name="compress">Whether to compress the owner name.</param>
    /// <returns>RR octets.</returns>
    private static byte[] BuildPtrRecord(FakeDnsQuery query, string ptrHostname, bool compress)
    {
        var name = compress ? new byte[] { 0xC0, 0x0C } : EncodeName(query.Name);
        var ptr = EncodeName(ptrHostname);
        var rr = new byte[name.Length + 10 + ptr.Length];
        name.CopyTo(rr.AsSpan(0));
        var o = name.Length;
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o, 2), (ushort)DnsRecordType.Ptr);
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o + 2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(rr.AsSpan(o + 4, 4), 60);
        BinaryPrimitives.WriteUInt16BigEndian(rr.AsSpan(o + 8, 2), (ushort)ptr.Length);
        ptr.CopyTo(rr.AsSpan(o + 10));
        return rr;
    }

    /// <summary>Encodes a DNS name as length-prefixed labels for fake responses.</summary>
    /// <param name="name">DNS name.</param>
    /// <returns>Wire-format name octets.</returns>
    private static byte[] EncodeName(string name)
    {
        var normalized = name.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return [0];
        }

        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var size = 1 + labels.Sum(l => 1 + l.Length);
        var buffer = new byte[size];
        var offset = 0;
        foreach (var label in labels)
        {
            buffer[offset++] = (byte)label.Length;
            Encoding.ASCII.GetBytes(label, buffer.AsSpan(offset));
            offset += label.Length;
        }

        buffer[offset] = 0;
        return buffer;
    }

    /// <summary>Reads an uncompressed DNS name from a fake query buffer.</summary>
    /// <param name="message">Message buffer.</param>
    /// <param name="offset">Current offset; advanced on success.</param>
    /// <param name="name">Decoded name.</param>
    /// <returns><see langword="true"/> when a name was read.</returns>
    private static bool TryReadName(ReadOnlySpan<byte> message, ref int offset, out string name)
    {
        name = string.Empty;
        var sb = new StringBuilder();
        var jumped = false;
        var endOffset = offset;
        var depth = 0;
        while (true)
        {
            if (offset >= message.Length)
            {
                return false;
            }

            var len = message[offset];
            if (len == 0)
            {
                offset++;
                if (!jumped)
                {
                    endOffset = offset;
                }

                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= message.Length)
                {
                    return false;
                }

                var pointer = ((len & 0x3F) << 8) | message[offset + 1];
                if (pointer >= message.Length || ++depth > 32)
                {
                    return false;
                }

                if (!jumped)
                {
                    endOffset = offset + 2;
                    jumped = true;
                }

                offset = pointer;
                continue;
            }

            if (len > 63 || offset + 1 + len > message.Length)
            {
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Encoding.ASCII.GetString(message.Slice(offset + 1, len)).ToLowerInvariant());
            offset += 1 + len;
            if (!jumped)
            {
                endOffset = offset;
            }
        }

        offset = jumped ? endOffset : offset;
        name = sb.ToString();
        return true;
    }
}
