// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusPapPassword.cs
//
// Purpose:
// Encodes User-Password attributes with RADIUS PAP reversible obfuscation
// (RFC 2865 §5.2) for Access-Request packets.
//
// -----------------------------------------------------------------------------
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TechConnect.Server.Authentication.Radius.Cryptography;

/// <summary>
/// RADIUS PAP User-Password hiding (RFC 2865 §5.2).
/// Behaviour preserved from enteriaza/RadiusClient <c>RadiusUtils.EncodePapPassword</c> @ fd27424.
/// </summary>
/// <remarks>
/// First block: <c>c(1) = p(1) XOR MD5(SharedSecret || RequestAuthenticator)</c>.
/// Subsequent blocks chain on the previous ciphertext block.
/// This is reversible protocol obfuscation for parties that know the shared secret and
/// Request Authenticator — it is not password hashing.
/// The Request Authenticator must be the exact 16 random octets from the Access-Request.
/// </remarks>
internal static class RadiusPapPassword
{
    /// <summary>
    /// Maximum plaintext password length accepted for PAP encoding (RFC 2865).
    /// </summary>
    public const int MaxPasswordLength = 128;

    /// <summary>
    /// MD5 block size used by PAP chaining (same as authenticator length).
    /// </summary>
    private const int BlockSize = RadiusAuthenticators.AuthenticatorLength;

    /// <summary>
    /// Encodes a plaintext password into PAP User-Password ciphertext.
    /// </summary>
    /// <param name="userPassword">UTF-8 password octets; must be non-empty and ≤ <see cref="MaxPasswordLength"/>.</param>
    /// <param name="requestAuthenticator">16-byte Access-Request authenticator used as the first chain input.</param>
    /// <param name="sharedSecret">RADIUS shared secret; must be non-empty.</param>
    /// <returns>
    /// Ciphertext padded to a multiple of 16 bytes. On failure paths the buffer is zeroed;
    /// on success the caller owns the returned array (zeroed by Access-Request factory after use).
    /// </returns>
    /// <exception cref="ArgumentException">Password, authenticator, or secret fails length checks.</exception>
    public static byte[] Encode(
        ReadOnlySpan<byte> userPassword,
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret)
    {
        if (userPassword.IsEmpty)
        {
            throw new ArgumentException("Password must not be empty.", nameof(userPassword));
        }

        if (userPassword.Length > MaxPasswordLength)
        {
            throw new ArgumentException(
                $"PAP password cannot be greater than {MaxPasswordLength} bytes.",
                nameof(userPassword));
        }

        if (requestAuthenticator.Length != BlockSize)
        {
            throw new ArgumentException(
                $"Request Authenticator must be exactly {BlockSize} bytes.",
                nameof(requestAuthenticator));
        }

        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }

        var paddedLength = (userPassword.Length + 15) / 16 * 16;
        var encrypted = new byte[paddedLength];
        userPassword.CopyTo(encrypted);

        var hashInputLength = sharedSecret.Length + BlockSize;
        var hashInput = ArrayPool<byte>.Shared.Rent(hashInputLength);
        var success = false;
        try
        {
            var hashInputSpan = hashInput.AsSpan(0, hashInputLength);
            var secretSlot = hashInputSpan[..sharedSecret.Length];
            var chainSlot = hashInputSpan[sharedSecret.Length..];
            sharedSecret.CopyTo(secretSlot);
            requestAuthenticator.CopyTo(chainSlot);

            var chunkCount = paddedLength / BlockSize;
            Span<byte> hash = stackalloc byte[BlockSize];

            for (var chunk = 0; chunk < chunkCount; chunk++)
            {
                MD5.TryHashData(hashInputSpan, hash, out _);

                var offset = chunk * BlockSize;
                var block = encrypted.AsSpan(offset, BlockSize);

                ref var blockRef = ref MemoryMarshal.GetReference(block);
                ref var hashRef = ref MemoryMarshal.GetReference(hash);
                for (var i = 0; i < BlockSize; i++)
                {
                    Unsafe.Add(ref blockRef, i) ^= Unsafe.Add(ref hashRef, i);
                }

                block.CopyTo(chainSlot);
            }

            success = true;
            return encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hashInput.AsSpan(0, hashInputLength));
            ArrayPool<byte>.Shared.Return(hashInput);
            if (!success)
            {
                CryptographicOperations.ZeroMemory(encrypted);
            }
        }
    }
}
