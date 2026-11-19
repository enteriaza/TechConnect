// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DnsWireCodec.cs
//
// Purpose:
// Encodes and decodes DNS wire messages with defensive bounds and
// compression-pointer handling for the TechConnect UDP/TCP DNS client.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TechConnect.Server.NameResolution;

/// <summary>
/// Encodes and decodes DNS messages with defensive bounds and compression-pointer handling.
/// </summary>
/// <remarks>
/// Used exclusively by <see cref="ConfiguredDnsResolver"/>; this codec does not contact
/// the OS resolver. Compression pointer loops are rejected after
/// <see cref="MaxCompressionDepth"/> jumps.
/// </remarks>
internal static class DnsWireCodec
{
    /// <summary>Maximum UDP DNS payload accepted on receive (classic 512-octet limit).</summary>
    internal const int MaxUdpPayload = 512;

    /// <summary>Maximum TCP DNS message length accepted after the 2-byte length prefix.</summary>
    internal const int MaxTcpMessage = 65535;

    /// <summary>Maximum DNS name length in octets (excluding root label edge cases).</summary>
    internal const int MaxNameLength = 255;

    /// <summary>Maximum DNS label length in octets.</summary>
    internal const int MaxLabelLength = 63;

    /// <summary>Maximum compression-pointer chase depth before rejecting as a loop.</summary>
    internal const int MaxCompressionDepth = 32;

    /// <summary>DNS CLASS IN (Internet).</summary>
    private const ushort ClassIn = 1;

    /// <summary>
    /// Builds a standard recursive query (RD=1) for <paramref name="name"/> and <paramref name="type"/>.
    /// </summary>
    /// <param name="transactionId">16-bit transaction identifier.</param>
    /// <param name="name">DNS name to encode.</param>
    /// <param name="type">Query type.</param>
    /// <returns>Complete DNS message bytes.</returns>
    /// <exception cref="ArgumentException">Name or label length is invalid.</exception>
    public static byte[] BuildQuery(ushort transactionId, string name, DnsRecordType type)
    {
        var nameBytes = EncodeName(name);
        var packet = new byte[12 + nameBytes.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), transactionId);
        // RD = 1
        packet[2] = 0x01;
        packet[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 1); // QDCOUNT
        nameBytes.CopyTo(packet.AsSpan(12));
        var qOffset = 12 + nameBytes.Length;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(qOffset, 2), (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(qOffset + 2, 2), ClassIn);
        return packet;
    }

    /// <summary>
    /// Parses a DNS response, validating transaction id and question match, collecting A/AAAA/PTR answers.
    /// </summary>
    /// <param name="message">Raw DNS message.</param>
    /// <param name="expectedId">Expected transaction id.</param>
    /// <param name="expectedName">Expected question name (normalized comparison).</param>
    /// <param name="expectedType">Expected question type.</param>
    /// <param name="response">Parsed response on success.</param>
    /// <param name="error">Parse error on failure.</param>
    /// <returns><see langword="true"/> when the message was parsed (including NXDOMAIN RCODEs).</returns>
    public static bool TryParseResponse(
        ReadOnlySpan<byte> message,
        ushort expectedId,
        string expectedName,
        DnsRecordType expectedType,
        out ParsedDnsResponse response,
        out string? error)
    {
        response = default;
        error = null;

        if (message.Length < 12)
        {
            error = "DNS response too short.";
            return false;
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message[..2]);
        if (id != expectedId)
        {
            error = "DNS response transaction ID mismatch.";
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(2, 2));
        var qr = (flags & 0x8000) != 0;
        if (!qr)
        {
            error = "DNS message is not a response.";
            return false;
        }

        var truncated = (flags & 0x0200) != 0;
        var rcode = (DnsResponseCode)(flags & 0x000F);
        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(4, 2));
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(6, 2));
        var nsCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(8, 2));
        var arCount = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(10, 2));

        var offset = 12;
        string? questionName = null;
        DnsRecordType questionType = 0;

        for (var q = 0; q < qdCount; q++)
        {
            if (!TryReadName(message, ref offset, out var qName, out error))
            {
                return false;
            }

            if (offset + 4 > message.Length)
            {
                error = "DNS question section truncated.";
                return false;
            }

            questionType = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 4; // type + class
            questionName ??= qName;
        }

        if (qdCount > 0)
        {
            if (!NamesEqual(questionName!, expectedName) || questionType != expectedType)
            {
                error = "DNS response question does not match query.";
                return false;
            }
        }

        var addresses = new List<IPAddress>();
        string? ptr = null;

        if (!TrySkipOrReadRecords(
                message,
                ref offset,
                anCount,
                expectedType,
                addresses,
                ref ptr,
                collectAnswers: true,
                out error))
        {
            return false;
        }

        // Authority + additional: skip without collecting.
        string? ignored = null;
        if (!TrySkipOrReadRecords(
                message,
                ref offset,
                nsCount,
                expectedType,
                addresses,
                ref ignored,
                collectAnswers: false,
                out error))
        {
            return false;
        }

        if (!TrySkipOrReadRecords(
                message,
                ref offset,
                arCount,
                expectedType,
                addresses,
                ref ignored,
                collectAnswers: false,
                out error))
        {
            return false;
        }

        response = new ParsedDnsResponse
        {
            ResponseCode = rcode,
            Truncated = truncated,
            Addresses = addresses,
            PtrHostname = ptr
        };
        return true;
    }

    /// <summary>
    /// Builds the reverse-lookup name for <paramref name="address"/> (<c>in-addr.arpa</c> / <c>ip6.arpa</c>).
    /// </summary>
    /// <param name="address">IPv4 or IPv6 address.</param>
    /// <returns>PTR query name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Unsupported address family.</exception>
    public static string BuildPtrName(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return $"{bytes[3]}.{bytes[2]}.{bytes[1]}.{bytes[0]}.in-addr.arpa";
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            var sb = new StringBuilder(64);
            for (var i = bytes.Length - 1; i >= 0; i--)
            {
                var b = bytes[i];
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:x}.", b & 0x0F);
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "{0:x}.", b >> 4);
            }

            sb.Append("ip6.arpa");
            return sb.ToString();
        }

        throw new ArgumentOutOfRangeException(nameof(address), "Unsupported address family for PTR.");
    }

    /// <summary>
    /// Walks <paramref name="count"/> resource records, optionally collecting A/AAAA/PTR answers.
    /// </summary>
    /// <param name="message">Full DNS message.</param>
    /// <param name="offset">Current byte offset; advanced past the section.</param>
    /// <param name="count">Number of records in the section.</param>
    /// <param name="expectedType">Expected query type (gates PTR collection).</param>
    /// <param name="addresses">Accumulator for A/AAAA when collecting.</param>
    /// <param name="ptr">First PTR name when collecting PTR answers.</param>
    /// <param name="collectAnswers">When false, records are skipped without collecting.</param>
    /// <param name="error">Parse error on failure.</param>
    /// <returns><see langword="true"/> when the section was consumed successfully.</returns>
    private static bool TrySkipOrReadRecords(
        ReadOnlySpan<byte> message,
        ref int offset,
        int count,
        DnsRecordType expectedType,
        List<IPAddress> addresses,
        ref string? ptr,
        bool collectAnswers,
        out string? error)
    {
        error = null;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(message, ref offset, out _, out error))
            {
                return false;
            }

            if (offset + 10 > message.Length)
            {
                error = "DNS resource record header truncated.";
                return false;
            }

            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;
            offset += 2; // class
            offset += 4; // TTL
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(message.Slice(offset, 2));
            offset += 2;

            if (rdLength < 0 || offset + rdLength > message.Length)
            {
                error = "DNS resource record data truncated.";
                return false;
            }

            if (collectAnswers)
            {
                if (type == DnsRecordType.A && rdLength == 4)
                {
                    addresses.Add(new IPAddress(message.Slice(offset, 4)));
                }
                else if (type == DnsRecordType.Aaaa && rdLength == 16)
                {
                    addresses.Add(new IPAddress(message.Slice(offset, 16)));
                }
                else if (type == DnsRecordType.Ptr && expectedType == DnsRecordType.Ptr)
                {
                    var nameOffset = offset;
                    if (TryReadName(message, ref nameOffset, out var name, out _) &&
                        nameOffset <= offset + rdLength)
                    {
                        ptr ??= name;
                    }
                }
            }

            offset += rdLength;
        }

        return true;
    }

    /// <summary>Encodes a DNS name into length-prefixed labels terminated by a root zero.</summary>
    /// <param name="name">DNS name (will be normalized).</param>
    /// <returns>Wire-format name octets.</returns>
    /// <exception cref="ArgumentException">Name or label length is invalid.</exception>
    internal static byte[] EncodeName(string name)
    {
        var normalized = NormalizeName(name);
        if (normalized.Length > MaxNameLength)
        {
            throw new ArgumentException("DNS name exceeds maximum length.", nameof(name));
        }

        if (normalized.Length == 0)
        {
            return [0];
        }

        var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var size = 1;
        foreach (var label in labels)
        {
            if (label.Length is 0 or > MaxLabelLength)
            {
                throw new ArgumentException("Invalid DNS label.", nameof(name));
            }

            size += 1 + label.Length;
        }

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

    /// <summary>
    /// Reads a DNS name at <paramref name="offset"/>, following compression pointers safely.
    /// </summary>
    /// <param name="message">Full message (required for pointer targets).</param>
    /// <param name="offset">Current offset; advanced past the name (or pointer) on success.</param>
    /// <param name="name">Decoded lowercase name (non-printable octets hex-escaped).</param>
    /// <param name="error">Parse error on failure.</param>
    /// <returns><see langword="true"/> when a name was decoded.</returns>
    internal static bool TryReadName(
        ReadOnlySpan<byte> message,
        ref int offset,
        out string name,
        out string? error)
    {
        name = string.Empty;
        error = null;
        var sb = new StringBuilder();
        var jumped = false;
        var originalOffset = offset;
        var depth = 0;
        var endOffset = offset;

        while (true)
        {
            if (offset >= message.Length)
            {
                error = "DNS name truncated.";
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
                    error = "DNS compression pointer truncated.";
                    return false;
                }

                var pointer = ((len & 0x3F) << 8) | message[offset + 1];
                if (pointer >= message.Length)
                {
                    error = "DNS compression pointer out of range.";
                    return false;
                }

                if (++depth > MaxCompressionDepth)
                {
                    error = "DNS compression pointer loop.";
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

            if ((len & 0xC0) != 0)
            {
                error = "Invalid DNS label length.";
                return false;
            }

            if (len > MaxLabelLength || offset + 1 + len > message.Length)
            {
                error = "DNS label truncated or too long.";
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            for (var i = 0; i < len; i++)
            {
                var c = (char)message[offset + 1 + i];
                if (c is < (char)33 or > (char)126)
                {
                    // Allow common DNS octets as lowercase ASCII when printable; otherwise hex-escape.
                    sb.Append('\\');
                    sb.Append(((byte)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            if (sb.Length > MaxNameLength)
            {
                error = "Decoded DNS name exceeds maximum length.";
                return false;
            }

            offset += 1 + len;
            if (!jumped)
            {
                endOffset = offset;
            }
        }

        offset = jumped ? endOffset : offset;
        // Silence unused when no jump path set endOffset from original — ensure progress.
        if (!jumped && endOffset == originalOffset && message.Length > originalOffset)
        {
            // root label already advanced offset
        }

        name = sb.ToString();
        return true;
    }

    /// <summary>
    /// Normalizes a DNS name: trim, strip trailing dot, lowercase invariant.
    /// </summary>
    /// <param name="name">Raw name.</param>
    /// <returns>Normalized name.</returns>
    internal static string NormalizeName(string name)
    {
        var trimmed = name.Trim().TrimEnd('.').ToLowerInvariant();
        return trimmed;
    }

    /// <summary>Compares two DNS names after <see cref="NormalizeName"/>.</summary>
    /// <param name="left">First name.</param>
    /// <param name="right">Second name.</param>
    /// <returns><see langword="true"/> when normalized forms are ordinal-equal.</returns>
    private static bool NamesEqual(string left, string right) =>
        string.Equals(NormalizeName(left), NormalizeName(right), StringComparison.Ordinal);
}

/// <summary>
/// Parsed DNS response fields collected by <see cref="DnsWireCodec.TryParseResponse"/>.
/// </summary>
internal readonly struct ParsedDnsResponse
{
    /// <summary>Wire RCODE from the response flags.</summary>
    public DnsResponseCode ResponseCode { get; init; }

    /// <summary>Whether the TC (truncated) bit was set.</summary>
    public bool Truncated { get; init; }

    /// <summary>A/AAAA addresses from the answer section.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; }

    /// <summary>First PTR hostname from the answer section, when present.</summary>
    public string? PtrHostname { get; init; }
}
