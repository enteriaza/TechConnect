// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    IcmpTracerouteParserTests.cs
//
// Purpose:
// Verifies IcmpTracerouteParser matching of Echo Reply, Time Exceeded, and unreachable frames.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for ICMP/ICMPv6 traceroute response parsing.
/// </summary>
public sealed class IcmpTracerouteParserTests
{
    /// <summary>Documentation IPv6 destination used as the expected traceroute target.</summary>
    private static readonly IPAddress Dest = IPAddress.Parse("2001:4860:4860::8888");
    /// <summary>Documentation IPv6 intermediate router source for Time Exceeded fixtures.</summary>
    private static readonly IPAddress Router = IPAddress.Parse("2c0f:f030:1442:501::1");

    /// <summary>
    /// IPv4 Time Exceeded with matching embedded echo id/seq classifies as TimeExceeded from the responder.
    /// </summary>
    [Fact]
    public void MatchIpv4_TimeExceeded_CorrelatesEmbeddedEcho()
    {
        const ushort id = 0x1234;
        const ushort seq = 0x0101;
        var frame = BuildIpv4TimeExceeded(
            responder: IPAddress.Parse("198.18.0.65"),
            id,
            seq);

        var match = IcmpTracerouteParser.TryMatchIpv4(frame, id, seq, out var reject);
        reject.Should().BeNull();
        match.Should().NotBeNull();
        match!.Value.Status.Should().Be(TracerouteProbeStatus.TimeExceeded);
        match.Value.Responder.Should().Be(IPAddress.Parse("198.18.0.65"));
    }

    /// <summary>
    /// IPv4 Echo Reply with matching id/seq classifies as EchoReply from the destination.
    /// </summary>
    [Fact]
    public void MatchIpv4_EchoReply_MarksDestinationReached()
    {
        const ushort id = 0x1234;
        const ushort seq = 0x0201;
        var frame = BuildIpv4EchoReply(IPAddress.Parse("198.18.255.255"), id, seq);

        var match = IcmpTracerouteParser.TryMatchIpv4(frame, id, seq, out _);
        match!.Value.Status.Should().Be(TracerouteProbeStatus.EchoReply);
        match.Value.Responder.Should().Be(IPAddress.Parse("198.18.255.255"));
    }

    /// <summary>
    /// Frames whose embedded id/seq do not match are rejected with a mismatch reason.
    /// </summary>
    [Fact]
    public void MatchIpv4_UnrelatedId_RejectedWithReason()
    {
        var frame = BuildIpv4TimeExceeded(IPAddress.Parse("10.0.0.1"), 1, 1);
        IcmpTracerouteParser.TryMatchIpv4(frame, expectedId: 9, expectedSeq: 9, out var reject)
            .Should().BeNull();
        reject.Should().Contain("mismatch");
    }

    /// <summary>
    /// Truncated IPv4 frames are rejected with a non-empty reason.
    /// </summary>
    [Fact]
    public void MatchIpv4_Truncated_Rejected()
    {
        IcmpTracerouteParser.TryMatchIpv4(new byte[10], 1, 1, out var reject)
            .Should().BeNull();
        reject.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// Destination Unreachable code 13 maps to AdministrativelyProhibited.
    /// </summary>
    [Fact]
    public void MatchIpv4_AdminProhibited()
    {
        var frame = BuildIpv4DestUnreachable(
            IPAddress.Parse("10.0.0.1"),
            code: 13,
            id: 7,
            seq: 3);
        var match = IcmpTracerouteParser.TryMatchIpv4(frame, 7, 3, out _);
        match!.Value.Status.Should().Be(TracerouteProbeStatus.AdministrativelyProhibited);
    }

    /// <summary>
    /// Matching sequence with a stale identifier is still rejected as mismatch.
    /// </summary>
    [Fact]
    public void MatchIpv4_StaleIdentifier_Rejected()
    {
        var frame = BuildIpv4TimeExceeded(IPAddress.Parse("10.0.0.1"), id: 100, seq: 0x0101);
        IcmpTracerouteParser.TryMatchIpv4(frame, expectedId: 200, expectedSeq: 0x0101, out var reject)
            .Should().BeNull();
        reject.Should().Contain("mismatch");
    }

    /// <summary>
    /// Matching id with wrong sequence is rejected (out-of-order / unmatched probe).
    /// </summary>
    [Fact]
    public void MatchIpv4_WrongSequence_Rejected_AsOutOfOrderOrUnmatched()
    {
        var frame = BuildIpv4EchoReply(IPAddress.Parse("8.8.8.8"), id: 5, seq: 0x0201);
        IcmpTracerouteParser.TryMatchIpv4(frame, expectedId: 5, expectedSeq: 0x0101, out var reject)
            .Should().BeNull();
        reject.Should().Contain("mismatch");
    }

    /// <summary>
    /// Re-matching the same valid Echo Reply frame is deterministic (EchoReply both times).
    /// </summary>
    [Fact]
    public void MatchIpv4_DuplicateCorrectMatch_IsDeterministic()
    {
        var frame = BuildIpv4EchoReply(IPAddress.Parse("8.8.8.8"), id: 5, seq: 0x0101);
        var first = IcmpTracerouteParser.TryMatchIpv4(frame, 5, 0x0101, out _);
        var second = IcmpTracerouteParser.TryMatchIpv4(frame, 5, 0x0101, out _);
        first!.Value.Status.Should().Be(TracerouteProbeStatus.EchoReply);
        second!.Value.Status.Should().Be(TracerouteProbeStatus.EchoReply);
    }

    /// <summary>
    /// IPv6 Time Exceeded reports the intermediate router, not EchoReply from the destination.
    /// </summary>
    [Fact]
    public void MatchIpv6_TimeExceeded_ReportsIntermediateRouter_NotDestination()
    {
        const ushort id = 99;
        const ushort seq = 0x0101;
        var te = BuildIpv6TimeExceeded(id, seq);
        var match = IcmpTracerouteParser.TryMatchIpv6(te, id, seq, Router, Dest, out var reject);
        reject.Should().BeNull();
        match!.Value.Status.Should().Be(TracerouteProbeStatus.TimeExceeded);
        match.Value.Responder.Should().Be(Router);
        match.Value.Status.Should().NotBe(TracerouteProbeStatus.EchoReply);
    }

    /// <summary>
    /// IPv6 Echo Reply is accepted only when the packet source equals the destination.
    /// </summary>
    [Fact]
    public void MatchIpv6_EchoReply_RequiresSourceEqualDestination()
    {
        const ushort id = 99;
        const ushort seq = 0x0A02;
        var reply = BuildIpv6EchoReply(id, seq);
        var match = IcmpTracerouteParser.TryMatchIpv6(reply, id, seq, Dest, Dest, out var reject);
        reject.Should().BeNull();
        match!.Value.Status.Should().Be(TracerouteProbeStatus.EchoReply);
        match.Value.Responder.Should().Be(Dest);
    }

    /// <summary>
    /// Echo Reply from a non-destination source is rejected; Time Exceeded from the router stays TimeExceeded.
    /// </summary>
    [Fact]
    public void MatchIpv6_EchoReply_WrongSource_Rejected_PreventsFalseHop1Destination()
    {
        // Regression: with HopLimit option mis-applied, Echo Reply arrives from the target at "hop 1".
        // Even with matching id/seq, a reply from an unexpected source must not count — and when the
        // option is fixed, hop-1 Time Exceeded from the router must not be classified as Echo Reply.
        const ushort id = 7;
        const ushort seq = 0x0101;
        var reply = BuildIpv6EchoReply(id, seq);

        IcmpTracerouteParser.TryMatchIpv6(reply, id, seq, Router, Dest, out var reject)
            .Should().BeNull();
        reject.Should().Contain("does not match destination");

        var te = BuildIpv6TimeExceeded(id, seq);
        var teMatch = IcmpTracerouteParser.TryMatchIpv6(te, id, seq, Router, Dest, out _);
        teMatch!.Value.Status.Should().Be(TracerouteProbeStatus.TimeExceeded);
        teMatch.Value.Responder.Should().Be(Router);
    }

    /// <summary>
    /// Outer Time Exceeded type wins; embedded Echo Request bytes are not treated as Echo Reply.
    /// </summary>
    [Fact]
    public void MatchIpv6_EmbeddedEchoRequest_IsNotTreatedAsEchoReply()
    {
        const ushort id = 1;
        const ushort seq = 1;
        var te = BuildIpv6TimeExceeded(id, seq);
        // Type byte of outer message is Time Exceeded (3), not Echo Reply (129).
        te[0].Should().Be(3);
        var match = IcmpTracerouteParser.TryMatchIpv6(te, id, seq, Router, Dest, out _);
        match!.Value.Status.Should().Be(TracerouteProbeStatus.TimeExceeded);
    }

    /// <summary>
    /// Captured outbound Echo Request copies are ignored (not matched as replies).
    /// </summary>
    [Fact]
    public void MatchIpv6_OutboundEchoRequestCopy_Ignored()
    {
        var frame = new byte[48];
        frame[0] = 128; // Echo Request
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6), 0x0101);
        IcmpTracerouteParser.TryMatchIpv6(frame, 1, 0x0101, Dest, Dest, out var reject)
            .Should().BeNull();
        reject.Should().Contain("Echo Request");
    }

    /// <summary>
    /// Dest Unreachable and Packet Too Big map to DestinationUnreachable (with reject detail for PTB).
    /// </summary>
    [Fact]
    public void MatchIpv6_DestUnreachable_AndPacketTooBig()
    {
        const ushort id = 3;
        const ushort seq = 0x0201;
        var unreachable = BuildIpv6Error(type: 1, code: 4, id, seq);
        var matchU = IcmpTracerouteParser.TryMatchIpv6(unreachable, id, seq, Router, Dest, out _);
        matchU!.Value.Status.Should().Be(TracerouteProbeStatus.DestinationUnreachable);

        var tooBig = BuildIpv6Error(type: 2, code: 0, id, seq);
        var matchT = IcmpTracerouteParser.TryMatchIpv6(tooBig, id, seq, Router, Dest, out _);
        matchT!.Value.Status.Should().Be(TracerouteProbeStatus.DestinationUnreachable);
        matchT.Value.RejectReason.Should().Contain("Packet too big");
    }

    /// <summary>
    /// Wrong IPv6 identifier or sequence is rejected with a mismatch reason.
    /// </summary>
    [Fact]
    public void MatchIpv6_WrongIdentifier_AndSequence_Rejected()
    {
        var frame = BuildIpv6EchoReply(id: 1, seq: 0x0101);
        IcmpTracerouteParser.TryMatchIpv6(frame, 9, 0x0101, Dest, Dest, out var rejectId)
            .Should().BeNull();
        rejectId.Should().Contain("mismatch");

        IcmpTracerouteParser.TryMatchIpv6(frame, 1, 0x0202, Dest, Dest, out var rejectSeq)
            .Should().BeNull();
        rejectSeq.Should().Contain("mismatch");
    }

    /// <summary>
    /// Truncated ICMPv6 and non-IPv6 embedded headers are rejected with clear reasons.
    /// </summary>
    [Fact]
    public void MatchIpv6_TruncatedAndMalformed_Rejected()
    {
        IcmpTracerouteParser.TryMatchIpv6(new byte[4], 1, 1, Dest, Dest, out var trunc)
            .Should().BeNull();
        trunc.Should().Contain("truncated");

        var bad = BuildIpv6TimeExceeded(1, 1);
        bad[8] = 0x40; // not IPv6 version
        IcmpTracerouteParser.TryMatchIpv6(bad, 1, 1, Router, Dest, out var mal)
            .Should().BeNull();
        mal.Should().Contain("not IPv6");
    }

    /// <summary>
    /// Equivalent IPv6 textual forms compare equal and still match Echo Reply destination checks.
    /// </summary>
    [Fact]
    public void MatchIpv6_EquivalentAddressText_StillMatchesDestination()
    {
        var destCompressed = IPAddress.Parse("2001:4860:4860:0:0:0:0:8888");
        var destShort = IPAddress.Parse("2001:4860:4860::8888");
        IcmpTracerouteParser.Ipv6AddressesEqual(destCompressed, destShort).Should().BeTrue();

        var reply = BuildIpv6EchoReply(5, 0x0101);
        var match = IcmpTracerouteParser.TryMatchIpv6(reply, 5, 0x0101, destCompressed, destShort, out _);
        match!.Value.Status.Should().Be(TracerouteProbeStatus.EchoReply);
    }

    /// <summary>
    /// Documents Windows mapping: outgoing Hop Limit uses IpTimeToLive (4), not HopLimit (21).
    /// </summary>
    [Fact]
    public void Ipv6OutgoingHopLimitOption_IsUnicastHops_NotHopLimit21()
    {
        // Documents the Windows mapping that caused false destination-at-hop-1.
        RawSocketIcmpTracerouteClient.Ipv6OutgoingHopLimitOption.Should().Be(SocketOptionName.IpTimeToLive);
        ((int)RawSocketIcmpTracerouteClient.Ipv6OutgoingHopLimitOption).Should().Be(4);
        ((int)SocketOptionName.HopLimit).Should().Be(21);
        RawSocketIcmpTracerouteClient.Ipv6OutgoingHopLimitOption.Should().NotBe(SocketOptionName.HopLimit);
    }

    /// <summary>
    /// Probe sequence encoding packs hop/probe indices into a ushort deterministically.
    /// </summary>
    [Fact]
    public void EncodeSequence_IsDeterministic()
    {
        RawSocketIcmpTracerouteClient.EncodeSequence(1, 1).Should().Be(0x0101);
        RawSocketIcmpTracerouteClient.EncodeSequence(2, 3).Should().Be(0x0203);
    }

    /// <summary>
    /// Built IPv4 Echo Request type is 8 and has a valid Internet checksum.
    /// </summary>
    [Fact]
    public void BuildIpv4EchoRequest_HasValidChecksum()
    {
        var packet = RawSocketIcmpTracerouteClient.BuildIpv4EchoRequest(0x11, 0x22, 40);
        packet[0].Should().Be(8);
        RawSocketIcmpEchoClient.ComputeInternetChecksum(packet).Should().Be(0);
    }
    /// <summary>Minimal IPv4 ICMP Time Exceeded with embedded Echo Request id/seq.</summary>
    private static byte[] BuildIpv4TimeExceeded(IPAddress responder, ushort id, ushort seq)
    {
        var frame = new byte[20 + 8 + 20 + 8];
        frame[0] = 0x45;
        frame[9] = 1;
        responder.TryWriteBytes(frame.AsSpan(12, 4), out _);
        var icmp = frame.AsSpan(20);
        icmp[0] = 11;
        icmp[1] = 0;
        var emb = icmp[8..];
        emb[0] = 0x45;
        emb[9] = 1;
        emb[20] = 8; // echo request
        BinaryPrimitives.WriteUInt16BigEndian(emb.Slice(24, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(emb.Slice(26, 2), seq);
        return frame;
    }
    /// <summary>Minimal IPv4 ICMP Echo Reply with id/seq.</summary>
    private static byte[] BuildIpv4EchoReply(IPAddress responder, ushort id, ushort seq)
    {
        var frame = new byte[20 + 8];
        frame[0] = 0x45;
        frame[9] = 1;
        responder.TryWriteBytes(frame.AsSpan(12, 4), out _);
        var icmp = frame.AsSpan(20);
        icmp[0] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(4, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(icmp.Slice(6, 2), seq);
        return frame;
    }
    /// <summary>IPv4 Destination Unreachable (type 3) with the given code over a Time Exceeded template.</summary>
    private static byte[] BuildIpv4DestUnreachable(IPAddress responder, byte code, ushort id, ushort seq)
    {
        var frame = BuildIpv4TimeExceeded(responder, id, seq);
        frame[20] = 3;
        frame[21] = code;
        return frame;
    }
    /// <summary>ICMPv6 Time Exceeded (type 3) with embedded Echo Request.</summary>
    private static byte[] BuildIpv6TimeExceeded(ushort id, ushort seq) =>
        BuildIpv6Error(type: 3, code: 0, id, seq);

    /// <summary>ICMPv6 error message with embedded IPv6 + Echo Request id/seq.</summary>
    private static byte[] BuildIpv6Error(byte type, byte code, ushort id, ushort seq)
    {
        var frame = new byte[8 + 40 + 8];
        frame[0] = type;
        frame[1] = code;
        var ipv6 = frame.AsSpan(8);
        ipv6[0] = 0x60;
        ipv6[6] = 58; // ICMPv6
        var emb = ipv6[40..];
        emb[0] = 128;
        BinaryPrimitives.WriteUInt16BigEndian(emb.Slice(4, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(emb.Slice(6, 2), seq);
        return frame;
    }
    /// <summary>Minimal ICMPv6 Echo Reply (type 129) with id/seq.</summary>
    private static byte[] BuildIpv6EchoReply(ushort id, ushort seq)
    {
        var frame = new byte[8];
        frame[0] = 129;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(6, 2), seq);
        return frame;
    }
}
