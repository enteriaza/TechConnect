// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DnsWireCodecTests.cs
//
// Purpose:
// Verifies DNS wire encode/decode, compression pointer safety, and PTR name construction.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Text;
using FluentAssertions;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Dns;

/// <summary>
/// Unit tests for DnsWireCodec.
/// </summary>
public sealed class DnsWireCodecTests
{
    /// <summary>
    /// <see cref="DnsWireCodec.BuildQuery"/> encodes TXID, RD bit, QNAME labels, and A/IN at the end.
    /// </summary>
    [Fact]
    public void BuildQuery_EncodesARecordWithRd()
    {
        var packet = DnsWireCodec.BuildQuery(0x1234, "Example.COM.", DnsRecordType.A);
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(0, 2)).Should().Be(0x1234);
        packet[2].Should().Be(0x01); // RD
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2)).Should().Be(1);
        packet.Should().ContainInOrder((byte)7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e');
        var qtypeOffset = packet.Length - 4;
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(qtypeOffset, 2)).Should().Be((ushort)DnsRecordType.A);
        BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(qtypeOffset + 2, 2)).Should().Be(1);
    }

    /// <summary>
    /// PTR name construction reverses IPv4 octets and IPv6 nibbles into arpa names.
    /// </summary>
    [Fact]
    public void BuildPtrName_Ipv4AndIpv6()
    {
        DnsWireCodec.BuildPtrName(IPAddress.Parse("1.2.3.4"))
            .Should().Be("4.3.2.1.in-addr.arpa");
        DnsWireCodec.BuildPtrName(IPAddress.Parse("2001:db8::1"))
            .Should().Be("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa");
    }

    /// <summary>
    /// Parses A (with compression) and AAAA (uncompressed) answer RDATA into addresses.
    /// </summary>
    [Fact]
    public void TryParseResponse_A_And_Aaaa()
    {
        var aQuery = DnsWireCodec.BuildQuery(1, "a.example", DnsRecordType.A);
        var aResponse = BuildAddressResponse(
            aQuery,
            1,
            "a.example",
            DnsRecordType.A,
            IPAddress.Parse("10.0.0.9"),
            compress: true);
        DnsWireCodec.TryParseResponse(aResponse, 1, "a.example", DnsRecordType.A, out var aParsed, out var aError)
            .Should().BeTrue(aError);
        aParsed.ResponseCode.Should().Be(DnsResponseCode.NoError);
        aParsed.Addresses.Should().Equal(IPAddress.Parse("10.0.0.9"));

        var aaaaQuery = DnsWireCodec.BuildQuery(2, "aaaa.example", DnsRecordType.Aaaa);
        var aaaaResponse = BuildAddressResponse(
            aaaaQuery,
            2,
            "aaaa.example",
            DnsRecordType.Aaaa,
            IPAddress.Parse("2001:db8::9"),
            compress: false);
        DnsWireCodec.TryParseResponse(
                aaaaResponse,
                2,
                "aaaa.example",
                DnsRecordType.Aaaa,
                out var aaaaParsed,
                out var aaaaError)
            .Should().BeTrue(aaaaError);
        aaaaParsed.Addresses.Should().Equal(IPAddress.Parse("2001:db8::9"));
    }

    /// <summary>
    /// PTR answers with a compression pointer to the question name decode the PTR hostname.
    /// </summary>
    [Fact]
    public void TryParseResponse_Ptr_WithCompression()
    {
        var ptrName = DnsWireCodec.BuildPtrName(IPAddress.Parse("8.8.8.8"));
        var query = DnsWireCodec.BuildQuery(9, ptrName, DnsRecordType.Ptr);
        var response = BuildPtrResponse(query, 9, ptrName, "dns.google", compress: true);
        DnsWireCodec.TryParseResponse(response, 9, ptrName, DnsRecordType.Ptr, out var parsed, out var error)
            .Should().BeTrue(error);
        parsed.PtrHostname.Should().Be("dns.google");
    }

    /// <summary>
    /// Non-NoError RCODEs parse successfully with empty addresses and the matching ResponseCode.
    /// </summary>
    [Theory]
    [InlineData((int)DnsResponseCode.FormErr)]
    [InlineData((int)DnsResponseCode.ServFail)]
    [InlineData((int)DnsResponseCode.NxDomain)]
    [InlineData((int)DnsResponseCode.Refused)]
    public void TryParseResponse_PropagatesRcode(int codeValue)
    {
        var code = (DnsResponseCode)codeValue;
        var query = DnsWireCodec.BuildQuery(3, "missing.example", DnsRecordType.A);
        var response = BuildRcodeResponse(query, 3, code);
        DnsWireCodec.TryParseResponse(
                response,
                3,
                "missing.example",
                DnsRecordType.A,
                out var parsed,
                out var error)
            .Should().BeTrue(error);
        parsed.ResponseCode.Should().Be(code);
        parsed.Addresses.Should().BeEmpty();
    }

    /// <summary>
    /// TC bit sets Truncated; wrong expected TXID fails with a transaction-ID error.
    /// </summary>
    [Fact]
    public void TryParseResponse_DetectsTruncationAndTxidMismatch()
    {
        var query = DnsWireCodec.BuildQuery(0xABCD, "t.example", DnsRecordType.A);
        var truncated = BuildRcodeResponse(query, 0xABCD, DnsResponseCode.NoError, truncated: true);
        DnsWireCodec.TryParseResponse(
                truncated,
                0xABCD,
                "t.example",
                DnsRecordType.A,
                out var parsed,
                out _)
            .Should().BeTrue();
        parsed.Truncated.Should().BeTrue();

        DnsWireCodec.TryParseResponse(
                truncated,
                0x0001,
                "t.example",
                DnsRecordType.A,
                out _,
                out var txError)
            .Should().BeFalse();
        txError.Should().Contain("transaction ID");
    }

    /// <summary>
    /// Truncated packets and compression-pointer loops are rejected with an error string.
    /// </summary>
    [Fact]
    public void TryParseResponse_RejectsMalformed()
    {
        DnsWireCodec.TryParseResponse([1, 2, 3], 1, "x", DnsRecordType.A, out _, out var shortError)
            .Should().BeFalse();
        shortError.Should().Contain("too short");

        // Compression pointer loop
        var loop = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(loop.AsSpan(0, 2), 1);
        loop[2] = 0x80; // QR
        BinaryPrimitives.WriteUInt16BigEndian(loop.AsSpan(4, 2), 1);
        loop[12] = 0xC0;
        loop[13] = 12; // pointer to itself
        DnsWireCodec.TryParseResponse(loop, 1, "anything", DnsRecordType.A, out _, out var loopError)
            .Should().BeFalse();
        loopError.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Answers whose question name does not match the expected name are rejected.
    /// </summary>
    [Fact]
    public void TryParseResponse_RejectsQuestionMismatch()
    {
        var query = DnsWireCodec.BuildQuery(5, "one.example", DnsRecordType.A);
        var response = BuildAddressResponse(
            query,
            5,
            "one.example",
            DnsRecordType.A,
            IPAddress.Parse("1.2.3.4"),
            compress: true);
        DnsWireCodec.TryParseResponse(response, 5, "two.example", DnsRecordType.A, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("question");
    }
    /// <summary>Synthesizes a single-answer A/AAAA response, optionally compressing the owner name.</summary>
    private static byte[] BuildAddressResponse(
        ReadOnlySpan<byte> query,
        ushort id,
        string name,
        DnsRecordType type,
        IPAddress address,
        bool compress)
    {
        var question = ExtractQuestion(query);
        var nameBytes = compress ? new byte[] { 0xC0, 0x0C } : EncodeName(name);
        var rdata = address.GetAddressBytes();
        var packet = new byte[12 + question.Length + nameBytes.Length + 10 + rdata.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 1);
        question.CopyTo(packet.AsSpan(12));
        var o = 12 + question.Length;
        nameBytes.CopyTo(packet.AsSpan(o));
        o += nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o, 2), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o + 2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(o + 4, 4), 30);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o + 8, 2), (ushort)rdata.Length);
        rdata.CopyTo(packet.AsSpan(o + 10));
        return packet;
    }
    /// <summary>Synthesizes a PTR answer with optional owner-name compression.</summary>
    private static byte[] BuildPtrResponse(
        ReadOnlySpan<byte> query,
        ushort id,
        string questionName,
        string ptrHostname,
        bool compress)
    {
        var question = ExtractQuestion(query);
        var nameBytes = compress ? new byte[] { 0xC0, 0x0C } : EncodeName(questionName);
        var ptr = EncodeName(ptrHostname);
        var packet = new byte[12 + question.Length + nameBytes.Length + 10 + ptr.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), id);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 1);
        question.CopyTo(packet.AsSpan(12));
        var o = 12 + question.Length;
        nameBytes.CopyTo(packet.AsSpan(o));
        o += nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o, 2), (ushort)DnsRecordType.Ptr);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o + 2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(o + 4, 4), 30);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(o + 8, 2), (ushort)ptr.Length);
        ptr.CopyTo(packet.AsSpan(o + 10));
        return packet;
    }
    /// <summary>Synthesizes a question-only response with the given RCODE and optional TC bit.</summary>
    private static byte[] BuildRcodeResponse(
        ReadOnlySpan<byte> query,
        ushort id,
        DnsResponseCode code,
        bool truncated = false)
    {
        var question = ExtractQuestion(query);
        var packet = new byte[12 + question.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), id);
        ushort flags = 0x8180;
        if (truncated)
        {
            flags |= 0x0200;
        }

        flags = (ushort)((flags & 0xFFF0) | ((int)code & 0xF));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1);
        question.CopyTo(packet.AsSpan(12));
        return packet;
    }
    /// <summary>Copies the question section (QNAME+QTYPE+QCLASS) from a query packet.</summary>
    private static byte[] ExtractQuestion(ReadOnlySpan<byte> query)
    {
        var offset = 12;
        while (offset < query.Length && query[offset] != 0)
        {
            offset += 1 + query[offset];
        }

        offset++; // root
        offset += 4; // type + class
        return query.Slice(12, offset - 12).ToArray();
    }
    /// <summary>Encodes a DNS name as uncompressed wire labels (lowercase ASCII).</summary>
    private static byte[] EncodeName(string name)
    {
        var labels = name.Trim().TrimEnd('.').ToLowerInvariant()
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
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
}
