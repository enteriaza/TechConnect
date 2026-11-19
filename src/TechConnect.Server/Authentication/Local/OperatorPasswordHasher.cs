// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorPasswordHasher.cs
//
// Purpose:
// Hashes and verifies local operator passwords and enable secrets using ASP.NET
// Identity V3 PBKDF2-HMAC-SHA512 (not ASP.NET hosting).
//
// -----------------------------------------------------------------------------

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication.Local;

/// <summary>
/// ASP.NET Identity V3 password hashing (PBKDF2-HMAC-SHA512, random salt, versioned format)
/// via <see cref="PasswordHasher{TUser}"/>. Not ASP.NET hosting.
/// </summary>
/// <remarks>
/// Verification treats <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> as success
/// but does not persist a rehash. Malformed stored hashes cause
/// <see cref="VerifyPassword"/> to return <see langword="false"/> rather than throw.
/// Plaintext secrets are not retained after hashing/verification returns.
/// </remarks>
internal sealed class OperatorPasswordHasher
{
    /// <summary>
    /// Hash subject used for <see cref="LocalAuthenticationOptions.EnableSecretHash"/>.
    /// Generate with <see cref="HashEnableSecret"/> or <c>HashPassword("enable", secret)</c>.
    /// </summary>
    public const string EnableSecretHashSubject = "enable";

    /// <summary>
    /// Identity V3 hasher configured with default IdentityV3 iteration count (100_000).
    /// </summary>
    private readonly PasswordHasher<PasswordHashSubject> _hasher = new(
        Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3
            // IterationCount defaults to 100_000 for IdentityV3.
        }));

    /// <summary>
    /// Creates a new Identity V3 hash for the given username and password.
    /// </summary>
    /// <param name="username">Hash subject; must be non-whitespace.</param>
    /// <param name="password">Plaintext password; must be non-empty.</param>
    /// <returns>Base64-encoded Identity V3 password hash.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="username"/> is null/whitespace or <paramref name="password"/> is null/empty.
    /// </exception>
    public string HashPassword(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return _hasher.HashPassword(new PasswordHashSubject(username), password);
    }

    /// <summary>
    /// Hashes an enable secret using <see cref="EnableSecretHashSubject"/> as the hash subject.
    /// </summary>
    /// <param name="secret">Plaintext enable secret.</param>
    /// <returns>Identity V3 hash suitable for enable-secret configuration.</returns>
    public string HashEnableSecret(string secret) =>
        HashPassword(EnableSecretHashSubject, secret);

    /// <summary>
    /// Verifies a plaintext password against a stored Identity V3 hash.
    /// </summary>
    /// <param name="username">Hash subject that must match the subject used when hashing.</param>
    /// <param name="passwordHash">Stored hash; whitespace or empty fails closed.</param>
    /// <param name="password">Candidate plaintext password.</param>
    /// <returns>
    /// <see langword="true"/> on <see cref="PasswordVerificationResult.Success"/> or
    /// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>; otherwise
    /// <see langword="false"/> (including malformed hash payloads).
    /// </returns>
    public bool VerifyPassword(string username, string passwordHash, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (string.IsNullOrWhiteSpace(passwordHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            var result = _hasher.VerifyHashedPassword(
                new PasswordHashSubject(username),
                passwordHash,
                password);
            return result is PasswordVerificationResult.Success
                or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies an enable secret against a stored enable-secret hash.
    /// </summary>
    /// <param name="enableSecretHash">Stored hash produced with <see cref="EnableSecretHashSubject"/>.</param>
    /// <param name="secret">Candidate plaintext enable secret.</param>
    /// <returns><see langword="true"/> when verification succeeds; otherwise <see langword="false"/>.</returns>
    public bool VerifyEnableSecret(string enableSecretHash, string secret) =>
        VerifyPassword(EnableSecretHashSubject, enableSecretHash, secret);

    /// <summary>
    /// Performs a structural check that <paramref name="passwordHash"/> looks like an Identity hash.
    /// </summary>
    /// <param name="passwordHash">Candidate hash text.</param>
    /// <returns>
    /// <see langword="true"/> when the value is non-empty Base64 with at least 13 bytes and
    /// format marker <c>0x00</c> or <c>0x01</c>; otherwise <see langword="false"/>.
    /// Does not prove cryptographic correctness.
    /// </returns>
    public bool IsWellFormedHash(string? passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        try
        {
            // Identity hashes are base64 payloads; reject obvious non-hashes.
            var bytes = Convert.FromBase64String(passwordHash);
            return bytes.Length >= 13 && bytes[0] is 0x00 or 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opaque hash subject carrying the username bound into Identity's hash API.
    /// </summary>
    /// <param name="username">Username or enable-secret subject string.</param>
    private sealed class PasswordHashSubject(string username)
    {
        /// <summary>
        /// Subject string passed to <see cref="PasswordHasher{TUser}"/>.
        /// </summary>
        public string Username { get; } = username;
    }
}
