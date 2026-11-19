// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    LocalAuthenticationOptions.cs
//
// Purpose:
// Defines authentication option graphs for local operator accounts, enable
// secrets, and the Authentication section that also hosts RADIUS settings.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Configuration;

/// <summary>
/// Root authentication options under <c>TechConnectServer:Authentication</c>.
/// </summary>
public sealed class AuthenticationOptions
{
    /// <summary>
    /// Local password / public-key authentication and enable-secret configuration.
    /// </summary>
    public LocalAuthenticationOptions Local { get; set; } = new();

    /// <summary>
    /// RADIUS password authentication configuration.
    /// </summary>
    public RadiusAuthenticationOptions Radius { get; set; } = new();
}

/// <summary>
/// Local operator authentication flags, enable secrets, and account list.
/// </summary>
public sealed class LocalAuthenticationOptions
{
    /// <summary>
    /// When <see langword="true"/>, the SSH server offers password authentication against
    /// <see cref="LocalUsers"/> (Identity V3 hashes).
    /// </summary>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool PasswordAuthenticationEnabled { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, the SSH server offers public-key authentication against
    /// per-account <see cref="LocalOperatorAccountOptions.AuthorizedPublicKeys"/>.
    /// </summary>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool PublicKeyAuthenticationEnabled { get; set; } = true;

    /// <summary>
    /// Convenience enable secret authorizing privilege <b>15</b> (Identity V3 hash with subject
    /// <c>enable</c>). Equivalent to an <see cref="EnableSecrets"/> entry for level 15.
    /// </summary>
    /// <remarks>
    /// Empty alone does not disable enable when <see cref="EnableSecrets"/> is populated.
    /// Empty EnableSecretHash / EnableSecrets is allowed at validation time; local enable
    /// fails closed at runtime when no secrets are configured.
    /// </remarks>
    /// <value>Empty by default. Never log the plaintext secret or this hash in application logs.</value>
    public string EnableSecretHash { get; set; } = string.Empty;

    /// <summary>
    /// Privilege-specific enable secrets.
    /// </summary>
    /// <remarks>
    /// Each entry authorizes its <see cref="LocalEnableSecretOptions.PrivilegeLevel"/>
    /// and any requested level at or below that maximum.
    /// </remarks>
    public List<LocalEnableSecretOptions> EnableSecrets { get; set; } = [];

    /// <summary>
    /// Configured local operator accounts used for password and/or public-key SSH login.
    /// </summary>
    public List<LocalOperatorAccountOptions> LocalUsers { get; set; } = [];
}

/// <summary>
/// One local enable secret and the maximum privilege it authorizes (1–15).
/// </summary>
public sealed class LocalEnableSecretOptions
{
    /// <summary>
    /// Maximum privilege this secret authorizes (integer 1–15 as string or number in JSON).
    /// </summary>
    public string PrivilegeLevel { get; set; } = string.Empty;

    /// <summary>
    /// ASP.NET Identity V3 hash (subject <c>enable</c>).
    /// </summary>
    /// <value>Never log this value.</value>
    public string SecretHash { get; set; } = string.Empty;
}

/// <summary>
/// One local operator account used for SSH authentication.
/// </summary>
public sealed class LocalOperatorAccountOptions
{
    /// <summary>
    /// Operator username (case-insensitive uniqueness across accounts).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// When <see langword="false"/>, the account is ignored for both password and public-key login.
    /// </summary>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Cisco-style privilege 1–15 (canonical).
    /// </summary>
    /// <remarks>
    /// Legacy names Administrator / Operator / ReadOnly are accepted only as an
    /// unambiguous migration of existing config.
    /// </remarks>
    public string PrivilegeLevel { get; set; } = string.Empty;

    /// <summary>
    /// ASP.NET Identity V3 password hash. Empty when the account is public-key only.
    /// </summary>
    /// <value>Never log this value.</value>
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// OpenSSH authorized_keys public-key lines for this account.
    /// </summary>
    /// <remarks>
    /// Fingerprints must be unique across all local accounts. Legacy global
    /// <c>TechConnectServer:Ssh:AuthorizedPublicKeys</c> is rejected at startup.
    /// </remarks>
    public List<string> AuthorizedPublicKeys { get; set; } = [];
}
