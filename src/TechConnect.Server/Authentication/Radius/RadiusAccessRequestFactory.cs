// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusAccessRequestFactory.cs
//
// Purpose:
// Builds RADIUS Access-Request packets using PAP password encoding and Request
// Authenticators for operator and enable authentication.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Security.Cryptography;
using System.Text;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// Builds Access-Request packets using Phase 1 PAP encoding and Request Authenticators.
/// </summary>
/// <remarks>
/// Does not send Message-Authenticator unless a future policy enables it.
/// Temporary password and PAP ciphertext buffers are zeroed before return.
/// Thread-safe: all state is local to each <see cref="Create"/> call.
/// </remarks>
internal static class RadiusAccessRequestFactory
{
    /// <summary>
    /// Service-Type Login (1) — administrative NAS login.
    /// </summary>
    private const int ServiceTypeLogin = 1;

    /// <summary>
    /// Creates a serialized Access-Request and its Request Authenticator.
    /// </summary>
    /// <param name="username">RADIUS User-Name; must be non-whitespace.</param>
    /// <param name="password">Plaintext password encoded as User-Password (PAP); must be non-empty.</param>
    /// <param name="options">NAS identifier and optional NAS-IP-Address settings.</param>
    /// <param name="sharedSecret">Shared secret used for PAP encoding; must be non-empty.</param>
    /// <returns>
    /// Wire bytes for the Access-Request and the 16-byte Request Authenticator used to build it.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// Username/password/shared secret fail empty checks.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static (byte[] Packet, byte[] RequestAuthenticator) Create(
        string username,
        string password,
        RadiusAuthenticationOptions options,
        ReadOnlySpan<byte> sharedSecret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(options);
        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }

        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var requestAuthenticator = RadiusAuthenticators.CreateAccessRequestAuthenticator();
        byte[]? pap = null;
        try
        {
            pap = RadiusPapPassword.Encode(passwordBytes, requestAuthenticator, sharedSecret);

            var packet = new RadiusPacket(RadiusPacketCode.AccessRequest);
            packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, username));
            packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserPassword, pap));
            packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.NasIdentifier, options.NasIdentifier));
            packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ServiceType, ServiceTypeLogin));

            if (!string.IsNullOrWhiteSpace(options.NasIpAddress) &&
                IPAddress.TryParse(options.NasIpAddress, out var nasIp))
            {
                packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.NasIpAddress, nasIp));
            }

            packet.SetAccessRequestAuthenticator(requestAuthenticator);
            return (packet.RawData.ToArray(), requestAuthenticator);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (pap is not null)
            {
                CryptographicOperations.ZeroMemory(pap);
            }
        }
    }
}
