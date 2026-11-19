// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuthenticatedOperatorClaims.cs
//
// Purpose:
// Maps between <see cref="TechConnect.Server.Authentication.Models.AuthenticatedOperator"/>
// and SSH <see cref="System.Security.Claims.ClaimsPrincipal"/> claim sets used
// after successful authentication.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Security.Claims;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authentication.Local;

/// <summary>
/// Claim type names and conversion helpers for authenticated operator principals.
/// </summary>
/// <remarks>
/// <see cref="CreatePrincipal"/> builds a principal for an already-authenticated
/// identity. <see cref="TryCreateOperator"/> rebuilds the identity only when all
/// required claims are present and parseable; otherwise it returns
/// <see langword="false"/> without throwing.
/// </remarks>
internal static class AuthenticatedOperatorClaims
{
    /// <summary>
    /// Claim type storing the numeric privilege level string.
    /// </summary>
    public const string PrivilegeClaim = "tc_privilege";

    /// <summary>
    /// Claim type storing <see cref="AuthenticationProviderKind"/>.
    /// </summary>
    public const string ProviderClaim = "tc_provider";

    /// <summary>
    /// Claim type storing <see cref="AuthenticationMethodKind"/>.
    /// </summary>
    public const string MethodClaim = "tc_method";

    /// <summary>
    /// Claim type storing the operator session correlation id.
    /// </summary>
    public const string SessionIdClaim = "tc_operator_session_id";

    /// <summary>
    /// Claim type storing the local account id when present.
    /// </summary>
    public const string AccountIdClaim = "tc_account_id";

    /// <summary>
    /// Claim type storing the SSH public-key fingerprint when present.
    /// </summary>
    public const string FingerprintClaim = "ssh_key_fingerprint";

    /// <summary>
    /// Claim type storing the round-trip ISO-8601 authentication timestamp.
    /// </summary>
    public const string AuthenticatedAtClaim = "tc_authenticated_at";

    /// <summary>
    /// Creates a <see cref="ClaimsPrincipal"/> from a successful authentication identity.
    /// </summary>
    /// <param name="identity">Authenticated operator; must not be <see langword="null"/>.</param>
    /// <returns>
    /// A principal whose authentication type is <c>ssh-publickey</c>, <c>ssh-password</c>,
    /// or <c>ssh</c> depending on <see cref="AuthenticatedOperator.Method"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public static ClaimsPrincipal CreatePrincipal(AuthenticatedOperator identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var authType = identity.Method switch
        {
            AuthenticationMethodKind.PublicKey => "ssh-publickey",
            AuthenticationMethodKind.Password => "ssh-password",
            _ => "ssh"
        };

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.Username),
            new(ClaimTypes.AuthenticationMethod, identity.Method.ToString()),
            new(PrivilegeClaim, identity.Privilege.ToString()),
            new(ProviderClaim, identity.Provider.ToString()),
            new(MethodClaim, identity.Method.ToString()),
            new(SessionIdClaim, identity.OperatorSessionId),
            new(AuthenticatedAtClaim, identity.AuthenticatedAtUtc.ToString("O", CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(identity.LocalAccountId))
        {
            claims.Add(new Claim(AccountIdClaim, identity.LocalAccountId));
        }

        if (!string.IsNullOrWhiteSpace(identity.PublicKeyFingerprint))
        {
            claims.Add(new Claim(FingerprintClaim, identity.PublicKeyFingerprint));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: authType));
    }

    /// <summary>
    /// Attempts to rebuild an <see cref="AuthenticatedOperator"/> from a principal's claims.
    /// </summary>
    /// <param name="principal">Principal to read; may be <see langword="null"/> or unauthenticated.</param>
    /// <param name="identity">
    /// Rebuilt identity when the method returns <see langword="true"/>; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the principal is authenticated and all required
    /// claims parse successfully; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryCreateOperator(ClaimsPrincipal? principal, out AuthenticatedOperator? identity)
    {
        identity = null;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var username = principal.FindFirst(ClaimTypes.Name)?.Value;
        var sessionId = principal.FindFirst(SessionIdClaim)?.Value;
        var privilegeRaw = principal.FindFirst(PrivilegeClaim)?.Value;
        var providerRaw = principal.FindFirst(ProviderClaim)?.Value;
        var methodRaw = principal.FindFirst(MethodClaim)?.Value;
        var authenticatedAtRaw = principal.FindFirst(AuthenticatedAtClaim)?.Value;

        if (string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(sessionId) ||
            !PrivilegeLevel.TryParse(privilegeRaw, out var privilege, out _) ||
            !Enum.TryParse<AuthenticationProviderKind>(providerRaw, ignoreCase: true, out var provider) ||
            !Enum.TryParse<AuthenticationMethodKind>(methodRaw, ignoreCase: true, out var method) ||
            !DateTimeOffset.TryParse(authenticatedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var authenticatedAt))
        {
            return false;
        }

        identity = new AuthenticatedOperator(
            Username: username,
            Provider: provider,
            Method: method,
            Privilege: privilege,
            AuthenticatedAtUtc: authenticatedAt,
            OperatorSessionId: sessionId,
            LocalAccountId: principal.FindFirst(AccountIdClaim)?.Value,
            PublicKeyFingerprint: principal.FindFirst(FingerprintClaim)?.Value);

        return true;
    }
}
