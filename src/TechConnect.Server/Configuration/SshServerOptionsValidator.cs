// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshServerOptionsValidator.cs
//
// Purpose:
// Validates TechConnectServer and file-logging options at startup (SSH, auth,
// RADIUS, connector TLS, audit, DNS) without logging secrets.
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Configuration;

/// <summary>
/// Startup validator for <see cref="TechConnectServerOptions"/>.
/// </summary>
/// <remarks>
/// When SSH is disabled, authentication and RADIUS checks are skipped.
/// When the connector listener is disabled, connector TLS checks are skipped.
/// Does not log secrets, password hashes, shared secrets, or private keys.
/// Privilege requirements for CLI commands are not validated here — see
/// <c>CliCommandPolicy</c> in Authorization.
/// </remarks>
public sealed class TechConnectServerOptionsValidator : IValidateOptions<TechConnectServerOptions>
{
    /// <summary>
    /// Hasher used only to verify Identity V3 hash well-formedness (no password verification).
    /// </summary>
    private readonly OperatorPasswordHasher _passwordHasher = new();

    /// <summary>
    /// Validates the bound TechConnect server options graph.
    /// </summary>
    /// <param name="name">Options name (unused).</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Success, or a failure listing all collected messages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ValidateOptionsResult Validate(string? name, TechConnectServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        ValidateSsh(options.Ssh ?? new SshServerOptions(), options.Authentication ?? new AuthenticationOptions(), failures);
        ValidateAuthentication(options.Authentication ?? new AuthenticationOptions(), options.Ssh?.Enabled == true, failures);
        ValidateRadius(options.Authentication ?? new AuthenticationOptions(), options.Ssh?.Enabled == true, failures);
        ValidateConnector(options.Connector ?? new ConnectorListenerOptions(), failures);
        ValidateAudit(options.Audit ?? new AuditOptions(), failures);
        ValidateDns(options.Dns ?? new DnsOptions(), failures);

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Validates local enable secret hashes and privilege levels (duplicates fail).
    /// </summary>
    /// <param name="local">Local authentication options.</param>
    /// <param name="failures">Mutable failure list.</param>
    private void ValidateLocalEnableSecrets(LocalAuthenticationOptions local, List<string> failures)
    {
        // Empty EnableSecretHash / EnableSecrets is allowed (local enable fails closed at runtime).
        var seenLevels = new HashSet<int>();

        if (!string.IsNullOrWhiteSpace(local.EnableSecretHash))
        {
            if (!_passwordHasher.IsWellFormedHash(local.EnableSecretHash))
            {
                failures.Add(
                    "TechConnectServer:Authentication:Local:EnableSecretHash is malformed. " +
                    "Generate an Identity V3 hash using --hash-password enable.");
            }
            else
            {
                seenLevels.Add(15);
            }
        }

        var secrets = local.EnableSecrets ?? [];
        for (var i = 0; i < secrets.Count; i++)
        {
            var entry = secrets[i] ?? new LocalEnableSecretOptions();
            var path = $"TechConnectServer:Authentication:Local:EnableSecrets[{i}]";

            if (!PrivilegeLevel.TryParse(entry.PrivilegeLevel, out var level, out var privilegeError))
            {
                failures.Add($"{path}:PrivilegeLevel is invalid. {privilegeError}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.SecretHash) ||
                !_passwordHasher.IsWellFormedHash(entry.SecretHash))
            {
                failures.Add(
                    $"{path}:SecretHash is malformed. Generate an Identity V3 hash using --hash-password enable.");
                continue;
            }

            if (!seenLevels.Add(level.Value))
            {
                failures.Add(
                    $"{path}:PrivilegeLevel {level.Value} duplicates another enable secret " +
                    "(including EnableSecretHash which maps to level 15).");
            }
        }
    }

    /// <summary>
    /// Validates audit retention bounds.
    /// </summary>
    /// <param name="audit">Audit options.</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateAudit(AuditOptions audit, List<string> failures)
    {
        if (audit.MaximumEvents < 1)
        {
            failures.Add("TechConnectServer:Audit:MaximumEvents must be at least 1.");
        }
        else if (audit.MaximumEvents > 1_000_000)
        {
            failures.Add("TechConnectServer:Audit:MaximumEvents must not exceed 1000000.");
        }
    }

    /// <summary>
    /// Validates DNS query timeout and that each server entry is an IPv4/IPv6 address.
    /// </summary>
    /// <param name="dns">DNS options.</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateDns(DnsOptions dns, List<string> failures)
    {
        if (dns.QueryTimeoutSeconds < 1)
        {
            failures.Add("TechConnectServer:Dns:QueryTimeoutSeconds must be at least 1.");
        }
        else if (dns.QueryTimeoutSeconds > 60)
        {
            failures.Add("TechConnectServer:Dns:QueryTimeoutSeconds must not exceed 60.");
        }

        for (var i = 0; i < dns.Servers.Count; i++)
        {
            var entry = dns.Servers[i];
            if (string.IsNullOrWhiteSpace(entry))
            {
                failures.Add($"TechConnectServer:Dns:Servers[{i}] must be a non-empty IP address.");
                continue;
            }

            if (!System.Net.IPAddress.TryParse(entry.Trim(), out var address) ||
                address.AddressFamily is not (System.Net.Sockets.AddressFamily.InterNetwork
                    or System.Net.Sockets.AddressFamily.InterNetworkV6))
            {
                failures.Add(
                    $"TechConnectServer:Dns:Servers[{i}] '{entry}' is not a valid IPv4 or IPv6 address.");
            }
        }
    }

    /// <summary>
    /// Validates SSH bind settings, rejects legacy global authorized keys, and connection limits.
    /// </summary>
    /// <param name="ssh">SSH options.</param>
    /// <param name="authentication">Authentication options (reserved; currently unused).</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateSsh(
        SshServerOptions ssh,
        AuthenticationOptions authentication,
        List<string> failures)
    {
        if (!ssh.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ssh.ListenAddress))
        {
            failures.Add("TechConnectServer:Ssh:ListenAddress is required when SSH is enabled.");
        }
        else if (!IPAddress.TryParse(ssh.ListenAddress, out _))
        {
            failures.Add(
                $"TechConnectServer:Ssh:ListenAddress '{ssh.ListenAddress}' is not a valid IP address.");
        }

        if (ssh.Port is < 1 or > 65535)
        {
            failures.Add("TechConnectServer:Ssh:Port must be between 1 and 65535.");
        }

        var legacyKeys = ssh.AuthorizedPublicKeys ?? [];
        if (legacyKeys.Any(static k => !string.IsNullOrWhiteSpace(k)))
        {
            failures.Add(
                "TechConnectServer:Ssh:AuthorizedPublicKeys is no longer supported. " +
                "Migrate each operator public key to TechConnectServer:Authentication:Local:LocalUsers[*]:AuthorizedPublicKeys " +
                "and clear the legacy list. Global keys are not accepted as a fallback.");
        }

        if (ssh.MaximumConcurrentConnections < 1)
        {
            failures.Add("TechConnectServer:Ssh:MaximumConcurrentConnections must be at least 1.");
        }

        if (ssh.ShutdownTimeoutSeconds < 1)
        {
            failures.Add("TechConnectServer:Ssh:ShutdownTimeoutSeconds must be at least 1.");
        }

        _ = authentication;
    }

    /// <summary>
    /// Validates local users, auth method enablement, and that enabled methods have capable accounts.
    /// </summary>
    /// <param name="authentication">Authentication options.</param>
    /// <param name="sshEnabled">When <see langword="false"/>, validation is skipped.</param>
    /// <param name="failures">Mutable failure list.</param>
    private void ValidateAuthentication(AuthenticationOptions authentication, bool sshEnabled, List<string> failures)
    {
        if (!sshEnabled)
        {
            return;
        }

        var local = authentication.Local ?? new LocalAuthenticationOptions();
        var users = local.LocalUsers ?? [];
        var seenUsernames = new HashSet<string>(OperatorUsernameRules.Comparer);
        var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);
        var passwordCapable = 0;
        var publicKeyCapable = 0;

        ValidateLocalEnableSecrets(local, failures);

        for (var i = 0; i < users.Count; i++)
        {
            var user = users[i] ?? new LocalOperatorAccountOptions();
            var path = $"TechConnectServer:Authentication:Local:LocalUsers[{i}]";
            var username = user.Username?.Trim() ?? string.Empty;

            if (!OperatorUsernameRules.IsValid(username))
            {
                failures.Add(
                    $"{path}:Username is invalid. Usernames must be 1–64 characters and contain only letters, digits, '.', '_', '@', '+', or '-'.");
                continue;
            }

            if (!seenUsernames.Add(username))
            {
                failures.Add($"{path}:Username '{username}' duplicates another configured account (comparison is case-insensitive).");
            }

            if (!PrivilegeLevel.TryParse(user.PrivilegeLevel, out _, out var privilegeError))
            {
                failures.Add(
                    $"{path}:PrivilegeLevel '{user.PrivilegeLevel}' is invalid. {privilegeError} " +
                    "Use an integer 1–15 (canonical), or legacy Administrator / Operator / ReadOnly for migration.");
            }

            var hasHash = !string.IsNullOrWhiteSpace(user.PasswordHash);
            if (hasHash && !_passwordHasher.IsWellFormedHash(user.PasswordHash))
            {
                failures.Add($"{path}:PasswordHash is malformed. Generate an Identity V3 hash using --hash-password.");
            }

            var keyLines = user.AuthorizedPublicKeys ?? [];
            var usableKeys = 0;
            for (var k = 0; k < keyLines.Count; k++)
            {
                var line = keyLines[k]?.Trim() ?? string.Empty;
                if (line.Length == 0)
                {
                    continue;
                }

                try
                {
                    var parsed = SshKeyMaterial.ParsePublicKeyLine(line, $"{path}:AuthorizedPublicKeys[{k}]");
                    var fingerprint = SshKeyMaterial.ComputeFingerprint(parsed.Key);
                    if (!seenFingerprints.Add(fingerprint))
                    {
                        failures.Add(
                            $"{path}:AuthorizedPublicKeys[{k}] duplicates a public key already configured for another account.");
                    }

                    usableKeys++;
                }
                catch (InvalidOperationException ex)
                {
                    failures.Add(ex.Message);
                }
            }

            if (user.Enabled && !hasHash && usableKeys == 0)
            {
                failures.Add(
                    $"{path}: enabled account '{username}' has neither a PasswordHash nor AuthorizedPublicKeys.");
            }

            if (user.Enabled && hasHash)
            {
                passwordCapable++;
            }

            if (user.Enabled && usableKeys > 0)
            {
                publicKeyCapable++;
            }
        }

        var radiusEnabled = authentication.Radius?.Enabled == true;

        if (!local.PasswordAuthenticationEnabled &&
            !local.PublicKeyAuthenticationEnabled &&
            !radiusEnabled)
        {
            failures.Add(
                "TechConnectServer:Authentication must enable Local PasswordAuthenticationEnabled, " +
                "Local PublicKeyAuthenticationEnabled, and/or Radius.Enabled when SSH is enabled.");
        }

        if (local.PasswordAuthenticationEnabled && passwordCapable == 0)
        {
            failures.Add(
                "TechConnectServer:Authentication:Local:PasswordAuthenticationEnabled is true but no enabled local account has a PasswordHash.");
        }

        if (local.PublicKeyAuthenticationEnabled && publicKeyCapable == 0)
        {
            failures.Add(
                "TechConnectServer:Authentication:Local:PublicKeyAuthenticationEnabled is true but no enabled local account has AuthorizedPublicKeys.");
        }

        var radius = authentication.Radius ?? new RadiusAuthenticationOptions();
        if (radius.LocalFallbackEnabled && passwordCapable == 0)
        {
            failures.Add(
                "TechConnectServer:Authentication:Radius:LocalFallbackEnabled is true but no enabled local account has a PasswordHash.");
        }

        var needsLocalUsers =
            local.PasswordAuthenticationEnabled ||
            local.PublicKeyAuthenticationEnabled ||
            radius.LocalFallbackEnabled;

        if (needsLocalUsers && users.Count == 0)
        {
            failures.Add(
                "TechConnectServer:Authentication:Local:LocalUsers must contain at least one operator account when local authentication or RADIUS local fallback is enabled.");
        }
    }

    /// <summary>
    /// Validates RADIUS server settings when RADIUS is enabled and SSH is enabled.
    /// </summary>
    /// <param name="authentication">Authentication options containing RADIUS.</param>
    /// <param name="sshEnabled">When <see langword="false"/>, enabled RADIUS is not validated further.</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateRadius(AuthenticationOptions authentication, bool sshEnabled, List<string> failures)
    {
        var radius = authentication.Radius ?? new RadiusAuthenticationOptions();
        if (!radius.Enabled)
        {
            if (radius.LocalFallbackEnabled)
            {
                failures.Add(
                    "TechConnectServer:Authentication:Radius:LocalFallbackEnabled requires Radius:Enabled to be true.");
            }

            return;
        }

        if (!sshEnabled)
        {
            return;
        }

        ValidateRadiusServer(radius.PrimaryServer, "TechConnectServer:Authentication:Radius:PrimaryServer", required: true, failures);

        if (IsConfiguredRadiusServer(radius.SecondaryServer))
        {
            ValidateRadiusServer(
                radius.SecondaryServer,
                "TechConnectServer:Authentication:Radius:SecondaryServer",
                required: true,
                failures);
        }

        if (radius.TimeoutMilliseconds is < 100 or > 120_000)
        {
            failures.Add(
                "TechConnectServer:Authentication:Radius:TimeoutMilliseconds must be between 100 and 120000.");
        }

        if (radius.MaxAttempts is < 1 or > 5)
        {
            failures.Add(
                "TechConnectServer:Authentication:Radius:MaxAttempts must be between 1 and 5. " +
                "Retries apply only to Unavailable outcomes for a single server.");
        }

        if (string.IsNullOrWhiteSpace(radius.NasIdentifier))
        {
            failures.Add("TechConnectServer:Authentication:Radius:NasIdentifier is required when RADIUS is enabled.");
        }
        else if (radius.NasIdentifier.Length > 253)
        {
            failures.Add("TechConnectServer:Authentication:Radius:NasIdentifier must be at most 253 characters.");
        }

        if (!string.IsNullOrWhiteSpace(radius.NasIpAddress) &&
            !IPAddress.TryParse(radius.NasIpAddress, out _))
        {
            failures.Add(
                $"TechConnectServer:Authentication:Radius:NasIpAddress '{radius.NasIpAddress}' is not a valid IP address.");
        }

        // Privilege is obtained from Cisco-AVPair shell:priv-lvl on Access-Accept (fail-closed).
    }

    /// <summary>
    /// Returns whether a RADIUS server entry has a non-empty address.
    /// </summary>
    /// <param name="server">Server options, possibly null.</param>
    /// <returns><see langword="true"/> when an address is configured.</returns>
    private static bool IsConfiguredRadiusServer(RadiusServerOptions? server) =>
        server is not null && !string.IsNullOrWhiteSpace(server.Address);

    /// <summary>
    /// Validates one RADIUS server address, port, and shared secret.
    /// </summary>
    /// <param name="server">Server options.</param>
    /// <param name="path">Configuration path prefix for failure messages.</param>
    /// <param name="required">When <see langword="true"/>, a missing address is a failure.</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateRadiusServer(
        RadiusServerOptions? server,
        string path,
        bool required,
        List<string> failures)
    {
        server ??= new RadiusServerOptions();
        if (string.IsNullOrWhiteSpace(server.Address))
        {
            if (required)
            {
                failures.Add($"{path}:Address is required when RADIUS is enabled.");
            }

            return;
        }

        if (server.Port is < 1 or > 65535)
        {
            failures.Add($"{path}:Port must be between 1 and 65535.");
        }

        if (string.IsNullOrEmpty(server.SharedSecret))
        {
            failures.Add($"{path}:SharedSecret is required.");
        }
        else if (server.SharedSecret.Length > 256)
        {
            failures.Add($"{path}:SharedSecret must be at most 256 characters.");
        }
    }

    /// <summary>
    /// Validates connector TLS bind, thumbprint, store, and mutual-TLS requirements when enabled.
    /// </summary>
    /// <param name="connector">Connector listener options.</param>
    /// <param name="failures">Mutable failure list.</param>
    private static void ValidateConnector(ConnectorListenerOptions connector, List<string> failures)
    {
        if (!connector.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(connector.ListenAddress))
        {
            failures.Add("TechConnectServer:Connector:ListenAddress is required when the connector listener is enabled.");
        }
        else if (!IPAddress.TryParse(connector.ListenAddress, out _))
        {
            failures.Add(
                $"TechConnectServer:Connector:ListenAddress '{connector.ListenAddress}' is not a valid IP address.");
        }

        if (connector.Port is < 1 or > 65535)
        {
            failures.Add("TechConnectServer:Connector:Port must be between 1 and 65535.");
        }

        var thumbprint = CertificateThumbprint.Normalize(connector.ServerCertificateThumbprint);
        if (thumbprint.Length is not (40 or 64) || !thumbprint.All(char.IsAsciiHexDigit))
        {
            failures.Add(
                "TechConnectServer:Connector:ServerCertificateThumbprint must be a 40- or 64-character hex thumbprint when the connector listener is enabled.");
        }

        if (!string.IsNullOrWhiteSpace(connector.ServerCertificateStoreName) &&
            !Enum.TryParse<StoreName>(connector.ServerCertificateStoreName, ignoreCase: true, out _))
        {
            failures.Add("TechConnectServer:Connector:ServerCertificateStoreName must be a valid X509 StoreName.");
        }

        if (!Enum.IsDefined(connector.ServerCertificateStoreLocation))
        {
            failures.Add("TechConnectServer:Connector:ServerCertificateStoreLocation must be CurrentUser or LocalMachine.");
        }

        var clientValidation = connector.ClientCertificateValidation ?? new ClientCertificateValidationOptions();
        if (!clientValidation.RequireClientCertificate)
        {
            failures.Add(
                "TechConnectServer:Connector:ClientCertificateValidation:RequireClientCertificate must be true.");
        }

        foreach (var ca in clientValidation.AllowedCertificateAuthorities ?? [])
        {
            if (string.IsNullOrWhiteSpace(ca))
            {
                continue;
            }

            var normalized = CertificateThumbprint.Normalize(ca);
            if (normalized.Length is not (40 or 64) || !normalized.All(char.IsAsciiHexDigit))
            {
                failures.Add(
                    "TechConnectServer:Connector:ClientCertificateValidation:AllowedCertificateAuthorities entries must be 40- or 64-character hex thumbprints.");
                break;
            }
        }

        if (connector.MaximumConcurrentConnections < 1)
        {
            failures.Add("TechConnectServer:Connector:MaximumConcurrentConnections must be at least 1.");
        }

        if (connector.HandshakeTimeoutSeconds < 1)
        {
            failures.Add("TechConnectServer:Connector:HandshakeTimeoutSeconds must be at least 1.");
        }

        if (connector.ShutdownTimeoutSeconds < 1)
        {
            failures.Add("TechConnectServer:Connector:ShutdownTimeoutSeconds must be at least 1.");
        }
    }
}

/// <summary>
/// Startup validator for <see cref="FileLoggingOptions"/>.
/// </summary>
public sealed class FileLoggingOptionsValidator : IValidateOptions<FileLoggingOptions>
{
    /// <summary>
    /// Validates file name prefix and rolling file limits.
    /// </summary>
    /// <param name="name">Options name (unused).</param>
    /// <param name="options">File logging options.</param>
    /// <returns>Success, or a failure listing all collected messages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ValidateOptionsResult Validate(string? name, FileLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.FileNamePrefix))
        {
            failures.Add("Logging:File:FileNamePrefix is required.");
        }

        if (options.RetainedFileCountLimit < 1)
        {
            failures.Add("Logging:File:RetainedFileCountLimit must be at least 1.");
        }

        if (options.FileSizeLimitBytes < 1024)
        {
            failures.Add("Logging:File:FileSizeLimitBytes must be at least 1024.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Normalizes certificate thumbprint strings for validation (hex digits only, upper-cased).
/// </summary>
internal static class CertificateThumbprint
{
    /// <summary>
    /// Strips non-hex characters and upper-cases remaining ASCII hex digits.
    /// </summary>
    /// <param name="value">Raw thumbprint string, possibly null.</param>
    /// <returns>Normalized hex string (may be empty).</returns>
    public static string Normalize(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsAsciiHexDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
}
