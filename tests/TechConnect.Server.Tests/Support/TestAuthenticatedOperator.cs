// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TestAuthenticatedOperator.cs
//
// Purpose:
// Builds deterministic AuthenticatedOperator instances for CLI and SSH unit tests.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Factory for fixed-timestamp <see cref="AuthenticatedOperator"/> test identities.
/// </summary>
internal static class TestAuthenticatedOperator
{
    /// <summary>
    /// Creates an authenticated operator with stable defaults suitable for privilege tests.
    /// </summary>
    /// <param name="username">Operator username.</param>
    /// <param name="privilege">Privilege level; defaults to administrator boundary.</param>
    /// <param name="method">Authentication method kind.</param>
    /// <param name="operatorSessionId">Optional fixed session id; otherwise a new GUID.</param>
    /// <param name="localAccountId">Optional local account id; defaults to lowercased username.</param>
    /// <param name="publicKeyFingerprint">Optional public-key fingerprint.</param>
    /// <returns>New <see cref="AuthenticatedOperator"/> with fixed <c>AuthenticatedAtUtc</c>.</returns>
    public static AuthenticatedOperator Create(
        string username = "noc",
        PrivilegeLevel? privilege = null,
        AuthenticationMethodKind method = AuthenticationMethodKind.PublicKey,
        string? operatorSessionId = null,
        string? localAccountId = null,
        string? publicKeyFingerprint = null) =>
        new(
            Username: username,
            Provider: AuthenticationProviderKind.Local,
            Method: method,
            Privilege: privilege ?? PrivilegeLevel.AdministratorBoundary,
            AuthenticatedAtUtc: DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            OperatorSessionId: operatorSessionId ?? Guid.NewGuid().ToString("N"),
            LocalAccountId: localAccountId ?? username.ToLowerInvariant(),
            PublicKeyFingerprint: publicKeyFingerprint);
}
