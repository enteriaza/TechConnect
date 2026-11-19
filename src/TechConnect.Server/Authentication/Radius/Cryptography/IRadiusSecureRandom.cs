// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IRadiusSecureRandom.cs
//
// Purpose:
// Narrow abstraction for cryptographically secure random bytes used when
// generating Access-Request Request Authenticators.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Cryptography;

/// <summary>
/// Narrow provider for cryptographically secure random bytes used by Access-Request
/// Request Authenticator generation (RFC 2865 §3 / §4.1).
/// </summary>
internal interface IRadiusSecureRandom
{
    /// <summary>
    /// Fills <paramref name="destination"/> with cryptographically secure random octets.
    /// </summary>
    /// <param name="destination">Buffer to fill; length may be any non-negative size.</param>
    void Fill(Span<byte> destination);
}
