// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    LocalOperatorAccount.cs
//
// Purpose:
// In-memory representations of a local operator account and its authorized
// SSH public keys used by the local authentication store.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.Algorithms;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authentication.Local;

/// <summary>
/// Local operator account loaded from configuration for password and/or public-key auth.
/// </summary>
/// <remarks>
/// Instances are immutable after construction (<c>init</c> properties). Password
/// material is stored only as <see cref="PasswordHash"/>; plaintext secrets are
/// never retained on this type.
/// </remarks>
internal sealed class LocalOperatorAccount
{
    /// <summary>
    /// Operator username as configured (lookup uses <see cref="OperatorUsernameRules"/>).
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Stable local account identifier written into authenticated-operator claims.
    /// </summary>
    public required string AccountId { get; init; }

    /// <summary>
    /// When <see langword="false"/>, the account must not authenticate successfully.
    /// </summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Privilege level granted on successful authentication with this account.
    /// </summary>
    public required PrivilegeLevel Privilege { get; init; }

    /// <summary>
    /// Password hash for local password authentication, or <see langword="null"/> when password auth is not configured for this account.
    /// </summary>
    public string? PasswordHash { get; init; }

    /// <summary>
    /// Authorized SSH public keys for this account; empty when public-key auth is not configured.
    /// </summary>
    public required IReadOnlyList<AuthorizedPublicKey> AuthorizedPublicKeys { get; init; }

    /// <summary>
    /// <see langword="true"/> when <see cref="PasswordHash"/> is non-empty.
    /// </summary>
    public bool HasPassword => !string.IsNullOrWhiteSpace(PasswordHash);

    /// <summary>
    /// <see langword="true"/> when at least one authorized public key is present.
    /// </summary>
    public bool HasPublicKeys => AuthorizedPublicKeys.Count > 0;
}

/// <summary>
/// A single authorized SSH public key bound to a <see cref="LocalOperatorAccount"/>.
/// </summary>
internal sealed class AuthorizedPublicKey
{
    /// <summary>
    /// Parsed SSH key material used for signature verification.
    /// </summary>
    public required IKeyPair Key { get; init; }

    /// <summary>
    /// Fingerprint string recorded on successful public-key authentication.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>
    /// Optional comment from the authorized-keys entry; not used for authorization.
    /// </summary>
    public string? Comment { get; init; }
}
