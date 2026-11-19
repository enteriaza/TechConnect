// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshTestKeys.cs
//
// Purpose:
// Generates ephemeral SSH key pairs and exports PEM / authorized_keys lines for
// SSH host and operator authentication tests.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Ephemeral SSH key helpers for tests (ECDSA P-384 when available, else RSA SHA-512).
/// </summary>
/// <remarks>
/// Keys are generated in-process and are not persisted unless a test writes them to a temp appsettings file.
/// </remarks>
internal static class SshTestKeys
{
    /// <summary>
    /// Generates a new key pair for host or operator use in tests.
    /// </summary>
    /// <param name="comment">Optional key comment.</param>
    /// <returns>Generated <see cref="IKeyPair"/>.</returns>
    public static IKeyPair CreateEcdsaKeyPair(string? comment = null)
    {
        var algorithm = SshAlgorithms.PublicKey.ECDsaSha2Nistp384;
        if (!algorithm.IsAvailable)
        {
            algorithm = SshAlgorithms.PublicKey.RsaWithSha512;
        }

        var key = algorithm.GenerateKeyPair();
        if (!string.IsNullOrWhiteSpace(comment))
        {
            key.Comment = comment;
        }

        return key;
    }

    /// <summary>
    /// Exports a private key as OpenSSH PEM ending with a newline.
    /// </summary>
    /// <param name="key">Key pair.</param>
    /// <returns>PEM text.</returns>
    public static string ExportPrivateKeyPem(IKeyPair key) =>
        KeyPair.ExportPrivateKey(key, passphrase: null, KeyFormat.OpenSsh, KeyEncoding.Pem).Trim()
        + Environment.NewLine;

    /// <summary>
    /// Exports a public key line, optionally forcing a trailing comment for authorized_keys tests.
    /// </summary>
    /// <param name="key">Key pair.</param>
    /// <param name="comment">Optional comment replacing any exported comment.</param>
    /// <returns>authorized_keys-style line.</returns>
    public static string ExportPublicKeyLine(IKeyPair key, string? comment = null)
    {
        var line = KeyPair.ExportPublicKey(key, KeyFormat.Ssh, KeyEncoding.SshBase64).Trim();
        if (!string.IsNullOrWhiteSpace(comment))
        {
            // Replace or append comment for test scenarios.
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return $"{parts[0]} {parts[1]} {comment}";
            }
        }

        return line;
    }
}
