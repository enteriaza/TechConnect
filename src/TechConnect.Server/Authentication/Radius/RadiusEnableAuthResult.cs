// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusEnableAuthResult.cs
//
// Purpose:
// Structured enable re-authentication outcome produced by the RADIUS path only
// (no local fallback, no operator session identity).
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// Enable re-auth outcome from RADIUS only (no local fallback, no operator session).
/// </summary>
/// <param name="Succeeded"><see langword="true"/> when Access-Accept mapped a usable privilege.</param>
/// <param name="Privilege">Authorized privilege on success; otherwise <c>default</c>.</param>
/// <param name="FailureKind">Failure classification when <see cref="Succeeded"/> is <see langword="false"/>.</param>
internal readonly record struct RadiusEnableAuthResult(
    bool Succeeded,
    PrivilegeLevel Privilege,
    EnableAuthFailureKind? FailureKind)
{
    /// <summary>
    /// Creates a successful RADIUS enable result.
    /// </summary>
    /// <param name="privilege">Privilege extracted from the Access-Accept.</param>
    /// <returns>A succeeded result with no failure kind.</returns>
    public static RadiusEnableAuthResult Ok(PrivilegeLevel privilege) =>
        new(true, privilege, null);

    /// <summary>
    /// Creates a failed RADIUS enable result.
    /// </summary>
    /// <param name="failureKind">Why enable authentication failed.</param>
    /// <returns>A failed result with <see cref="Privilege"/> left at <c>default</c>.</returns>
    public static RadiusEnableAuthResult Fail(EnableAuthFailureKind failureKind) =>
        new(false, default, failureKind);
}
