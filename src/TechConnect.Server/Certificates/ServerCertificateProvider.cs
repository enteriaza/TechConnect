// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ServerCertificateProvider.cs
//
// Purpose:
// Loads and caches the TLS server certificate used by TechConnect listeners.
//
// -----------------------------------------------------------------------------
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Certificates;

/// <summary>
/// Loads the connector TLS server certificate from the Windows store.
/// </summary>
/// <remarks>
/// Never generates, installs, removes, or renews certificates. Failures throw
/// <see cref="InvalidOperationException"/> with operator-safe messages. Private-key
/// material must not be logged.
/// </remarks>
internal sealed class ServerCertificateProvider
{
    /// <summary>
    /// Server Authentication EKU OID (1.3.6.1.5.5.7.3.1).
    /// </summary>
    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";

    /// <summary>
    /// Server options supplying connector certificate thumbprint and store settings.
    /// </summary>
    private readonly IOptions<TechConnectServerOptions> _options;

    /// <summary>
    /// Factory for opening the configured certificate store.
    /// </summary>
    private readonly ICertificateStoreFactory _storeFactory;

    /// <summary>
    /// Clock used for NotBefore/NotAfter validation.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Logger for load milestones (thumbprint prefixes only; no private keys).
    /// </summary>
    private readonly ILogger<ServerCertificateProvider> _logger;

    /// <summary>
    /// Creates a provider over options, store factory, clock, and logger.
    /// </summary>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public ServerCertificateProvider(
        IOptions<TechConnectServerOptions> options,
        ICertificateStoreFactory storeFactory,
        TimeProvider timeProvider,
        ILogger<ServerCertificateProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Loads Required Certificate from configured stores.
    /// </summary>
    public X509Certificate2 LoadRequiredCertificate()
    {
        var connector = _options.Value.Connector ?? new ConnectorListenerOptions();
        var thumbprint = CertificateThumbprint.Normalize(connector.ServerCertificateThumbprint);
        if (thumbprint.Length is not (40 or 64) || thumbprint.Any(static c => !char.IsAsciiHexDigit(c)))
        {
            throw new InvalidOperationException(
                "TechConnectServer:Connector:ServerCertificateThumbprint must identify a single TLS server certificate. " +
                "Application startup never provisions certificates.");
        }

        if (!Enum.TryParse<StoreName>(connector.ServerCertificateStoreName, ignoreCase: true, out var storeName))
        {
            throw new InvalidOperationException(
                $"TechConnectServer:Connector:ServerCertificateStoreName '{connector.ServerCertificateStoreName}' is invalid.");
        }

        var storeLocation = connector.ServerCertificateStoreLocation;
        _logger.LogInformation(
            "Loading connector TLS server certificate from {StoreLocation}\\{StoreName} (thumbprint prefix {Prefix}…)",
            storeLocation,
            storeName,
            thumbprint[..Math.Min(8, thumbprint.Length)]);

        IReadOnlyList<X509Certificate2> matches;
        try
        {
            using var store = _storeFactory.Create();
            store.Open(storeName, storeLocation);
            matches = store.FindByThumbprint(thumbprint);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Unable to open certificate store {storeLocation}\\{storeName}.",
                ex);
        }

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Connector TLS server certificate {thumbprint} was not found in {storeLocation}\\{storeName}. " +
                "This application never installs certificates.");
        }

        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple certificates match connector TLS thumbprint {thumbprint}. Correct the store configuration.");
        }

        var selected = matches[0];
        Validate(selected, connector);

        // Rematerialize via PKCS#12 so ephemeral private-key handles remain usable after the store closes.
        // Never log or persist the exported bytes beyond this load.
        var clone = RematerializeWithPrivateKey(selected);
        _logger.LogInformation(
            "Connector TLS server certificate loaded. Subject={Subject}, Thumbprint={Thumbprint}, NotAfter={NotAfter:u}",
            clone.Subject,
            CertificateThumbprint.Normalize(clone.Thumbprint ?? string.Empty),
            clone.NotAfter.ToUniversalTime());
        return clone;
    }

    /// <summary>
    /// Rematerialize With Private Key.
    /// </summary>
    private static X509Certificate2 RematerializeWithPrivateKey(X509Certificate2 selected)
    {
        try
        {
            // UserKeySet is required for SslStream server authentication on Windows Schannel.
            // Never persists into a certificate store; only materializes a private-key handle.
            var pfx = selected.Export(X509ContentType.Pfx);
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
        catch (CryptographicException)
        {
            // Fallback for store-backed keys that refuse export; retain the previous copy behaviour.
            return new X509Certificate2(selected);
        }
    }

    /// <summary>
    /// Validates .
    /// </summary>
    private void Validate(X509Certificate2 certificate, ConnectorListenerOptions options)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (certificate.NotBefore.ToUniversalTime() > now)
        {
            throw new InvalidOperationException(
                $"Connector TLS server certificate is not valid yet (NotBefore={certificate.NotBefore:u}).");
        }

        if (certificate.NotAfter.ToUniversalTime() < now)
        {
            throw new InvalidOperationException(
                $"Connector TLS server certificate has expired (NotAfter={certificate.NotAfter:u}).");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException(
                "Connector TLS server certificate does not have an accessible private key.");
        }

        if (!HasServerAuthenticationEku(certificate))
        {
            throw new InvalidOperationException(
                "Connector TLS server certificate is missing the Server Authentication EKU (1.3.6.1.5.5.7.3.1).");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedServerSubject) &&
            !SubjectsEqual(certificate.Subject, options.ExpectedServerSubject))
        {
            throw new InvalidOperationException(
                $"Connector TLS server certificate subject '{certificate.Subject}' does not match expected '{options.ExpectedServerSubject}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedServerIssuer) &&
            !SubjectsEqual(certificate.Issuer, options.ExpectedServerIssuer))
        {
            throw new InvalidOperationException(
                $"Connector TLS server certificate issuer '{certificate.Issuer}' does not match expected '{options.ExpectedServerIssuer}'.");
        }
    }

    /// <summary>
    /// Returns whether Server Authentication Eku is present.
    /// </summary>
    private static bool HasServerAuthenticationEku(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            // Require explicit Server Auth for this PKI; unconstrained certificates are rejected.
            return false;
        }

        foreach (Oid oid in eku.EnhancedKeyUsages)
        {
            if (string.Equals(oid.Value, ServerAuthenticationOid, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Subjects Equal.
    /// </summary>
    private static bool SubjectsEqual(string left, string right)
    {
        static string Normalize(string value) =>
            string.Join(",", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}
