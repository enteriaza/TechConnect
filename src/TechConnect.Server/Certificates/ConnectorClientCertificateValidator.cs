// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorClientCertificateValidator.cs
//
// Purpose:
// Validates connector client certificates against configured trust anchors.
//
// -----------------------------------------------------------------------------
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Certificates;

/// <summary>
/// Validated connector client identity extracted from an accepted client certificate.
/// </summary>
internal sealed class ConnectorClientIdentity
{
    /// <summary>
    /// Normalized certificate thumbprint used as the connector identity key.
    /// </summary>
    public required string Thumbprint { get; init; }

    /// <summary>
    /// Certificate subject distinguished name.
    /// </summary>
    public required string Subject { get; init; }

    /// <summary>
    /// Certificate issuer distinguished name.
    /// </summary>
    public required string Issuer { get; init; }
}

/// <summary>
/// Validates TechConnect.Client client certificates presented during mTLS.
/// Checks performed:
/// 1. Certificate must be present (missing client cert rejected).
/// 2. Validity window (NotBefore / NotAfter) against UTC now.
/// 3. Client Authentication EKU (1.3.6.1.5.5.7.3.2), or unconstrained when EKU is absent.
/// 4. X509 chain build with either system trust (no AllowedCertificateAuthorities)
///    or CustomRootTrust against configured CA thumbprints resolved from the store.
/// 5. Rejects RemoteCertificateNameMismatch / NotAvailable when they accompany chain failure;
///    name mismatch alone is ignored for client certificates (identity is the validated thumbprint).
/// Never accepts a certificate merely because it was presented.
/// </summary>
internal sealed class ConnectorClientCertificateValidator
{
    /// <summary>
    /// Client Authentication EKU OID (1.3.6.1.5.5.7.3.2).
    /// </summary>
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Server options supplying connector client-certificate validation settings.
    /// </summary>
    private readonly IOptions<TechConnectServerOptions> _options;

    /// <summary>
    /// Provider that resolves configured CA thumbprints to trust anchors.
    /// </summary>
    private readonly IClientCertificateTrustAnchorProvider _trustAnchors;

    /// <summary>
    /// Clock used for certificate validity-window checks.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger for validation failures (thumbprint prefixes only; no private keys).
    /// </summary>
    private readonly ILogger<ConnectorClientCertificateValidator> _logger;

    /// <summary>
    /// Creates a validator over options, trust-anchor provider, clock, and logger.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public ConnectorClientCertificateValidator(
        IOptions<TechConnectServerOptions> options,
        IClientCertificateTrustAnchorProvider trustAnchors,
        TimeProvider timeProvider,
        ILogger<ConnectorClientCertificateValidator> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _trustAnchors = trustAnchors ?? throw new ArgumentNullException(nameof(trustAnchors));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Attempts to Validate without throwing for ordinary failures.
    /// </summary>
    public bool TryValidate(
        X509Certificate2? certificate,
        X509Chain? presentedChain,
        SslPolicyErrors sslPolicyErrors,
        out ConnectorClientIdentity? identity,
        out string error)
    {
        identity = null;
        error = string.Empty;

        if (certificate is null)
        {
            error = "Client certificate was not provided.";
            return false;
        }

        // Re-load the public certificate so EKU/extension checks are reliable for SslStream-presented certs.
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now < leaf.NotBefore.ToUniversalTime())
        {
            error = "Client certificate is not valid yet.";
            return false;
        }

        if (now > leaf.NotAfter.ToUniversalTime())
        {
            error = "Client certificate has expired.";
            return false;
        }

        if (!SupportsClientAuthentication(leaf))
        {
            error = "Client certificate is not suitable for client authentication (missing Client Authentication EKU).";
            return false;
        }

        var validation = _options.Value.Connector.ClientCertificateValidation
            ?? new ClientCertificateValidationOptions();
        var allowed = (validation.AllowedCertificateAuthorities ?? [])
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(CertificateThumbprint.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        X509Certificate2Collection customRoots;
        try
        {
            customRoots = _trustAnchors.Resolve(allowed);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

        if (allowed.Length > 0)
        {
            if (customRoots.Count == 0)
            {
                error = "Allowed certificate authorities are configured but no trust anchors could be resolved.";
                return false;
            }

            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.AddRange(customRoots);
        }

        if (presentedChain is { ChainElements.Count: > 0 })
        {
            foreach (var element in presentedChain.ChainElements)
            {
                if (!string.Equals(
                        CertificateThumbprint.Normalize(element.Certificate.Thumbprint ?? string.Empty),
                        CertificateThumbprint.Normalize(leaf.Thumbprint ?? string.Empty),
                        StringComparison.OrdinalIgnoreCase))
                {
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
                }
            }
        }

        if (!chain.Build(leaf))
        {
            var status = string.Join(", ", chain.ChainStatus.Select(s => s.Status.ToString()));
            error = string.IsNullOrWhiteSpace(status)
                ? "Client certificate chain validation failed."
                : $"Client certificate chain validation failed: {status}.";
            return false;
        }

        // Ignore RemoteCertificateNameMismatch for client certs; identity is the thumbprint.
        // RemoteCertificateChainErrors may still be reported by SslStream when CustomRootTrust succeeded.
        var materialErrors = sslPolicyErrors &
                             ~(SslPolicyErrors.RemoteCertificateNameMismatch |
                               SslPolicyErrors.RemoteCertificateChainErrors);
        if (materialErrors != SslPolicyErrors.None)
        {
            error = $"Client certificate TLS policy errors: {materialErrors}.";
            return false;
        }

        identity = new ConnectorClientIdentity
        {
            Thumbprint = CertificateThumbprint.Normalize(leaf.Thumbprint ?? string.Empty),
            Subject = leaf.Subject,
            Issuer = leaf.Issuer
        };

        _logger.LogDebug(
            "Client certificate validated. Subject={Subject}, Thumbprint={Thumbprint}",
            identity.Subject,
            identity.Thumbprint);
        return true;
    }

    /// <summary>
    /// Supports Client Authentication.
    /// </summary>
    private static bool SupportsClientAuthentication(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            return true;
        }

        foreach (Oid oid in eku.EnhancedKeyUsages)
        {
            if (string.Equals(oid.Value, ClientAuthenticationOid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
