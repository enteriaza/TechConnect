// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusMessageAuthenticator.cs
//
// Purpose:
// Computes and verifies HMAC-MD5 Message-Authenticator (Type 80) attributes
// per RFC 3579 for RADIUS packets.
//
// -----------------------------------------------------------------------------
using System.Security.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Authentication.Radius.Cryptography;

/// <summary>
/// HMAC-MD5 Message-Authenticator (Type 80) per RFC 3579 §3.2.
/// Derived from enteriaza/RadiusClient <c>RadiusUtils</c> @ fd27424.
/// </summary>
internal static class RadiusMessageAuthenticator
{
    /// <summary>
    /// Wire length of a Message-Authenticator attribute (Type + Length + 16-byte value).
    /// </summary>
    public const int AttributeWireLength = 18; // Type + Length + 16-byte value

    /// <summary>
    /// Length of the HMAC-MD5 value field in octets.
    /// </summary>
    public const int ValueLength = 16;

    /// <summary>
    /// Computes HMAC-MD5 over the full packet with the Message-Authenticator value field zeroed.
    /// </summary>
    public static byte[] Compute(ReadOnlySpan<byte> packetWithZeroedMessageAuthenticator, ReadOnlySpan<byte> sharedSecret)
    {
        if (packetWithZeroedMessageAuthenticator.Length < RadiusAuthenticators.MinimumPacketLength)
        {
            throw new ArgumentException(
                $"RADIUS packet must be at least {RadiusAuthenticators.MinimumPacketLength} bytes.",
                nameof(packetWithZeroedMessageAuthenticator));
        }

        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }

        var key = sharedSecret.ToArray();
        try
        {
            using var hmac = new HMACMD5(key);
            return hmac.ComputeHash(packetWithZeroedMessageAuthenticator.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Verifies a Message-Authenticator attribute already present in <paramref name="packet"/>.
    /// Returns false when the attribute is missing, malformed, or the HMAC does not match.
    /// </summary>
    public static bool TryVerify(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sharedSecret)
    {
        if (!TryFindMessageAuthenticatorOffset(packet, out var valueOffset))
        {
            return false;
        }

        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }

        var working = packet.ToArray();
        try
        {
            working.AsSpan(valueOffset, ValueLength).Clear();
            var expected = Compute(working, sharedSecret);
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                    packet.Slice(valueOffset, ValueLength),
                    expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(working);
        }
    }

    /// <summary>
    /// Locates the value offset of the first Message-Authenticator attribute in <paramref name="packet"/>.
    /// </summary>
    /// <param name="packet">Complete RADIUS packet bytes.</param>
    /// <param name="valueOffset">Offset of the 16-byte value when found.</param>
    /// <returns><see langword="true"/> when a well-formed Type 80 attribute is present.</returns>
    private static bool TryFindMessageAuthenticatorOffset(ReadOnlySpan<byte> packet, out int valueOffset)
    {
        valueOffset = 0;
        if (packet.Length < RadiusAuthenticators.MinimumPacketLength)
        {
            return false;
        }

        var offset = RadiusAuthenticators.MinimumPacketLength;
        while (offset + 2 <= packet.Length)
        {
            var type = packet[offset];
            var length = packet[offset + 1];
            if (length < 2 || offset + length > packet.Length)
            {
                return false;
            }

            if (type == (byte)RadiusAttributeType.MessageAuthenticator)
            {
                if (length != AttributeWireLength)
                {
                    return false;
                }

                valueOffset = offset + 2;
                return true;
            }

            offset += length;
        }

        return false;
    }
}
