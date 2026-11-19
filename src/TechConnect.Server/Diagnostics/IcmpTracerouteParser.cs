// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IcmpTracerouteParser.cs
//
// Purpose:
// Parses ICMP/ICMPv6 responses for Echo-Request traceroute (TTL expiry and replies)
// including embedded original Echo matching by identifier and sequence.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Parses ICMP / ICMPv6 responses for Echo-Request traceroute (TTL/Hop Limit expiry and replies).
/// </summary>
/// <remarks>
/// IPv4 matching expects Windows-style IP+ICMP delivery, with a fallback for ICMP-only frames.
/// IPv6 matching expects ICMP-header-first frames as returned by Windows raw ICMPv6 sockets.
/// Non-matching packets return <see langword="null"/> with a diagnostic <c>rejectReason</c>.
/// </remarks>
internal static class IcmpTracerouteParser
{
    /// <summary>ICMPv4 Echo Reply.</summary>
    private const byte Icmpv4EchoReply = 0;

    /// <summary>ICMPv4 Destination Unreachable.</summary>
    private const byte Icmpv4DestUnreachable = 3;

    /// <summary>ICMPv4 Echo Request (embedded original).</summary>
    private const byte Icmpv4EchoRequest = 8;

    /// <summary>ICMPv4 Time Exceeded.</summary>
    private const byte Icmpv4TimeExceeded = 11;

    /// <summary>ICMPv4 Port Unreachable code (treated as destination unreachable).</summary>
    private const byte Icmpv4PortUnreachable = 3;

    /// <summary>ICMPv4 Communication Administratively Prohibited code.</summary>
    private const byte Icmpv4AdminProhibited = 13;

    /// <summary>ICMPv6 Destination Unreachable.</summary>
    private const byte Icmpv6DestUnreachable = 1;

    /// <summary>ICMPv6 Packet Too Big.</summary>
    private const byte Icmpv6PacketTooBig = 2;

    /// <summary>ICMPv6 Time Exceeded.</summary>
    private const byte Icmpv6TimeExceeded = 3;

    /// <summary>ICMPv6 Parameter Problem.</summary>
    private const byte Icmpv6ParameterProblem = 4;

    /// <summary>ICMPv6 Echo Request (ignored when looped back).</summary>
    private const byte Icmpv6EchoRequest = 128;

    /// <summary>ICMPv6 Echo Reply.</summary>
    private const byte Icmpv6EchoReply = 129;

    /// <summary>ICMPv6 Destination Unreachable — administratively prohibited code.</summary>
    private const byte Icmpv6AdminProhibited = 1;

    /// <summary>ICMPv6 Destination Unreachable — port unreachable code.</summary>
    private const byte Icmpv6PortUnreachable = 4;

    /// <summary>
    /// Successful match of a traceroute-related ICMP message to the expected Echo identifiers.
    /// </summary>
    /// <param name="Status">Mapped probe status.</param>
    /// <param name="Responder">Responding address when known.</param>
    /// <param name="RejectReason">
    /// Optional diagnostic retained on some successful matches (e.g. Packet too big);
    /// also unused on pure matches (<see langword="null"/>).
    /// </param>
    public readonly record struct MatchResult(
        TracerouteProbeStatus Status,
        IPAddress? Responder,
        string? RejectReason);

    /// <summary>
    /// Attempts to match an IPv4 receive frame to the expected Echo Request id/sequence.
    /// </summary>
    /// <param name="frame">Raw receive buffer (typically IP header + ICMP).</param>
    /// <param name="expectedId">Expected Echo identifier.</param>
    /// <param name="expectedSeq">Expected Echo sequence.</param>
    /// <param name="rejectReason">Why the frame was ignored when the return is null.</param>
    /// <returns>Match result, or <see langword="null"/> when the frame should be ignored.</returns>
    public static MatchResult? TryMatchIpv4(
        ReadOnlySpan<byte> frame,
        ushort expectedId,
        ushort expectedSeq,
        out string? rejectReason)
    {
        rejectReason = null;
        if (frame.Length < 20)
        {
            rejectReason = "frame too short for IPv4 header";
            return null;
        }

        // Windows raw ICMP delivers IP header + ICMP.
        if ((frame[0] >> 4) != 4)
        {
            // Some stacks may deliver ICMP-only; try that layout.
            return TryMatchIpv4IcmpOnly(frame, expectedId, expectedSeq, remoteHint: null, out rejectReason);
        }

        var ihl = (frame[0] & 0x0F) * 4;
        if (ihl < 20 || frame.Length < ihl + 8)
        {
            rejectReason = "invalid IPv4 IHL or truncated ICMP";
            return null;
        }

        var responder = new IPAddress(frame.Slice(12, 4));
        return TryMatchIpv4IcmpOnly(frame[ihl..], expectedId, expectedSeq, responder, out rejectReason);
    }

    /// <summary>
    /// Matches an ICMPv4 payload (no outer IP header) against the expected Echo identifiers.
    /// </summary>
    /// <param name="icmp">ICMP payload starting at type/code.</param>
    /// <param name="expectedId">Expected Echo identifier.</param>
    /// <param name="expectedSeq">Expected Echo sequence.</param>
    /// <param name="remoteHint">Responder address from the outer IP header when available.</param>
    /// <param name="rejectReason">Ignore reason when null is returned.</param>
    /// <returns>Match result, or null to ignore.</returns>
    private static MatchResult? TryMatchIpv4IcmpOnly(
        ReadOnlySpan<byte> icmp,
        ushort expectedId,
        ushort expectedSeq,
        IPAddress? remoteHint,
        out string? rejectReason)
    {
        rejectReason = null;
        if (icmp.Length < 8)
        {
            rejectReason = "ICMP truncated";
            return null;
        }

        var type = icmp[0];
        var code = icmp[1];

        if (type == Icmpv4EchoReply)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(4, 2));
            var seq = BinaryPrimitives.ReadUInt16BigEndian(icmp.Slice(6, 2));
            if (id != expectedId || seq != expectedSeq)
            {
                rejectReason = $"echo reply id/seq mismatch (got {id}/{seq}, expected {expectedId}/{expectedSeq})";
                return null;
            }

            return new MatchResult(TracerouteProbeStatus.EchoReply, remoteHint, null);
        }

        if (type is not (Icmpv4TimeExceeded or Icmpv4DestUnreachable))
        {
            rejectReason = $"ignored ICMP type {type} code {code}";
            return null;
        }

        if (!TryExtractEmbeddedEcho(icmp, AddressFamily.InterNetwork, out var id2, out var seq2, out var extractReason))
        {
            rejectReason = extractReason;
            return null;
        }

        if (id2 != expectedId || seq2 != expectedSeq)
        {
            rejectReason = $"embedded echo id/seq mismatch (got {id2}/{seq2}, expected {expectedId}/{expectedSeq})";
            return null;
        }

        if (type == Icmpv4TimeExceeded)
        {
            return new MatchResult(TracerouteProbeStatus.TimeExceeded, remoteHint, null);
        }

        var status = code switch
        {
            Icmpv4AdminProhibited => TracerouteProbeStatus.AdministrativelyProhibited,
            Icmpv4PortUnreachable => TracerouteProbeStatus.DestinationUnreachable,
            _ => TracerouteProbeStatus.DestinationUnreachable
        };
        return new MatchResult(status, remoteHint, null);
    }

    /// <summary>
    /// Parses a raw ICMPv6 frame as returned by Windows (ICMP header first; no outer IPv6 header).
    /// </summary>
    /// <param name="frame">ICMPv6 datagram.</param>
    /// <param name="expectedId">Expected Echo identifier.</param>
    /// <param name="expectedSeq">Expected Echo sequence.</param>
    /// <param name="remoteResponder">Receive-from source address.</param>
    /// <param name="expectedDestination">
    /// Trace destination; Echo Reply requires <paramref name="remoteResponder"/> to equal this address.
    /// </param>
    /// <param name="rejectReason">Ignore reason when null is returned.</param>
    /// <returns>Match result, or null to ignore.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectedDestination"/> is null.</exception>
    public static MatchResult? TryMatchIpv6(
        ReadOnlySpan<byte> frame,
        ushort expectedId,
        ushort expectedSeq,
        IPAddress? remoteResponder,
        IPAddress expectedDestination,
        out string? rejectReason)
    {
        ArgumentNullException.ThrowIfNull(expectedDestination);
        rejectReason = null;
        if (frame.Length < 8)
        {
            rejectReason = "ICMPv6 truncated";
            return null;
        }

        // Windows raw ICMPv6 delivers the ICMPv6 header without an outer IPv6 header.
        // Echo Reply is typically 8 + payload bytes; Time Exceeded / Dest Unreachable embed
        // the original IPv6 header (40) + leading bytes of the Echo Request.
        var type = frame[0];
        var code = frame[1];

        if (type == Icmpv6EchoRequest)
        {
            rejectReason = "ignored ICMPv6 Echo Request (loopback/copy of outbound probe)";
            return null;
        }

        if (type == Icmpv6EchoReply)
        {
            var id = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(4, 2));
            var seq = BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(6, 2));
            if (id != expectedId || seq != expectedSeq)
            {
                rejectReason = $"echo reply id/seq mismatch (got {id}/{seq}, expected {expectedId}/{expectedSeq})";
                return null;
            }

            if (remoteResponder is null || !Ipv6AddressesEqual(remoteResponder, expectedDestination))
            {
                rejectReason =
                    $"echo reply source {remoteResponder} does not match destination {expectedDestination}";
                return null;
            }

            return new MatchResult(TracerouteProbeStatus.EchoReply, remoteResponder, null);
        }

        if (type is not (Icmpv6TimeExceeded or Icmpv6DestUnreachable or Icmpv6PacketTooBig
            or Icmpv6ParameterProblem))
        {
            rejectReason = $"ignored ICMPv6 type {type} code {code}";
            return null;
        }

        if (!TryExtractEmbeddedEcho(frame, AddressFamily.InterNetworkV6, out var id2, out var seq2, out var extractReason))
        {
            rejectReason = extractReason;
            return null;
        }

        if (id2 != expectedId || seq2 != expectedSeq)
        {
            rejectReason = $"embedded echo id/seq mismatch (got {id2}/{seq2}, expected {expectedId}/{expectedSeq})";
            return null;
        }

        if (type == Icmpv6TimeExceeded)
        {
            return new MatchResult(TracerouteProbeStatus.TimeExceeded, remoteResponder, null);
        }

        if (type == Icmpv6PacketTooBig)
        {
            return new MatchResult(
                TracerouteProbeStatus.DestinationUnreachable,
                remoteResponder,
                "Packet too big");
        }

        if (type == Icmpv6ParameterProblem)
        {
            return new MatchResult(
                TracerouteProbeStatus.DestinationUnreachable,
                remoteResponder,
                "Parameter problem");
        }

        var status = code switch
        {
            Icmpv6AdminProhibited => TracerouteProbeStatus.AdministrativelyProhibited,
            Icmpv6PortUnreachable => TracerouteProbeStatus.DestinationUnreachable,
            _ => TracerouteProbeStatus.DestinationUnreachable
        };
        return new MatchResult(status, remoteResponder, null);
    }

    /// <summary>
    /// Compares IPv6 addresses by address bytes (ignores formatting / scope id for equality of globals).
    /// </summary>
    /// <param name="left">First address.</param>
    /// <param name="right">Second address.</param>
    /// <returns><see langword="true"/> when equal via <see cref="IPAddress.Equals(object?)"/> or 16-byte compare.</returns>
    internal static bool Ipv6AddressesEqual(IPAddress left, IPAddress right)
    {
        if (left.Equals(right))
        {
            return true;
        }

        if (left.AddressFamily != AddressFamily.InterNetworkV6 ||
            right.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        if (!left.TryWriteBytes(a, out var writtenA) || writtenA != 16 ||
            !right.TryWriteBytes(b, out var writtenB) || writtenB != 16)
        {
            return false;
        }

        return a.SequenceEqual(b);
    }

    /// <summary>
    /// Extracts identifier/sequence from the original Echo Request embedded in Time Exceeded /
    /// Destination Unreachable payloads.
    /// </summary>
    /// <param name="icmp">Outer ICMP message including the 8-byte header.</param>
    /// <param name="family">Original probe family (IPv4 or IPv6 embedding layout).</param>
    /// <param name="id">Extracted Echo identifier.</param>
    /// <param name="seq">Extracted Echo sequence.</param>
    /// <param name="reason">Failure reason when extraction fails.</param>
    /// <returns><see langword="true"/> when an embedded Echo Request was extracted.</returns>
    private static bool TryExtractEmbeddedEcho(
        ReadOnlySpan<byte> icmp,
        AddressFamily family,
        out ushort id,
        out ushort seq,
        out string? reason)
    {
        id = 0;
        seq = 0;
        reason = null;

        if (family == AddressFamily.InterNetwork)
        {
            if (icmp.Length < 8 + 20 + 8)
            {
                reason = "embedded IPv4+ICMP truncated";
                return false;
            }

            var embeddedIp = icmp[8..];
            var embeddedIhl = (embeddedIp[0] & 0x0F) * 4;
            if (embeddedIhl < 20 || icmp.Length < 8 + embeddedIhl + 8)
            {
                reason = "embedded IPv4 IHL invalid";
                return false;
            }

            if (embeddedIp[9] != (byte)ProtocolType.Icmp)
            {
                reason = $"embedded protocol {embeddedIp[9]} is not ICMP";
                return false;
            }

            var embeddedIcmp = embeddedIp[embeddedIhl..];
            if (embeddedIcmp[0] != Icmpv4EchoRequest)
            {
                reason = $"embedded ICMP type {embeddedIcmp[0]} is not Echo Request";
                return false;
            }

            id = BinaryPrimitives.ReadUInt16BigEndian(embeddedIcmp.Slice(4, 2));
            seq = BinaryPrimitives.ReadUInt16BigEndian(embeddedIcmp.Slice(6, 2));
            return true;
        }

        if (icmp.Length < 8 + 40 + 8)
        {
            reason = "embedded IPv6+ICMPv6 truncated";
            return false;
        }

        var ipv6 = icmp[8..];
        if ((ipv6[0] >> 4) != 6)
        {
            reason = "embedded packet is not IPv6";
            return false;
        }

        if (ipv6[6] != (byte)ProtocolType.IcmpV6)
        {
            reason = $"embedded next-header {ipv6[6]} is not ICMPv6";
            return false;
        }

        var embedded = ipv6[40..];
        if (embedded[0] != Icmpv6EchoRequest)
        {
            reason = $"embedded ICMPv6 type {embedded[0]} is not Echo Request";
            return false;
        }

        id = BinaryPrimitives.ReadUInt16BigEndian(embedded.Slice(4, 2));
        seq = BinaryPrimitives.ReadUInt16BigEndian(embedded.Slice(6, 2));
        return true;
    }
}
