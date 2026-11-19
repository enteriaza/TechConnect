// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RawSocketIcmpTracerouteClient.cs
//
// Purpose:
// ICMP Echo traceroute with incrementing TTL/Hop Limit via raw sockets.
// Owns the probe socket for the duration of TraceAsync; port CLI option unused.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Native traceroute using ICMP Echo Request probes with incrementing TTL / Hop Limit.
/// </summary>
/// <remarks>
/// <para>
/// Windows does not deliver ICMP Time Exceeded / Dest Unreachable for <em>UDP</em> probes to a
/// raw ICMP socket (verified: UDP send + raw ICMP receive times out; ICMP Echo TTL probes receive
/// Time Exceeded / Echo Reply and match <c>tracert</c>). This client therefore uses ICMP Echo,
/// which is also what Windows <c>tracert</c> uses. CLI syntax (including unused <c>port</c>) is unchanged.
/// </para>
/// <para>
/// The raw ICMP socket is owned for the duration of each <see cref="TraceAsync"/> enumeration
/// (<c>using</c> dispose). Cross-platform native qualification is not claimed beyond what
/// live/unit tests establish; IPv6 outgoing hop limit uses
/// <see cref="Ipv6OutgoingHopLimitOption"/> (<c>IPV6_UNICAST_HOPS</c>), not <see cref="SocketOptionName.HopLimit"/>.
/// </para>
/// <para>
/// Probes are sequential on one socket. Per-probe timeout uses a linked CTS with
/// <c>CancelAfter</c>; caller cancellation throws between probes / on send.
/// </para>
/// </remarks>
internal sealed class RawSocketIcmpTracerouteClient : IIcmpTracerouteClient
{
    /// <summary>Logger for bind/probe failures and debug match tracing.</summary>
    private readonly ILogger<RawSocketIcmpTracerouteClient> _logger;

    /// <summary>
    /// Initializes the traceroute client.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public RawSocketIcmpTracerouteClient(ILogger<RawSocketIcmpTracerouteClient> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TracerouteProbeResult> TraceAsync(
        TracerouteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Family == TracerouteAddressFamily.Ipv6
            ? TraceIpv6Async(request, cancellationToken)
            : TraceIpv4Async(request, cancellationToken);
    }

    /// <summary>Runs an IPv4 ICMP Echo traceroute yielding one result per probe.</summary>
    /// <param name="request">Validated IPv4 traceroute request.</param>
    /// <param name="cancellationToken">Enumerator cancellation token.</param>
    /// <returns>Async sequence of probe results; socket-init errors yield a single Error result.</returns>
    private async IAsyncEnumerable<TracerouteProbeResult> TraceIpv4Async(
        TracerouteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryOpenIcmp(AddressFamily.InterNetwork, ProtocolType.Icmp, out var icmp, out var openError))
        {
            yield return openError!;
            yield break;
        }

        using (icmp)
        {
            if (!TryBindIcmp(icmp!, AddressFamily.InterNetwork, request.Source, out var bindError))
            {
                yield return bindError!;
                yield break;
            }

            if (request.InitialPort != TracerouteLimits.DefaultInitialPort)
            {
                _logger.LogDebug(
                    "Traceroute IPv4 ignoring port {Port}; probes use ICMP Echo (Windows raw sockets do not deliver UDP-related ICMP errors)",
                    request.InitialPort);
            }

            var identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var receiveBuffer = new byte[65535];

            for (var ttl = 1; ttl <= request.MaxTtl; ttl++)
            {
                var hopReachedDestination = false;

                for (var probe = 1; probe <= request.ProbesPerHop; probe++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Apply TTL immediately before each probe so values cannot leak across hops
                    // if concurrency is introduced later. Probes remain sequential on one socket.
                    icmp!.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                    var sequence = EncodeSequence(ttl, probe);
                    var (ok, result, error) = await TryProbeIpv4Async(
                            icmp,
                            request.Destination,
                            identifier,
                            sequence,
                            request.PayloadBytes,
                            TimeSpan.FromSeconds(request.TimeoutSeconds),
                            ttl,
                            probe,
                            receiveBuffer,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!ok)
                    {
                        yield return error!;
                        yield break;
                    }

                    if (result!.DestinationReached)
                    {
                        hopReachedDestination = true;
                    }

                    yield return result;

                    if (result.StopTracing && !result.DestinationReached)
                    {
                        yield break;
                    }
                }

                if (hopReachedDestination)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Runs an IPv6 ICMPv6 Echo traceroute yielding one result per probe.</summary>
    /// <param name="request">Validated IPv6 traceroute request.</param>
    /// <param name="cancellationToken">Enumerator cancellation token.</param>
    /// <returns>Async sequence of probe results; socket-init errors yield a single Error result.</returns>
    private async IAsyncEnumerable<TracerouteProbeResult> TraceIpv6Async(
        TracerouteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryOpenIcmp(AddressFamily.InterNetworkV6, ProtocolType.IcmpV6, out var icmp, out var openError))
        {
            yield return openError!;
            yield break;
        }

        using (icmp)
        {
            if (!TryBindIcmp(icmp!, AddressFamily.InterNetworkV6, request.Source, out var bindError))
            {
                yield return bindError!;
                yield break;
            }

            var identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var receiveBuffer = new byte[65535];

            for (var hopLimit = 1; hopLimit <= request.MaxTtl; hopLimit++)
            {
                var hopReachedDestination = false;

                for (var probe = 1; probe <= request.ProbesPerHop; probe++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // See Ipv6OutgoingHopLimitOption — HopLimit (21) does not control outgoing hops on Windows.
                    icmp!.SetSocketOption(SocketOptionLevel.IPv6, Ipv6OutgoingHopLimitOption, hopLimit);
                    var sequence = EncodeSequence(hopLimit, probe);
                    var (ok, result, error) = await TryProbeIpv6Async(
                            icmp,
                            request.Destination,
                            identifier,
                            sequence,
                            request.PayloadBytes,
                            TimeSpan.FromSeconds(request.TimeoutSeconds),
                            hopLimit,
                            probe,
                            receiveBuffer,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (!ok)
                    {
                        yield return error!;
                        yield break;
                    }

                    if (result!.DestinationReached)
                    {
                        hopReachedDestination = true;
                    }

                    yield return result;

                    if (result.StopTracing && !result.DestinationReached)
                    {
                        yield break;
                    }
                }

                if (hopReachedDestination)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>
    /// Opens a raw ICMP socket, mapping privilege/platform failures to a stop-tracing error result.
    /// </summary>
    /// <param name="family">Address family for the socket.</param>
    /// <param name="protocol"><see cref="ProtocolType.Icmp"/> or <see cref="ProtocolType.IcmpV6"/>.</param>
    /// <param name="icmp">Opened socket when successful; null on failure.</param>
    /// <param name="error">Error probe result when open fails.</param>
    /// <returns><see langword="true"/> when the socket was opened.</returns>
    private bool TryOpenIcmp(
        AddressFamily family,
        ProtocolType protocol,
        out Socket? icmp,
        out TracerouteProbeResult? error)
    {
        icmp = null;
        error = null;
        try
        {
            icmp = new Socket(family, SocketType.Raw, protocol);
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AccessDenied
                                             or SocketError.OperationNotSupported
                                             or SocketError.ProtocolOption)
        {
            _logger.LogWarning(ex, "Traceroute ICMP socket unavailable");
            icmp?.Dispose();
            icmp = null;
            error = SocketInitError(
                "Traceroute sockets are unavailable on this host (privileges or platform policy). " +
                ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Binds the ICMP socket via <see cref="TracerouteSocketSetup"/>, mapping failures to an error result.
    /// </summary>
    /// <param name="icmp">Unbound raw socket.</param>
    /// <param name="family">Address family.</param>
    /// <param name="source">Optional local source address.</param>
    /// <param name="error">Error probe result when bind fails.</param>
    /// <returns><see langword="true"/> when bind succeeds.</returns>
    private bool TryBindIcmp(
        Socket icmp,
        AddressFamily family,
        IPAddress? source,
        out TracerouteProbeResult? error)
    {
        error = null;
        try
        {
            var endpoint = TracerouteSocketSetup.CreateIcmpBindEndpoint(family, source);
            TracerouteSocketSetup.BindIcmpSocket(icmp, endpoint);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ArgumentException)
        {
            _logger.LogWarning(ex, "Traceroute ICMP socket bind failed");
            error = SocketInitError("Traceroute socket initialization failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Builds a terminal socket-initialization error probe result.</summary>
    /// <param name="detail">Human-readable failure detail.</param>
    /// <returns>Error result with <see cref="TracerouteProbeResult.StopTracing"/> set.</returns>
    private static TracerouteProbeResult SocketInitError(string detail) =>
        new()
        {
            Ttl = 1,
            ProbeIndex = 1,
            Status = TracerouteProbeStatus.Error,
            Detail = detail,
            StopTracing = true
        };

    /// <summary>
    /// Sends one IPv4 probe and maps socket exceptions to a stop-tracing error tuple.
    /// </summary>
    /// <returns>Tuple of success flag, probe result, and optional error result.</returns>
    private async Task<(bool Ok, TracerouteProbeResult? Result, TracerouteProbeResult? Error)> TryProbeIpv4Async(
        Socket icmp,
        IPAddress destination,
        ushort identifier,
        ushort sequence,
        int payloadBytes,
        TimeSpan timeout,
        int ttl,
        int probeIndex,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAndReceiveIpv4Async(
                    icmp,
                    destination,
                    identifier,
                    sequence,
                    payloadBytes,
                    timeout,
                    ttl,
                    probeIndex,
                    receiveBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
            return (true, result, null);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "IPv4 traceroute probe failed");
            return (false, null, new TracerouteProbeResult
            {
                Ttl = ttl,
                ProbeIndex = probeIndex,
                Status = TracerouteProbeStatus.Error,
                Detail = ex.Message,
                StopTracing = true
            });
        }
    }

    /// <summary>
    /// Sends one IPv6 probe and maps socket exceptions to a stop-tracing error tuple.
    /// </summary>
    /// <returns>Tuple of success flag, probe result, and optional error result.</returns>
    private async Task<(bool Ok, TracerouteProbeResult? Result, TracerouteProbeResult? Error)> TryProbeIpv6Async(
        Socket icmp,
        IPAddress destination,
        ushort identifier,
        ushort sequence,
        int payloadBytes,
        TimeSpan timeout,
        int hopLimit,
        int probeIndex,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await SendAndReceiveIpv6Async(
                    icmp,
                    destination,
                    identifier,
                    sequence,
                    payloadBytes,
                    timeout,
                    hopLimit,
                    probeIndex,
                    receiveBuffer,
                    cancellationToken)
                .ConfigureAwait(false);
            return (true, result, null);
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "IPv6 traceroute probe failed");
            return (false, null, new TracerouteProbeResult
            {
                Ttl = hopLimit,
                ProbeIndex = probeIndex,
                Status = TracerouteProbeStatus.Error,
                Detail = ex.Message,
                StopTracing = true
            });
        }
    }

    /// <summary>
    /// Arms receive before send, then matches IPv4 ICMP responses until timeout or match.
    /// </summary>
    /// <param name="icmp">Bound raw ICMP socket.</param>
    /// <param name="destination">Probe destination.</param>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequence">Encoded TTL/probe sequence.</param>
    /// <param name="payloadBytes">Echo payload length.</param>
    /// <param name="timeout">Per-probe timeout.</param>
    /// <param name="ttl">Current TTL.</param>
    /// <param name="probeIndex">1-based probe index.</param>
    /// <param name="receiveBuffer">Reusable receive buffer.</param>
    /// <param name="cancellationToken">Caller cancellation (timeout uses a linked token).</param>
    /// <returns>Matched, timed-out, or mapped probe result.</returns>
    private async Task<TracerouteProbeResult> SendAndReceiveIpv4Async(
        Socket icmp,
        IPAddress destination,
        ushort identifier,
        ushort sequence,
        int payloadBytes,
        TimeSpan timeout,
        int ttl,
        int probeIndex,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        // Arm receive before send so sub-millisecond Time Exceeded replies are not missed.
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        var receiveTask = icmp
            .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
            .AsTask();

        var packet = BuildIpv4EchoRequest(identifier, sequence, payloadBytes);
        var sw = Stopwatch.StartNew();
        var sent = await icmp.SendToAsync(packet, SocketFlags.None, new IPEndPoint(destination, 0), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Traceroute IPv4 probe sent Destination={Destination} Ttl={Ttl} Probe={Probe} Id={Id} Seq={Seq} Payload={Payload} SentBytes={SentBytes}",
            destination,
            ttl,
            probeIndex,
            identifier,
            sequence,
            payloadBytes,
            sent);

        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Traceroute IPv4 probe timeout Destination={Destination} Ttl={Ttl} Probe={Probe} Id={Id} Seq={Seq}",
                    destination,
                    ttl,
                    probeIndex,
                    identifier,
                    sequence);
                return TimeoutResult(ttl, probeIndex);
            }

            sw.Stop();
            var match = IcmpTracerouteParser.TryMatchIpv4(
                receiveBuffer.AsSpan(0, received.ReceivedBytes),
                identifier,
                sequence,
                out var rejectReason);

            if (match is null)
            {
                _logger.LogDebug(
                    "Traceroute IPv4 ICMP ignored Bytes={Bytes} From={From} Reason={Reason} Ttl={Ttl} Probe={Probe}",
                    received.ReceivedBytes,
                    received.RemoteEndPoint,
                    rejectReason,
                    ttl,
                    probeIndex);

                remote = new IPEndPoint(IPAddress.Any, 0);
                receiveTask = icmp
                    .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
                    .AsTask();
                sw.Start();
                continue;
            }

            _logger.LogDebug(
                "Traceroute IPv4 ICMP matched Status={Status} Responder={Responder} RttMs={RttMs} Ttl={Ttl} Probe={Probe}",
                match.Value.Status,
                match.Value.Responder,
                sw.Elapsed.TotalMilliseconds,
                ttl,
                probeIndex);

            return MapStatus(match.Value.Status, ttl, probeIndex, match.Value.Responder, sw.Elapsed);
        }
    }

    /// <summary>
    /// Arms receive before send, then matches ICMPv6 responses until timeout or match.
    /// </summary>
    /// <param name="icmp">Bound raw ICMPv6 socket.</param>
    /// <param name="destination">Probe destination.</param>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequence">Encoded hop/probe sequence.</param>
    /// <param name="payloadBytes">Echo payload length.</param>
    /// <param name="timeout">Per-probe timeout.</param>
    /// <param name="hopLimit">Current outgoing hop limit.</param>
    /// <param name="probeIndex">1-based probe index.</param>
    /// <param name="receiveBuffer">Reusable receive buffer.</param>
    /// <param name="cancellationToken">Caller cancellation (timeout uses a linked token).</param>
    /// <returns>Matched, timed-out, or mapped probe result (may carry parser diagnostic detail).</returns>
    private async Task<TracerouteProbeResult> SendAndReceiveIpv6Async(
        Socket icmp,
        IPAddress destination,
        ushort identifier,
        ushort sequence,
        int payloadBytes,
        TimeSpan timeout,
        int hopLimit,
        int probeIndex,
        byte[] receiveBuffer,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        EndPoint remote = new IPEndPoint(IPAddress.IPv6Any, 0);
        var receiveTask = icmp
            .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
            .AsTask();

        var packet = BuildIpv6EchoRequest(identifier, sequence, payloadBytes);
        var sw = Stopwatch.StartNew();
        var sent = await icmp.SendToAsync(packet, SocketFlags.None, new IPEndPoint(destination, 0), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogDebug(
            "Traceroute IPv6 probe sent Destination={Destination} HopLimit={HopLimit} Probe={Probe} Id={Id} Seq={Seq} Payload={Payload} SentBytes={SentBytes}",
            destination,
            hopLimit,
            probeIndex,
            identifier,
            sequence,
            payloadBytes,
            sent);

        while (true)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    "Traceroute IPv6 probe timeout Destination={Destination} HopLimit={HopLimit} Probe={Probe}",
                    destination,
                    hopLimit,
                    probeIndex);
                return TimeoutResult(hopLimit, probeIndex);
            }

            sw.Stop();
            var remoteEp = received.RemoteEndPoint as IPEndPoint;
            var frame = receiveBuffer.AsSpan(0, received.ReceivedBytes);
            var icmpType = frame.Length > 0 ? frame[0] : (byte)0;
            var icmpCode = frame.Length > 1 ? frame[1] : (byte)0;
            var match = IcmpTracerouteParser.TryMatchIpv6(
                frame,
                identifier,
                sequence,
                remoteEp?.Address,
                destination,
                out var rejectReason);

            if (match is null)
            {
                _logger.LogDebug(
                    "Traceroute IPv6 ICMP ignored Bytes={Bytes} From={From} Type={IcmpType} Code={IcmpCode} Reason={Reason} HopLimit={HopLimit} Probe={Probe} Id={Id} Seq={Seq}",
                    received.ReceivedBytes,
                    received.RemoteEndPoint,
                    icmpType,
                    icmpCode,
                    rejectReason,
                    hopLimit,
                    probeIndex,
                    identifier,
                    sequence);

                remote = new IPEndPoint(IPAddress.IPv6Any, 0);
                receiveTask = icmp
                    .ReceiveFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remote, timeoutCts.Token)
                    .AsTask();
                sw.Start();
                continue;
            }

            _logger.LogDebug(
                "Traceroute IPv6 ICMP matched Status={Status} Responder={Responder} Type={IcmpType} Code={IcmpCode} RttMs={RttMs} HopLimit={HopLimit} Probe={Probe} Id={Id} Seq={Seq}",
                match.Value.Status,
                match.Value.Responder,
                icmpType,
                icmpCode,
                sw.Elapsed.TotalMilliseconds,
                hopLimit,
                probeIndex,
                identifier,
                sequence);

            var mapped = MapStatus(match.Value.Status, hopLimit, probeIndex, match.Value.Responder, sw.Elapsed);
            if (string.IsNullOrWhiteSpace(match.Value.RejectReason))
            {
                return mapped;
            }

            // Parser may attach a diagnostic (e.g. Packet too big) in RejectReason on a successful match.
            return new TracerouteProbeResult
            {
                Ttl = mapped.Ttl,
                ProbeIndex = mapped.ProbeIndex,
                Status = mapped.Status,
                Responder = mapped.Responder,
                RoundTripTime = mapped.RoundTripTime,
                Detail = match.Value.RejectReason,
                DestinationReached = mapped.DestinationReached,
                StopTracing = mapped.StopTracing
            };
        }
    }

    /// <summary>
    /// Windows <c>IPV6_UNICAST_HOPS</c> (4). Exposed in .NET as <see cref="SocketOptionName.IpTimeToLive"/>.
    /// Do not use <see cref="SocketOptionName.HopLimit"/> (21 / <c>IPV6_HOPLIMIT</c>) — it does not set
    /// the outgoing unicast hop limit, which previously caused false destination-at-hop-1 results.
    /// </summary>
    internal static SocketOptionName Ipv6OutgoingHopLimitOption => SocketOptionName.IpTimeToLive;

    /// <summary>
    /// Encodes TTL/hop and probe index into the ICMP sequence field (high/low bytes).
    /// </summary>
    /// <param name="ttlOrHop">TTL or hop limit (low 8 bits used).</param>
    /// <param name="probeIndex">Probe index (low 8 bits used).</param>
    /// <returns>16-bit sequence value.</returns>
    internal static ushort EncodeSequence(int ttlOrHop, int probeIndex) =>
        (ushort)(((ttlOrHop & 0xFF) << 8) | (probeIndex & 0xFF));

    /// <summary>Builds an ICMPv4 Echo Request with checksum for traceroute probes.</summary>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequence">Encoded sequence.</param>
    /// <param name="payloadBytes">Payload length.</param>
    /// <returns>ICMP datagram without IP header.</returns>
    internal static byte[] BuildIpv4EchoRequest(ushort identifier, ushort sequence, int payloadBytes)
    {
        var packet = new byte[8 + payloadBytes];
        packet[0] = 8; // Echo Request
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), sequence);
        for (var i = 0; i < payloadBytes; i++)
        {
            packet[8 + i] = (byte)('a' + (i % 23));
        }

        var checksum = RawSocketIcmpEchoClient.ComputeInternetChecksum(packet);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), checksum);
        return packet;
    }

    /// <summary>Builds an ICMPv6 Echo Request (checksum left zero for OS fill-in).</summary>
    /// <param name="identifier">Echo identifier.</param>
    /// <param name="sequence">Encoded sequence.</param>
    /// <param name="payloadBytes">Payload length.</param>
    /// <returns>ICMPv6 datagram without IPv6 header.</returns>
    internal static byte[] BuildIpv6EchoRequest(ushort identifier, ushort sequence, int payloadBytes)
    {
        var packet = new byte[8 + payloadBytes];
        packet[0] = 128; // Echo Request
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), identifier);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), sequence);
        for (var i = 0; i < payloadBytes; i++)
        {
            packet[8 + i] = (byte)('a' + (i % 23));
        }

        return packet;
    }

    /// <summary>Creates a timed-out probe result for the given hop/probe.</summary>
    /// <param name="ttl">TTL or hop limit.</param>
    /// <param name="probeIndex">Probe index.</param>
    /// <returns>Timed-out result without responder or RTT.</returns>
    private static TracerouteProbeResult TimeoutResult(int ttl, int probeIndex) =>
        new()
        {
            Ttl = ttl,
            ProbeIndex = probeIndex,
            Status = TracerouteProbeStatus.TimedOut
        };

    /// <summary>
    /// Maps a parser status to a probe result, setting destination-reached and stop-tracing flags.
    /// </summary>
    /// <param name="status">Matched traceroute status.</param>
    /// <param name="ttl">TTL or hop limit.</param>
    /// <param name="probeIndex">Probe index.</param>
    /// <param name="responder">Responding address when known.</param>
    /// <param name="rtt">Measured round-trip time.</param>
    /// <returns>
    /// Result with <see cref="TracerouteProbeResult.DestinationReached"/> for Echo Reply /
    /// Port Unreachable, and <see cref="TracerouteProbeResult.StopTracing"/> for unreachable /
    /// prohibited / error.
    /// </returns>
    private static TracerouteProbeResult MapStatus(
        TracerouteProbeStatus status,
        int ttl,
        int probeIndex,
        IPAddress? responder,
        TimeSpan rtt)
    {
        var reached = status is TracerouteProbeStatus.EchoReply or TracerouteProbeStatus.PortUnreachable;
        var stop = status is TracerouteProbeStatus.DestinationUnreachable
            or TracerouteProbeStatus.AdministrativelyProhibited
            or TracerouteProbeStatus.Error;

        var detail = status switch
        {
            TracerouteProbeStatus.DestinationUnreachable => "Destination unreachable",
            TracerouteProbeStatus.AdministrativelyProhibited => "Administratively prohibited",
            _ => null
        };

        return new TracerouteProbeResult
        {
            Ttl = ttl,
            ProbeIndex = probeIndex,
            Status = status,
            Responder = responder,
            RoundTripTime = rtt,
            Detail = detail,
            DestinationReached = reached,
            StopTracing = stop
        };
    }
}
