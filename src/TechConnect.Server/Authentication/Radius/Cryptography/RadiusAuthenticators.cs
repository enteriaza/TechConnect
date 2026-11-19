// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusAuthenticators.cs
//
// Purpose:
// Computes and verifies RADIUS Request/Response/Accounting authenticators
// per RFC 2865 / RFC 2866, including Access-Request CSPRNG authenticators.
//
// -----------------------------------------------------------------------------
using System.Buffers;
using System.Security.Cryptography;

namespace TechConnect.Server.Authentication.Radius.Cryptography;

/// <summary>
/// RADIUS request/response authenticator computation and verification (RFC 2865 §3 / §4.1, RFC 2866 §3.1).
/// Response and accounting authenticators follow enteriaza/RadiusClient @ fd27424.
/// Access-Request Request Authenticators follow RFC 2865 (16 cryptographically random octets),
/// correcting the original library's non-standard <c>MD5(random || secret)</c> construction.
/// </summary>
/// <remarks>
/// Distinctions:
/// <list type="bullet">
/// <item><description>Access-Request Request Authenticator: 16 CSPRNG bytes (not hashed with the secret).</description></item>
/// <item><description>PAP User-Password hiding: <c>MD5(SharedSecret || RequestAuthenticator)</c> chaining (reversible obfuscation, not password hashing).</description></item>
/// <item><description>Response Authenticator: MD5 over the response with the original Request Authenticator substituted.</description></item>
/// </list>
/// </remarks>
internal static class RadiusAuthenticators
{
    /// <summary>
    /// Length in octets of the RADIUS Authenticator field.
    /// </summary>
    public const int AuthenticatorLength = 16;

    /// <summary>
    /// Minimum valid RADIUS packet length (header only).
    /// </summary>
    public const int MinimumPacketLength = 20;

    /// <summary>
    /// Byte offset of the Authenticator field within a RADIUS packet.
    /// </summary>
    public const int AuthenticatorOffset = 4;

    /// <summary>
    /// Random source for Access-Request Request Authenticators. Defaults to
    /// <see cref="SystemRadiusSecureRandom"/>. Tests may replace this temporarily.
    /// </summary>
    public static IRadiusSecureRandom SecureRandom { get; set; } = SystemRadiusSecureRandom.Instance;

    /// <summary>
    /// Creates an Access-Request Request Authenticator per RFC 2865 §3 / §4.1:
    /// exactly 16 cryptographically unpredictable random octets.
    /// The shared secret is not an input.
    /// </summary>
    public static byte[] CreateAccessRequestAuthenticator()
    {
        var authenticator = new byte[AuthenticatorLength];
        SecureRandom.Fill(authenticator);
        return authenticator;
    }

    /// <summary>
    /// Response Authenticator:
    /// <c>MD5(Code || Identifier || Length || RequestAuthenticator || Attributes || SharedSecret)</c>.
    /// </summary>
    public static byte[] ComputeResponseAuthenticator(
        ReadOnlySpan<byte> responsePacket,
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret)
    {
        ValidatePacketAndSecret(responsePacket, sharedSecret);
        if (requestAuthenticator.Length != AuthenticatorLength)
        {
            throw new ArgumentException(
                $"Request Authenticator must be exactly {AuthenticatorLength} bytes.",
                nameof(requestAuthenticator));
        }

        var working = ArrayPool<byte>.Shared.Rent(responsePacket.Length);
        try
        {
            responsePacket.CopyTo(working);
            requestAuthenticator.CopyTo(working.AsSpan(AuthenticatorOffset, AuthenticatorLength));
            return HashPacketWithSecret(working.AsSpan(0, responsePacket.Length), sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(working.AsSpan(0, responsePacket.Length));
            ArrayPool<byte>.Shared.Return(working);
        }
    }

    /// <summary>
    /// Accounting-Request authenticator:
    /// <c>MD5(Code || Identifier || Length || 16-zero-bytes || Attributes || SharedSecret)</c>.
    /// </summary>
    public static byte[] ComputeAccountingRequestAuthenticator(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> sharedSecret)
    {
        ValidatePacketAndSecret(packet, sharedSecret);

        var working = ArrayPool<byte>.Shared.Rent(packet.Length);
        try
        {
            packet.CopyTo(working);
            working.AsSpan(AuthenticatorOffset, AuthenticatorLength).Clear();
            return HashPacketWithSecret(working.AsSpan(0, packet.Length), sharedSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(working.AsSpan(0, packet.Length));
            ArrayPool<byte>.Shared.Return(working);
        }
    }

    /// <summary>
    /// Verifies a Response Authenticator using fixed-time comparison.
    /// </summary>
    /// <param name="responsePacket">Complete response packet including claimed authenticator.</param>
    /// <param name="requestAuthenticator">Original Access-Request authenticator.</param>
    /// <param name="sharedSecret">Shared secret; must be non-empty.</param>
    /// <returns><see langword="true"/> when the authenticator matches the recomputed value.</returns>
    /// <exception cref="ArgumentException">Packet too short, secret empty, or request authenticator wrong length.</exception>
    public static bool VerifyResponseAuthenticator(
        ReadOnlySpan<byte> responsePacket,
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret)
    {
        ValidatePacketAndSecret(responsePacket, sharedSecret);
        if (requestAuthenticator.Length != AuthenticatorLength)
        {
            throw new ArgumentException(
                $"Request Authenticator must be exactly {AuthenticatorLength} bytes.",
                nameof(requestAuthenticator));
        }

        var expected = ComputeResponseAuthenticator(responsePacket, requestAuthenticator, sharedSecret);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                responsePacket.Slice(AuthenticatorOffset, AuthenticatorLength),
                expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    /// <summary>
    /// Verifies an Accounting-Request authenticator using fixed-time comparison.
    /// </summary>
    /// <param name="packet">Accounting-Request packet including claimed authenticator.</param>
    /// <param name="sharedSecret">Shared secret; must be non-empty.</param>
    /// <returns><see langword="true"/> when the authenticator matches the recomputed value.</returns>
    /// <exception cref="ArgumentException">Packet too short or secret empty.</exception>
    public static bool VerifyAccountingRequestAuthenticator(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> sharedSecret)
    {
        ValidatePacketAndSecret(packet, sharedSecret);
        var expected = ComputeAccountingRequestAuthenticator(packet, sharedSecret);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                packet.Slice(AuthenticatorOffset, AuthenticatorLength),
                expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    /// <summary>
    /// Ensures the packet meets the minimum length and the shared secret is non-empty.
    /// </summary>
    /// <param name="packet">Candidate RADIUS packet bytes.</param>
    /// <param name="sharedSecret">Shared secret bytes.</param>
    /// <exception cref="ArgumentException">Validation failed.</exception>
    private static void ValidatePacketAndSecret(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sharedSecret)
    {
        if (packet.Length < MinimumPacketLength)
        {
            throw new ArgumentException(
                $"RADIUS packet must be at least {MinimumPacketLength} bytes.",
                nameof(packet));
        }

        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }
    }

    /// <summary>
    /// Computes MD5 over <paramref name="packetData"/> concatenated with <paramref name="sharedSecret"/>.
    /// </summary>
    /// <param name="packetData">Packet bytes (authenticator field already substituted as required).</param>
    /// <param name="sharedSecret">Shared secret appended after the packet.</param>
    /// <returns>16-byte MD5 digest; temporary buffers are zeroed before return.</returns>
    private static byte[] HashPacketWithSecret(ReadOnlySpan<byte> packetData, ReadOnlySpan<byte> sharedSecret)
    {
        var totalLength = packetData.Length + sharedSecret.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(totalLength);
        try
        {
            var span = buffer.AsSpan(0, totalLength);
            packetData.CopyTo(span);
            sharedSecret.CopyTo(span[packetData.Length..]);
            return MD5.HashData(span);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, totalLength));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
