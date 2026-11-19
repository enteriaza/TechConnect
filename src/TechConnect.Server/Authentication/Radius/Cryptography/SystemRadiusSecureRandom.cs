// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SystemRadiusSecureRandom.cs
//
// Purpose:
// Default IRadiusSecureRandom implementation backed by
// System.Security.Cryptography.RandomNumberGenerator.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;

namespace TechConnect.Server.Authentication.Radius.Cryptography;

/// <summary>
/// Production <see cref="IRadiusSecureRandom"/> that fills buffers via
/// <see cref="RandomNumberGenerator.Fill(Span{byte})"/>.
/// </summary>
/// <remarks>
/// Singleton <see cref="Instance"/> is thread-safe. Tests may substitute a different
/// <see cref="IRadiusSecureRandom"/> via <see cref="RadiusAuthenticators.SecureRandom"/>.
/// </remarks>
internal sealed class SystemRadiusSecureRandom : IRadiusSecureRandom
{
    /// <summary>
    /// Shared singleton instance used as the default random source.
    /// </summary>
    public static SystemRadiusSecureRandom Instance { get; } = new();

    /// <summary>
    /// Prevents external construction; use <see cref="Instance"/>.
    /// </summary>
    private SystemRadiusSecureRandom()
    {
    }

    /// <summary>
    /// Fills <paramref name="destination"/> using the system CSPRNG.
    /// </summary>
    /// <param name="destination">Buffer to fill.</param>
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
