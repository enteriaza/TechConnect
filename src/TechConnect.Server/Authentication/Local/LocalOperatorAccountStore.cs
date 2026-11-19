// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    LocalOperatorAccountStore.cs
//
// Purpose:
// Loads an immutable local operator account map from validated configuration and
// performs password / public-key authentication lookups against it.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Authentication.Local;

/// <summary>
/// Immutable local operator account store loaded once from validated configuration.
/// </summary>
/// <remarks>
/// <para>
/// Construction fails with <see cref="InvalidOperationException"/> when a configured
/// privilege level cannot be parsed. After construction the dictionary is read-only;
/// concurrent readers are safe.
/// </para>
/// <para>
/// Failed authentication paths clear the <c>out</c> account to avoid username-existence
/// signals in caller messages.
/// </para>
/// </remarks>
internal sealed class LocalOperatorAccountStore
{
    /// <summary>
    /// Accounts keyed by normalized username (<see cref="OperatorUsernameRules.Comparer"/>).
    /// </summary>
    private readonly IReadOnlyDictionary<string, LocalOperatorAccount> _byUsername;

    /// <summary>
    /// Hasher used for local password verification.
    /// </summary>
    private readonly OperatorPasswordHasher _passwordHasher;

    /// <summary>
    /// Builds the store from <see cref="TechConnectServerOptions"/> local authentication settings.
    /// </summary>
    /// <param name="options">Server options; local section may be absent (treated as empty defaults).</param>
    /// <param name="passwordHasher">Password hasher used by verify methods.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="passwordHasher"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A configured account has an invalid <c>PrivilegeLevel</c>.
    /// </exception>
    public LocalOperatorAccountStore(
        IOptions<TechConnectServerOptions> options,
        OperatorPasswordHasher passwordHasher)
    {
        ArgumentNullException.ThrowIfNull(options);
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));

        var local = options.Value.Authentication?.Local ?? new LocalAuthenticationOptions();
        var map = new Dictionary<string, LocalOperatorAccount>(OperatorUsernameRules.Comparer);

        foreach (var configured in local.LocalUsers ?? [])
        {
            var username = configured.Username?.Trim() ?? string.Empty;
            if (!PrivilegeLevel.TryParse(configured.PrivilegeLevel, out var privilege, out var privilegeError))
            {
                throw new InvalidOperationException(
                    $"TechConnectServer:Authentication:Local:LocalUsers account '{username}' " +
                    $"has an invalid PrivilegeLevel: {privilegeError}");
            }

            var keys = LoadKeys(configured.AuthorizedPublicKeys ?? [], username);

            var account = new LocalOperatorAccount
            {
                Username = username,
                AccountId = OperatorUsernameRules.NormalizeKey(username),
                Enabled = configured.Enabled,
                Privilege = privilege,
                PasswordHash = string.IsNullOrWhiteSpace(configured.PasswordHash)
                    ? null
                    : configured.PasswordHash.Trim(),
                AuthorizedPublicKeys = keys
            };

            map.Add(account.AccountId, account);
        }

        _byUsername = map;
        PasswordAuthenticationEnabled = local.PasswordAuthenticationEnabled;
        PublicKeyAuthenticationEnabled = local.PublicKeyAuthenticationEnabled;
    }

    /// <summary>
    /// Whether local password authentication is enabled in configuration.
    /// </summary>
    public bool PasswordAuthenticationEnabled { get; }

    /// <summary>
    /// Whether local public-key authentication is enabled in configuration.
    /// </summary>
    public bool PublicKeyAuthenticationEnabled { get; }

    /// <summary>
    /// Number of configured local accounts.
    /// </summary>
    public int Count => _byUsername.Count;

    /// <summary>
    /// Looks up an account by username without authenticating.
    /// </summary>
    /// <param name="username">Candidate username; whitespace fails.</param>
    /// <param name="account">Matching account when found; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when an account exists for the normalized key.</returns>
    public bool TryGet(string? username, out LocalOperatorAccount? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return _byUsername.TryGetValue(OperatorUsernameRules.NormalizeKey(username), out account);
    }

    /// <summary>
    /// Authenticates a password when <see cref="PasswordAuthenticationEnabled"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="username">Operator username.</param>
    /// <param name="password">Candidate password.</param>
    /// <param name="account">Authenticated account on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when password authentication succeeds.</returns>
    public bool TryAuthenticatePassword(string? username, string? password, out LocalOperatorAccount? account)
    {
        account = null;
        if (!PasswordAuthenticationEnabled)
        {
            return false;
        }

        return TryVerifyPassword(username, password, out account);
    }

    /// <summary>
    /// Verifies a local password hash without requiring <see cref="PasswordAuthenticationEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Used for RADIUS local fallback when every configured RADIUS server is Unavailable.
    /// </remarks>
    /// <param name="username">Operator username.</param>
    /// <param name="password">Candidate password.</param>
    /// <param name="account">Matching enabled account with a verified password; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when verification succeeds.</returns>
    public bool TryVerifyPassword(string? username, string? password, out LocalOperatorAccount? account)
    {
        account = null;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryGet(username, out account) || account is null || !account.Enabled || !account.HasPassword)
        {
            // Avoid username-existence signals in caller messages.
            account = null;
            return false;
        }

        if (!_passwordHasher.VerifyPassword(account.Username, account.PasswordHash!, password))
        {
            account = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Authenticates a presented SSH public key against the account's authorized keys.
    /// </summary>
    /// <param name="username">Operator username.</param>
    /// <param name="presentedKey">Key presented by the SSH client.</param>
    /// <param name="account">Matching account on success; otherwise <see langword="null"/>.</param>
    /// <param name="fingerprint">
    /// Fingerprint of <paramref name="presentedKey"/> always computed; on success replaced
    /// with the matching authorized-key fingerprint.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when public-key auth is enabled, the account is enabled, and
    /// a key equality match is found.
    /// </returns>
    public bool TryAuthenticatePublicKey(
        string? username,
        IKeyPair presentedKey,
        out LocalOperatorAccount? account,
        out string fingerprint)
    {
        fingerprint = SshKeyMaterial.ComputeFingerprint(presentedKey);
        account = null;

        if (!PublicKeyAuthenticationEnabled || string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        if (!TryGet(username, out account) || account is null || !account.Enabled)
        {
            account = null;
            return false;
        }

        foreach (var authorized in account.AuthorizedPublicKeys)
        {
            if (SshKeyMaterial.PublicKeysEqual(authorized.Key, presentedKey))
            {
                fingerprint = authorized.Fingerprint;
                return true;
            }
        }

        account = null;
        return false;
    }

    /// <summary>
    /// Parses configured authorized-key lines into <see cref="AuthorizedPublicKey"/> entries.
    /// </summary>
    /// <param name="lines">Authorized-keys lines from configuration.</param>
    /// <param name="username">Username used only in parse-error path context strings.</param>
    /// <returns>Parsed keys; empty lines are skipped.</returns>
    private static IReadOnlyList<AuthorizedPublicKey> LoadKeys(IReadOnlyList<string> lines, string username)
    {
        var keys = new List<AuthorizedPublicKey>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i]?.Trim() ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            var key = SshKeyMaterial.ParsePublicKeyLine(
                line,
                $"TechConnectServer:Authentication:Local:LocalUsers[{username}]:AuthorizedPublicKeys[{i}]");
            keys.Add(new AuthorizedPublicKey
            {
                Key = key.Key,
                Fingerprint = SshKeyMaterial.ComputeFingerprint(key.Key),
                Comment = key.Comment
            });
        }

        return keys;
    }
}
