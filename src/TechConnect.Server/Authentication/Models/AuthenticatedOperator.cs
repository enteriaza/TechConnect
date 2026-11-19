// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuthenticatedOperator.cs
//
// Purpose:
// Immutable operator identity established only after successful SSH
// authentication, carrying privilege and session correlation fields.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Models;

/// <summary>
/// Immutable identity established only after successful SSH authentication.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Privilege"/> is the Cisco-style level (1–15) used for CLI
/// authorization. This type does not itself enforce command policy; callers
/// pass it to authorization services.
/// </para>
/// <para>
/// Instances are value-like records and are safe to share across threads once
/// constructed. Optional fields may be <see langword="null"/> depending on
/// provider and method (for example RADIUS identities have no local account id).
/// </para>
/// </remarks>
/// <param name="Username">Canonical operator username from the successful auth path.</param>
/// <param name="Provider">Backend that authenticated the operator.</param>
/// <param name="Method">SSH method used (password or public key).</param>
/// <param name="Privilege">Authorized privilege level from the successful auth path.</param>
/// <param name="AuthenticatedAtUtc">UTC timestamp when authentication completed.</param>
/// <param name="OperatorSessionId">Opaque correlation id for this authenticated session.</param>
/// <param name="LocalAccountId">
/// Local store account id when <paramref name="Provider"/> is
/// <see cref="AuthenticationProviderKind.Local"/>; otherwise typically
/// <see langword="null"/>.
/// </param>
/// <param name="PublicKeyFingerprint">
/// SSH key fingerprint when <paramref name="Method"/> is
/// <see cref="AuthenticationMethodKind.PublicKey"/>; otherwise
/// <see langword="null"/>.
/// </param>
internal sealed record AuthenticatedOperator(
    string Username,
    AuthenticationProviderKind Provider,
    AuthenticationMethodKind Method,
    PrivilegeLevel Privilege,
    DateTimeOffset AuthenticatedAtUtc,
    string OperatorSessionId,
    string? LocalAccountId,
    string? PublicKeyFingerprint);
