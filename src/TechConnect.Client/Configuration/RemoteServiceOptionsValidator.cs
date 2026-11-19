// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteServiceOptionsValidator.cs
//
// Purpose:
// Startup validation for RemoteService options (endpoint, TLS host, reconnect,
// buffer, client certificate thumbprint) without logging secrets.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace TechConnect.Client.Configuration;

/// <summary>
/// Startup validator for <see cref="RemoteServiceOptions"/>.
/// </summary>
/// <remarks>
/// Empty <see cref="RemoteServiceOptions.Host"/> is allowed so the app can start before
/// a remote endpoint is configured. When Host is set, port and placeholder-host checks
/// apply. Does not log thumbprints in full or any private-key material.
/// </remarks>
public sealed class RemoteServiceOptionsValidator : IValidateOptions<RemoteServiceOptions>
{
    /// <summary>
    /// Validates the bound <see cref="RemoteServiceOptions"/> graph.
    /// </summary>
    /// <param name="name">Options name (unused).</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Success, or a failure listing all collected messages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ValidateOptionsResult Validate(string? name, RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!string.IsNullOrWhiteSpace(options.Host))
        {
            if (IsPlaceholderHost(options.Host))
            {
                failures.Add("RemoteService:Host must not use a placeholder/example host.");
            }

            if (options.Port is < 1 or > 65535)
            {
                failures.Add("RemoteService:Port must be between 1 and 65535 when Host is configured.");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.TlsHostName) && IsPlaceholderHost(options.TlsHostName))
        {
            failures.Add("RemoteService:TlsHostName must not use a placeholder/example host.");
        }

        if (options.ConnectTimeoutSeconds < 1)
        {
            failures.Add("RemoteService:ConnectTimeoutSeconds must be at least 1.");
        }

        if (options.MaximumMessageBytes < RemoteServiceOptions.MinimumMaximumMessageBytes)
        {
            failures.Add(
                $"RemoteService:MaximumMessageBytes must be at least {RemoteServiceOptions.MinimumMaximumMessageBytes}.");
        }
        else if (options.MaximumMessageBytes > RemoteServiceOptions.AbsoluteMaximumMessageBytes)
        {
            failures.Add(
                $"RemoteService:MaximumMessageBytes must not exceed {RemoteServiceOptions.AbsoluteMaximumMessageBytes}.");
        }

        if (!Enum.IsDefined(options.CertificateRevocationMode))
        {
            failures.Add(
                "RemoteService:CertificateRevocationMode must be NoCheck, Online, or Offline.");
        }

        var reconnect = options.Reconnect ?? new ReconnectOptions();
        if (reconnect.InitialDelaySeconds < 0)
        {
            failures.Add("RemoteService:Reconnect:InitialDelaySeconds must be zero or greater.");
        }

        if (reconnect.MaxDelaySeconds < 1)
        {
            failures.Add("RemoteService:Reconnect:MaxDelaySeconds must be at least 1.");
        }

        if (reconnect.MaxDelaySeconds < reconnect.InitialDelaySeconds)
        {
            failures.Add("RemoteService:Reconnect:MaxDelaySeconds must be greater than or equal to InitialDelaySeconds.");
        }

        if (reconnect.BackoffMultiplier < 1.0)
        {
            failures.Add("RemoteService:Reconnect:BackoffMultiplier must be at least 1.0.");
        }

        if (reconnect.JitterFactor is < 0.0 or > 1.0)
        {
            failures.Add("RemoteService:Reconnect:JitterFactor must be between 0.0 and 1.0.");
        }

        var buffer = options.Buffer ?? new RemoteBufferOptions();
        if (buffer.MaximumQueuedBytes < 4096)
        {
            failures.Add("RemoteService:Buffer:MaximumQueuedBytes must be at least 4096.");
        }

        var certificate = options.ClientCertificate ?? new ClientCertificateOptions();
        if (!string.IsNullOrWhiteSpace(certificate.Thumbprint))
        {
            var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
            if (thumbprint.Length is not (40 or 64) || !thumbprint.All(Uri.IsHexDigit))
            {
                failures.Add("RemoteService:ClientCertificate:Thumbprint must be a 40- or 64-character hex SHA-1/SHA-256 thumbprint when provided.");
            }
        }

        if (!string.IsNullOrWhiteSpace(certificate.StoreName) &&
            !Enum.TryParse<StoreName>(certificate.StoreName, ignoreCase: true, out _))
        {
            failures.Add("RemoteService:ClientCertificate:StoreName must be a valid X509 StoreName value.");
        }

        if (!Enum.IsDefined(certificate.StoreLocation))
        {
            failures.Add("RemoteService:ClientCertificate:StoreLocation must be CurrentUser or LocalMachine.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    /// <summary>
    /// Normalizes a certificate thumbprint for comparison and validation.
    /// </summary>
    /// <param name="thumbprint">Raw thumbprint that may contain spaces or colons.</param>
    /// <returns>Uppercase hex string with spaces and colons removed.</returns>
    internal static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    /// <summary>
    /// Returns whether <paramref name="host"/> is a reserved example/placeholder DNS name.
    /// </summary>
    /// <param name="host">Host name to test.</param>
    /// <returns>
    /// <see langword="true"/> for <c>example.com</c>, <c>example.org</c>, or any subdomain of those.
    /// </returns>
    internal static bool IsPlaceholderHost(string host) =>
        host.EndsWith(".example.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".example.org", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("example.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("example.org", StringComparison.OrdinalIgnoreCase);
}
