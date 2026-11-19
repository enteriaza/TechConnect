// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ScpHostKeyConfirmation.cs
//
// Purpose:
// Presents per-connection SCP SSH host-key details and helpers for explicit
// operator yes/no confirmation that is never persisted.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.Algorithms;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Copy;

/// <summary>Presented SSH host key details for a single SCP connection (not persisted).</summary>
/// <remarks>
/// Approval applies only to the current outbound SSH connection. TechConnect does
/// not store host keys or remember prior answers across connections.
/// </remarks>
internal sealed class ScpHostKeyInfo
{
    /// <summary>Remote host name or address from the copy source.</summary>
    public required string Host { get; init; }

    /// <summary>SSH port used for this connection.</summary>
    public required int Port { get; init; }

    /// <summary>Server public-key algorithm name.</summary>
    public required string Algorithm { get; init; }

    /// <summary>OpenSSH-style <c>SHA256:&lt;base64-without-padding&gt;</c>.</summary>
    public required string Fingerprint { get; init; }
}

/// <summary>SHA-256 host-key fingerprint helpers for SCP operator confirmation.</summary>
internal static class ScpHostKeyFingerprint
{
    /// <summary>
    /// Computes the OpenSSH-style SHA-256 fingerprint for a server public key.
    /// </summary>
    /// <param name="publicKey">Server host key material.</param>
    /// <returns>Fingerprint string starting with <c>SHA256:</c> without base64 padding.</returns>
    public static string Compute(IKeyPair publicKey) =>
        SshKeyMaterial.ComputeFingerprint(publicKey);

    /// <summary>
    /// Builds a <see cref="ScpHostKeyInfo"/> for operator prompting.
    /// </summary>
    /// <param name="host">Remote host.</param>
    /// <param name="port">SSH port.</param>
    /// <param name="publicKey">Presented server public key.</param>
    /// <returns>Info DTO for <see cref="FormatPrompt"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is null.</exception>
    public static ScpHostKeyInfo Create(string host, int port, IKeyPair publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(publicKey);
        return new ScpHostKeyInfo
        {
            Host = host,
            Port = port,
            Algorithm = publicKey.KeyAlgorithmName,
            Fingerprint = Compute(publicKey)
        };
    }

    /// <summary>Only an explicit affirmative (case-insensitive <c>yes</c>) accepts.</summary>
    /// <param name="response">Operator reply text.</param>
    /// <returns>True only for trimmed case-insensitive <c>yes</c>; <c>y</c>/<c>true</c>/empty are false.</returns>
    public static bool IsExplicitYes(string? response) =>
        string.Equals(response?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Formats the interactive host-key prompt, including the non-persistence disclaimer.
    /// </summary>
    /// <param name="info">Host key details for this connection only.</param>
    /// <returns>Multi-line prompt ending with <c>[yes/no]:</c>.</returns>
    public static string FormatPrompt(ScpHostKeyInfo info) =>
        "SSH server host key:\r\n" +
        $"  Host:        {info.Host}\r\n" +
        $"  Port:        {info.Port}\r\n" +
        $"  Algorithm:   {info.Algorithm}\r\n" +
        $"  Fingerprint: {info.Fingerprint}\r\n" +
        "\r\n" +
        "Approval applies only to this connection and is not remembered.\r\n" +
        "Do you trust this host key for this connection only? [yes/no]: ";
}
