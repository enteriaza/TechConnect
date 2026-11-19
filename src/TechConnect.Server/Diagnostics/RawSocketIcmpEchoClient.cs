// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RawSocketIcmpEchoClient.cs
//
// Purpose:
// Raw-socket ICMP/ICMPv6 Echo Request/Reply client for ping (no OS ping process).
// Owns per-call sockets; Windows typically requires elevation for raw ICMP.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// ICMP / ICMPv6 Echo Request/Reply using raw sockets (no OS <c>ping</c> process, no third-party library).
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="SendAsync"/> call creates and disposes its own raw socket.
/// Windows typically requires elevated privileges for raw ICMP sockets; access
/// failures are returned as <see cref="IcmpEchoStatus.Error"/> rather than thrown.
/// </para>
/// <para>
/// Cross-platform native qualification is not claimed here: behaviour depends on
/// OS raw-socket delivery (IPv4 replies include an IP header; ICMPv6 replies are
/// typically ICMP-header-first on Windows). Live network tests gate platform coverage.
/// </para>
/// <para>
/// Per-probe timeout uses a linked <see cref="CancellationTokenSource"/> with
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>; caller cancellation
/// propagates as <see cref="OperationCanceledException"/>.
/// </para>
/// </remarks>
internal sealed class RawSocketIcmpEchoClient : IIcmpEchoClient
{
    /// <summary>ICMPv4 Echo Request type.</summary>
    private const byte Icmpv4EchoRequest = 8;

    /// <summary>ICMPv4 Echo Reply type.</summary>
    private const byte Icmpv4EchoReply = 0;

    /// <summary>ICMPv4 Destination Unreachable type.</summary>
    private const byte Icmpv4DestUnreachable = 3;

    /// <summary>ICMPv4 Fragmentation Needed code within Destination Unreachable.</summary>
    private const byte Icmpv4FragNeeded = 4; // code within Dest Unreachable

    /// <summary>ICMPv6 Echo Request type.</summary>
    private const byte Icmpv6EchoRequest = 128;

    /// <summary>ICMPv6 Echo Reply type.</summary>
    private const byte Icmpv6EchoReply = 129;

    /// <summary>Logger for privilege and unexpected failures.</summary>
    private readonly ILogger<RawSocketIcmpEchoClient> _logger;

    /// <summary>
    /// Initializes the client with the specified logger.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public RawSocketIcmpEchoClient(ILogger<RawSocketIcmpEchoClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IcmpEchoReply> SendAsync(IcmpEchoRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Destination);

        if (request.PayloadBytes is < PingLimits.MinPayloadBytes or > PingLimits.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Payload size is out of range.");
        }

        if (request.DontFragment &&
            request.Destination.AddressFamily != AddressFamily.InterNetwork)
        {
            return new IcmpEchoReply
            {
                Status = IcmpEchoStatus.Error,
                Error = "Don't Fragment (df) is only supported for IPv4."
            };
        }

        try
        {
            return request.Destination.AddressFamily switch
            {
                AddressFamily.InterNetwork => await SendIpv4Async(request, cancellationToken)
                    .ConfigureAwait(false),
                AddressFamily.InterNetworkV6 => await SendIpv6Async(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => new IcmpEchoReply
                {
                    Status = IcmpEchoStatus.Error,
                    Error = "Unsupported address family."
                }
            };
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied
                                             or SocketError.OperationNotSupported
                                             or SocketError.ProtocolOption)
        {
            _logger.LogWarning(ex, "Raw ICMP socket unavailable");
            return new IcmpEchoReply
            {
                Status = IcmpEchoStatus.Error,
                Error =
                    "ICMP raw sockets are unavailable on this host (privileges or platform policy). " +
                    ex.Message
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ICMP echo failed");
            return new IcmpEchoReply
            {
                Status = IcmpEchoStatus.Error,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Sends one IPv4 ICMP Echo Request and waits for a matching reply or timeout.
    /// </summary>
    /// <param name="request">Probe parameters; destination must be IPv4.</param>
    /// <param name="cancellationToken">Caller cancellation; linked with per-probe timeout.</param>
    /// <returns>Matched reply, timeout, or family/source error status.</returns>
    private static async Task<IcmpEchoReply> SendIpv4Async(
        IcmpEchoRequest request,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
        socket.DontFragment = request.DontFragment;

        if (request.Source is not null)
        {
            if (request.Source.AddressFamily != AddressFamily.InterNetwork)
            {
                return new IcmpEchoReply
                {
                    Status = IcmpEchoStatus.Error,
                    Error = "Source address must be IPv4 for an IPv4 destination."
                };
            }

            socket.Bind(new IPEndPoint(request.Source, 0));
        }

        var endpoint = new IPEndPoint(request.Destination, 0);
        var packet = BuildIcmpv4EchoRequest(request.Identifier, request.Sequence, request.PayloadBytes);
        var receiveBuffer = new byte[65535];

        var sw = Stopwatch.StartNew();
        await socket.SendToAsync(packet, SocketFlags.None, endpoint, cancellationToken)
            .ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                received = await socket
                    .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new IcmpEchoReply { Status = IcmpEchoStatus.TimedOut };
            }

            sw.Stop();
            var reply = TryParseIpv4Reply(
                receiveBuffer.AsSpan(0, received.ReceivedBytes),
                request.Identifier,
                request.Sequence,
                sw.Elapsed,
                received.RemoteEndPoint as IPEndPoint);

            if (reply is not null)
            {
                return reply;
            }

            sw.Start();
        }
    }

    /// <summary>
    /// Sends one ICMPv6 Echo Request and waits for a matching Echo Reply or timeout.
    /// </summary>
    /// <param name="request">Probe parameters; destination must be IPv6.</param>
    /// <param name="cancellationToken">Caller cancellation; linked with per-probe timeout.</param>
    /// <returns>Matched reply, timeout, or family/source error status.</returns>
    /// <remarks>ICMPv6 Destination Unreachable is not mapped in this client (ignored until timeout).</remarks>
    private static async Task<IcmpEchoReply> SendIpv6Async(
        IcmpEchoRequest request,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Raw, ProtocolType.IcmpV6);

        if (request.Source is not null)
        {
            if (request.Source.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return new IcmpEchoReply
                {
                    Status = IcmpEchoStatus.Error,
                    Error = "Source address must be IPv6 for an IPv6 destination."
                };
            }

            socket.Bind(new IPEndPoint(request.Source, 0));
        }

        var endpoint = new IPEndPoint(request.Destination, 0);
        var packet = BuildIcmpv6EchoRequest(request.Identifier, request.Sequence, request.PayloadBytes);
        var receiveBuffer = new byte[65535];

        var sw = Stopwatch.StartNew();
        await socket.SendToAsync(packet, SocketFlags.None, endpoint, cancellationToken)
            .ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(request.Timeout);

        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
                received = await socket
                    .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new IcmpEchoReply { Status = IcmpEchoStatus.TimedOut };
            }

            sw.Stop();
            var reply = TryParseIpv6Reply(
                receiveBuffer.AsSpan(0, received.ReceivedBytes),
                request.Identifier,
                request.Sequence,
                sw.Elapsed,
                received.RemoteEndPoint as IPEndPoint);

            if (reply is not null)
            {
                return reply;
            }

            sw.Start();
        }
    }

    /// <summary>
    /// Builds an ICMPv4 Echo Request with internet checksum and repeating payload pattern.
    /// </summary>
    /// <param name="identifier">ICMP identifier field.</param>
    /// <param name="sequence">ICMP sequence field.</param>
    /// <param name="payloadBytes">Payload length after the 8-byte ICMP header.</param>
    /// <returns>Complete ICMP datagram (no IP header).</returns>
    internal static byte[] BuildIcmpv4EchoRequest(ushort identifier, ushort sequence, int payloadBytes)
    {
        var packet = new byte[8 + payloadBytes];
        packet[0] = Icmpv4EchoRequest;
        packet[1] = 0;
        // checksum filled below
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), sequence);
        for (var i = 0; i < payloadBytes; i++)
        {
            packet[8 + i] = (byte)('a' + (i % 23));
        }

        var checksum = ComputeInternetChecksum(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checksum);
        return packet;
    }

    /// <summary>
    /// Builds an ICMPv6 Echo Request with checksum left as zero for OS fill-in.
    /// </summary>
    /// <param name="identifier">ICMP identifier field.</param>
    /// <param name="sequence">ICMP sequence field.</param>
    /// <param name="payloadBytes">Payload length after the 8-byte ICMPv6 header.</param>
    /// <returns>Complete ICMPv6 datagram (no IPv6 header).</returns>
    /// <remarks>
    /// ICMPv6 checksum includes a pseudo-header; this implementation leaves checksum 0 and
    /// relies on the OS to compute it when sending via raw ICMPv6 sockets on Windows/.NET
    /// (common behaviour). Identifier/sequence still match replies.
    /// </remarks>
    internal static byte[] BuildIcmpv6EchoRequest(ushort identifier, ushort sequence, int payloadBytes)
    {
        // ICMPv6 checksum includes a pseudo-header; many stacks accept 0 and rewrite, but we
        // leave checksum 0 and rely on the OS to compute it when sending via raw ICMPv6 sockets
        // on Windows/.NET (common behaviour). Identifier/sequence still match replies.
        var packet = new byte[8 + payloadBytes];
        packet[0] = Icmpv6EchoRequest;
        packet[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), sequence);
        for (var i = 0; i < payloadBytes; i++)
        {
            packet[8 + i] = (byte)('a' + (i % 23));
        }

        return packet;
    }

    /// <summary>
    /// Computes the standard internet checksum (one's complement) over <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Octets to checksum (checksum field should be zero when computing).</param>
    /// <returns>16-bit checksum in host-independent big-endian wire form as <see cref="ushort"/>.</returns>
    internal static ushort ComputeInternetChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        var offset = 0;
        while (offset + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            offset += 2;
        }

        if (offset < data.Length)
        {
            sum += (uint)(data[offset] << 8);
        }

        while (sum >> 16 != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    /// <summary>
    /// Attempts to parse an IPv4 raw receive buffer as a matching Echo Reply or Dest Unreachable.
    /// </summary>
    /// <param name="frame">Received bytes including IPv4 header.</param>
    /// <param name="identifier">Expected ICMP identifier.</param>
    /// <param name="sequence">Expected ICMP sequence.</param>
    /// <param name="rtt">Elapsed time to assign on match.</param>
    /// <param name="remote">Remote endpoint from receive-from, used for <see cref="IcmpEchoReply.Responder"/>.</param>
    /// <returns>Matched reply, or <see langword="null"/> to keep waiting for another packet.</returns>
    private static IcmpEchoReply? TryParseIpv4Reply(
        ReadOnlySpan<byte> frame,
        ushort identifier,
        ushort sequence,
        TimeSpan rtt,
        IPEndPoint? remote)
    {
        // Raw IPv4 receives include the IP header.
        if (frame.Length < 20)
        {
            return null;
        }

        var ihl = (frame[0] & 0x0F) * 4;
        if (ihl < 20 || frame.Length < ihl + 8)
        {
            return null;
        }

        var icmp = frame[ihl..];
        var type = icmp[0];
        var code = icmp[1];

        if (type == Icmpv4EchoReply)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4, 2));
            var seq = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(6, 2));
            if (id != identifier || seq != sequence)
            {
                return null;
            }

            return new IcmpEchoReply
            {
                Status = IcmpEchoStatus.Success,
                RoundTripTime = rtt,
                Responder = remote?.Address
            };
        }

        if (type == Icmpv4DestUnreachable)
        {
            // Original ICMP header starts at offset 8 + original IP header inside ICMP payload.
            if (icmp.Length < 8 + 20 + 8)
            {
                return new IcmpEchoReply
                {
                    Status = code == Icmpv4FragNeeded
                        ? IcmpEchoStatus.FragmentationNeeded
                        : IcmpEchoStatus.DestinationUnreachable,
                    RoundTripTime = rtt,
                    Responder = remote?.Address,
                    Error = code == Icmpv4FragNeeded
                        ? "Fragmentation needed and DF set."
                        : "Destination unreachable."
                };
            }

            var embeddedIp = icmp[8..];
            var embeddedIhl = (embeddedIp[0] & 0x0F) * 4;
            if (embeddedIhl < 20 || icmp.Length < 8 + embeddedIhl + 8)
            {
                return new IcmpEchoReply
                {
                    Status = code == Icmpv4FragNeeded
                        ? IcmpEchoStatus.FragmentationNeeded
                        : IcmpEchoStatus.DestinationUnreachable,
                    RoundTripTime = rtt,
                    Responder = remote?.Address
                };
            }

            var embeddedIcmp = icmp[(8 + embeddedIhl)..];
            var id = BinaryPrimitives.ReadUInt16BigEndian(embeddedIcmp.Slice(4, 2));
            var seq = BinaryPrimitives.ReadUInt16BigEndian(embeddedIcmp.Slice(6, 2));
            if (id != identifier || seq != sequence)
            {
                return null;
            }

            return new IcmpEchoReply
            {
                Status = code == Icmpv4FragNeeded
                    ? IcmpEchoStatus.FragmentationNeeded
                    : IcmpEchoStatus.DestinationUnreachable,
                RoundTripTime = rtt,
                Responder = remote?.Address,
                Error = code == Icmpv4FragNeeded
                    ? "Fragmentation needed and DF set."
                    : "Destination unreachable."
            };
        }

        return null;
    }

    /// <summary>
    /// Attempts to parse a raw ICMPv6 receive buffer as a matching Echo Reply.
    /// </summary>
    /// <param name="frame">Received bytes (ICMP header first; no outer IPv6 header expected).</param>
    /// <param name="identifier">Expected ICMPv6 identifier.</param>
    /// <param name="sequence">Expected ICMPv6 sequence.</param>
    /// <param name="rtt">Elapsed time to assign on match.</param>
    /// <param name="remote">Remote endpoint from receive-from.</param>
    /// <returns>Matched success reply, or <see langword="null"/> to keep waiting.</returns>
    private static IcmpEchoReply? TryParseIpv6Reply(
        ReadOnlySpan<byte> frame,
        ushort identifier,
        ushort sequence,
        TimeSpan rtt,
        IPEndPoint? remote)
    {
        // Raw ICMPv6 typically delivers the ICMP header without an IPv6 header.
        if (frame.Length < 8)
        {
            return null;
        }

        var type = frame[0];
        if (type != Icmpv6EchoReply)
        {
            return null;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
        var seq = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6, 2));
        if (id != identifier || seq != sequence)
        {
            return null;
        }

        return new IcmpEchoReply
        {
            Status = IcmpEchoStatus.Success,
            RoundTripTime = rtt,
            Responder = remote?.Address
        };
    }
}
