// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    EnablePrivilegeResult.cs
//
// Purpose:
// Structured outcome of enable re-authentication / privilege authorization,
// including success factories and failure kinds used by the CLI enable path.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authentication;

/// <summary>
/// Classifies why an enable authentication or authorization attempt failed.
/// </summary>
internal enum EnableAuthFailureKind
{
    /// <summary>
    /// Enable path is not configured or the provider is disabled; fail closed.
    /// </summary>
    Unavailable = 1,

    /// <summary>
    /// Provider explicitly rejected the credentials (for example RADIUS Access-Reject).
    /// </summary>
    Rejected = 2,

    /// <summary>
    /// Authentication failed for a non-reject reason (bad secret, ambiguous match, transport error mapped to failure).
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The operation was cancelled via <see cref="CancellationToken"/>.
    /// </summary>
    Cancelled = 4,

    /// <summary>
    /// Authentication succeeded but requested privilege exceeds authorized privilege.
    /// </summary>
    InsufficientPrivilege = 5
}

/// <summary>
/// Structured outcome of enable re-authentication / authorization.
/// </summary>
/// <remarks>
/// Does not mutate session privilege; callers apply the granted (requested) level
/// when <see cref="Succeeded"/> is <see langword="true"/>. On failure,
/// <see cref="Privilege"/> is <c>default</c> and <see cref="FailureKind"/> is set.
/// </remarks>
/// <param name="Succeeded"><see langword="true"/> when enable auth and privilege check succeeded.</param>
/// <param name="Privilege">Granted (requested) level on success; otherwise <c>default</c>.</param>
/// <param name="AuthorizedPrivilege">
/// Maximum privilege authorized by the enable path when known; may be set on
/// <see cref="EnableAuthFailureKind.InsufficientPrivilege"/> failures.
/// </param>
/// <param name="FailureKind">Failure classification when <see cref="Succeeded"/> is <see langword="false"/>.</param>
/// <param name="Description">Optional operator-facing or log description of the outcome.</param>
internal sealed record EnablePrivilegeResult(
    bool Succeeded,
    PrivilegeLevel Privilege,
    PrivilegeLevel? AuthorizedPrivilege,
    EnableAuthFailureKind? FailureKind,
    string? Description)
{
    /// <summary>
    /// Creates a successful result granting the requested level within an authorized maximum.
    /// </summary>
    /// <param name="grantedRequested">Privilege level the caller requested and is allowed to assume.</param>
    /// <param name="authorizedMaximum">Maximum privilege the enable path authorized.</param>
    /// <returns>A succeeded result with no failure kind.</returns>
    public static EnablePrivilegeResult Success(
        PrivilegeLevel grantedRequested,
        PrivilegeLevel authorizedMaximum) =>
        new(true, grantedRequested, authorizedMaximum, null, null);

    /// <summary>
    /// Creates a failed result with the given classification.
    /// </summary>
    /// <param name="failureKind">Why enable failed.</param>
    /// <param name="description">Optional description returned to callers.</param>
    /// <param name="authorizedPrivilege">Optional authorized maximum when known (insufficient-privilege path).</param>
    /// <returns>A failed result with <see cref="Privilege"/> left at <c>default</c>.</returns>
    public static EnablePrivilegeResult Fail(
        EnableAuthFailureKind failureKind,
        string? description = null,
        PrivilegeLevel? authorizedPrivilege = null) =>
        new(false, default, authorizedPrivilege, failureKind, description);
}
