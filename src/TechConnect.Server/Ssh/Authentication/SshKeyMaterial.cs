// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshKeyMaterial.cs
//
// Purpose:
// Imports, generates, exports, and fingerprints SSH host/operator key material
// without reading filesystem paths (PEM/authorized_keys text only).
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace TechConnect.Server.Ssh.Authentication;

/// <summary>
/// Helpers for importing, generating, exporting, and fingerprinting SSH key material.
/// </summary>
/// <remarks>
/// Does not read or write files. Invalid configured host keys are not auto-replaced.
/// <see cref="LoadAuthorizedPublicKeys"/> targets the legacy global key list path and
/// fails closed when empty; per-user keys use <see cref="ParsePublicKeyLine"/> instead.
/// </remarks>
internal static class SshKeyMaterial
{
    /// <summary>
    /// Imports an OpenSSH PEM (or compatible) private host key from configuration text.
    /// Does not generate keys and does not read files.
    /// </summary>
    /// <param name="privateKeyMaterial">PEM (or compatible) private key text.</param>
    /// <returns>Imported key pair that includes a private key.</returns>
    /// <exception cref="InvalidOperationException">
    /// Material is empty, lacks a private key, or cannot be imported.
    /// </exception>
    public static IKeyPair ImportHostPrivateKey(string privateKeyMaterial)
    {
        if (string.IsNullOrWhiteSpace(privateKeyMaterial))
        {
            throw new InvalidOperationException(
                "SSH host private key material is empty.");
        }

        try
        {
            var key = KeyPair.ImportKey(
                privateKeyMaterial.Trim(),
                passphrase: null,
                KeyFormat.Default,
                KeyEncoding.Default);

            if (!key.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    "Configured TechConnectServer:Ssh:HostPrivateKey does not contain a private key.");
            }

            return key;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Configured TechConnectServer:Ssh:HostPrivateKey is invalid and cannot be imported. " +
                "Startup will not replace an invalid key automatically.",
                ex);
        }
    }

    /// <summary>
    /// Loads non-empty lines from a legacy global authorized-keys list.
    /// </summary>
    /// <param name="configuredKeys">Configured public-key lines.</param>
    /// <returns>Parsed keys with source indices.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuredKeys"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">No usable keys remain after parsing.</exception>
    public static IReadOnlyList<AuthorizedOperatorKey> LoadAuthorizedPublicKeys(
        IReadOnlyList<string> configuredKeys)
    {
        ArgumentNullException.ThrowIfNull(configuredKeys);

        var keys = new List<AuthorizedOperatorKey>();
        for (var i = 0; i < configuredKeys.Count; i++)
        {
            var line = configuredKeys[i]?.Trim() ?? string.Empty;
            if (line.Length == 0)
            {
                continue;
            }

            var parsed = ParsePublicKeyLine(
                line,
                $"TechConnectServer:Ssh:AuthorizedPublicKeys[{i}]");
            keys.Add(new AuthorizedOperatorKey(parsed.Key, parsed.Comment, i));
        }

        if (keys.Count == 0)
        {
            throw new InvalidOperationException(
                "TechConnectServer:Ssh:AuthorizedPublicKeys contains no usable public keys. " +
                "The server fails closed when the authorized-key set is empty.");
        }

        return keys;
    }

    /// <summary>
    /// Parses a single OpenSSH authorized_keys public-key line.
    /// </summary>
    /// <param name="line">authorized_keys line text.</param>
    /// <param name="configPath">Configuration path used in error messages.</param>
    /// <returns>Parsed key with optional comment; <see cref="AuthorizedOperatorKey.SourceIndex"/> is -1.</returns>
    /// <exception cref="ArgumentException"><paramref name="line"/> or <paramref name="configPath"/> is blank.</exception>
    /// <exception cref="InvalidOperationException">The line cannot be imported as a public key.</exception>
    public static AuthorizedOperatorKey ParsePublicKeyLine(string line, string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);

        try
        {
            var key = KeyPair.ImportKey(line.Trim(), passphrase: null, KeyFormat.Default, KeyEncoding.Default);
            return new AuthorizedOperatorKey(key, ParseComment(line), SourceIndex: -1);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"{configPath} contains invalid public-key data.",
                ex);
        }
    }

    /// <summary>
    /// Generates a new host key pair (ECDSA P-384 when available, otherwise RSA SHA-512).
    /// </summary>
    /// <returns>Generated key pair with comment <c>techconnect-server-host</c>.</returns>
    public static IKeyPair GenerateHostKeyPair()
    {
        var algorithm = SshAlgorithms.PublicKey.ECDsaSha2Nistp384;
        if (!algorithm.IsAvailable)
        {
            algorithm = SshAlgorithms.PublicKey.RsaWithSha512;
        }

        var key = algorithm.GenerateKeyPair();
        key.Comment = "techconnect-server-host";
        return key;
    }

    /// <summary>
    /// Exports a private key as OpenSSH PEM text ending with a newline.
    /// </summary>
    /// <param name="key">Key pair containing a private key.</param>
    /// <returns>PEM text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static string ExportPrivateKeyPem(IKeyPair key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return KeyPair.ExportPrivateKey(key, passphrase: null, KeyFormat.OpenSsh, KeyEncoding.Pem).Trim()
            + Environment.NewLine;
    }

    /// <summary>
    /// Exports a public key as a single OpenSSH authorized_keys-style line.
    /// </summary>
    /// <param name="key">Key pair.</param>
    /// <returns>Trimmed public-key line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static string ExportPublicKeyLine(IKeyPair key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return KeyPair.ExportPublicKey(key, KeyFormat.Ssh, KeyEncoding.SshBase64).Trim();
    }

    /// <summary>
    /// Computes an OpenSSH-style SHA256 fingerprint (<c>SHA256:</c> + unpadded Base64).
    /// </summary>
    /// <param name="key">Key pair whose public key bytes are hashed.</param>
    /// <returns>Fingerprint string (never the private key).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public static string ComputeFingerprint(IKeyPair key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var publicKeyBytes = key.GetPublicKeyBytes().ToArray();
        var hash = SHA256.HashData(publicKeyBytes);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>
    /// Compares two keys by public key bytes.
    /// </summary>
    /// <param name="left">First key.</param>
    /// <param name="right">Second key.</param>
    /// <returns><see langword="true"/> when public key bytes are equal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.</exception>
    public static bool PublicKeysEqual(IKeyPair left, IKeyPair right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return left.GetPublicKeyBytes().Equals(right.GetPublicKeyBytes());
    }

    /// <summary>
    /// Extracts the trailing comment from an authorized_keys line when present.
    /// </summary>
    /// <param name="authorizedKeysLine">Full authorized_keys line.</param>
    /// <returns>Comment text, or <see langword="null"/> when fewer than three tokens.</returns>
    private static string? ParseComment(string authorizedKeysLine)
    {
        var parts = authorizedKeysLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? string.Join(' ', parts.Skip(2)) : null;
    }
}

/// <summary>
/// Parsed operator public key with optional comment and configuration source index.
/// </summary>
/// <param name="Key">Imported public key pair.</param>
/// <param name="Comment">Trailing comment from the authorized_keys line, if any.</param>
/// <param name="SourceIndex">Index in a configured list, or -1 when parsed ad hoc.</param>
internal sealed record AuthorizedOperatorKey(IKeyPair Key, string? Comment, int SourceIndex);
