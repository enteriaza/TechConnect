// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusAttribute.cs
//
// Purpose:
// RADIUS attribute Type-Length-Value builder and parser used when
// constructing and reading authentication packets.
//
// -----------------------------------------------------------------------------
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TechConnect.Server.Authentication.Radius.Protocol;

/// <summary>
/// A single RADIUS attribute in Type-Length-Value form (RFC 2865 §5).
/// Derived from enteriaza/RadiusClient <c>RadiusAttributes</c> @ fd27424; trimmed for TechConnect.
/// </summary>
internal sealed class RadiusAttribute
{
    /// <summary>
    /// Header Size backing member.
    /// </summary>
    private const byte HeaderSize = 2;
    /// <summary>
    /// Max Value Length backing member.
    /// </summary>
    private const int MaxValueLength = 255 - HeaderSize;

    /// <summary>
    /// wire backing member.
    /// </summary>
    private readonly byte[] _wire;

    /// <summary>
    /// Radius Attribute.
    /// </summary>
    private RadiusAttribute(byte[] wire)
    {
        _wire = wire;
        Type = (RadiusAttributeType)wire[0];
        Length = wire[1];
    }

    /// <summary>
    /// Type backing member.
    /// </summary>
    public RadiusAttributeType Type { get; }

    /// <summary>Total TLV length including the 2-byte header.</summary>
    public byte Length { get; }

    /// <summary>Value octets (excluding Type/Length).</summary>
    public ReadOnlySpan<byte> Value => _wire.AsSpan(HeaderSize, Length - HeaderSize);

    /// <summary>
    /// Returns Wire Bytes.
    /// </summary>
    public byte[] GetWireBytes() => (byte[])_wire.Clone();

    /// <summary>
    /// Returns String Value.
    /// </summary>
    public string GetStringValue() => Encoding.UTF8.GetString(Value);

    /// <summary>
    /// Creates .
    /// </summary>
    public static RadiusAttribute Create(RadiusAttributeType type, ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxValueLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value.Length,
                $"Attribute value must not exceed {MaxValueLength} bytes.");
        }

        var length = (byte)(value.Length + HeaderSize);
        var wire = new byte[length];
        wire[0] = (byte)type;
        wire[1] = length;
        value.CopyTo(wire.AsSpan(HeaderSize));
        return new RadiusAttribute(wire);
    }

    /// <summary>
    /// Creates .
    /// </summary>
    public static RadiusAttribute Create(RadiusAttributeType type, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaxValueLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                byteCount,
                $"UTF-8 encoded attribute value must not exceed {MaxValueLength} bytes.");
        }

        var length = (byte)(byteCount + HeaderSize);
        var wire = new byte[length];
        wire[0] = (byte)type;
        wire[1] = length;
        Encoding.UTF8.GetBytes(value, wire.AsSpan(HeaderSize));
        return new RadiusAttribute(wire);
    }

    /// <summary>
    /// Creates .
    /// </summary>
    public static RadiusAttribute Create(RadiusAttributeType type, int value)
    {
        var length = (byte)(sizeof(int) + HeaderSize);
        var wire = new byte[length];
        wire[0] = (byte)type;
        wire[1] = length;
        BinaryPrimitives.WriteInt32BigEndian(wire.AsSpan(HeaderSize), value);
        return new RadiusAttribute(wire);
    }

    /// <summary>
    /// Creates .
    /// </summary>
    public static RadiusAttribute Create(RadiusAttributeType type, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            throw new NotSupportedException("Only IPv4 and IPv6 addresses are supported.");
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out var written))
        {
            throw new InvalidOperationException("Failed to encode IP address.");
        }

        return Create(type, bytes[..written]);
    }

    /// <summary>
    /// Parses one attribute TLV from <paramref name="buffer"/>. Returns false when the TLV is truncated
    /// or declares an invalid length.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> buffer, out RadiusAttribute? attribute, out int consumed)
    {
        attribute = null;
        consumed = 0;

        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        var length = buffer[1];
        if (length < HeaderSize || length > buffer.Length)
        {
            return false;
        }

        var wire = buffer[..length].ToArray();
        attribute = new RadiusAttribute(wire);
        consumed = length;
        return true;
    }
}
