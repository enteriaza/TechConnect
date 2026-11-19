// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ClientCertificateProvider.cs
//
// Purpose:
// Resolves and validates the configured Windows client certificate for mTLS.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Services;

/// <summary>
/// Looks up the configured client certificate by thumbprint in a Windows store.
/// </summary>
/// <remarks>
/// <para>
/// When no thumbprint is configured, <see cref="GetClientCertificate"/> returns
/// <see langword="null"/>. The production TCP/mTLS transport
/// (<c>TcpMtlsRemoteConsoleTransport</c>) requires a non-null client certificate and
/// rejects a null result with <see cref="RemoteTransportConfigurationException"/> —
/// mutual TLS is mandatory. Returning null only signals “no thumbprint configured”;
/// it is not an accept-without-client-certificate path for TechConnect.Server.
/// </para>
/// <para>
/// When a thumbprint is configured, lookup failures and validation failures throw
/// <see cref="RemoteTransportConfigurationException"/> (permanent for reconnect).
/// </para>
/// <para>
/// Returns a duplicated <see cref="X509Certificate2"/> so the store can close without
/// invalidating the private-key handle. Callers own disposal of the returned instance.
/// Does not export PKCS#12 / private-key bytes. Not intended for concurrent use of a
/// single returned certificate instance across threads without external synchronization.
/// </para>
/// </remarks>
internal sealed class ClientCertificateProvider : IClientCertificateProvider
{
    /// <summary>OID for id-kp-clientAuth (client authentication EKU).</summary>
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Remote options containing store location/name and thumbprint.</summary>
    private readonly IOptions<RemoteServiceOptions> _options;

    /// <summary>Structured logger (thumbprint prefixes only; never private key material).</summary>
    private readonly ILogger<ClientCertificateProvider> _logger;

    /// <summary>Clock used for not-before / not-after / expiry-warning checks.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Factory for disposable Windows store adapters.</summary>
    private readonly ICertificateStoreFactory _storeFactory;

    /// <summary>
    /// Initializes the provider with options, clock, logger, and store factory.
    /// </summary>
    public ClientCertificateProvider(
        IOptions<RemoteServiceOptions> options,
        TimeProvider timeProvider,
        ILogger<ClientCertificateProvider> logger,
        ICertificateStoreFactory storeFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    }

    /// <inheritdoc />
    /// <remarks>
    /// True when a non-whitespace thumbprint is present in options — not whether a
    /// matching certificate currently exists in the store.
    /// </remarks>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Value.ClientCertificate?.Thumbprint);

    /// <inheritdoc />
    /// <remarks>
    /// Opens the configured store, finds by normalized thumbprint, validates time window,
    /// private key, clientAuth EKU (or unconstrained), and optional expected subject/issuer.
    /// Multiple matches are treated as configuration errors.
    /// </remarks>
    public X509Certificate2? GetClientCertificate()
    {
        var certificateOptions = _options.Value.ClientCertificate ?? new ClientCertificateOptions();
        if (string.IsNullOrWhiteSpace(certificateOptions.Thumbprint))
        {
            _logger.LogInformation("No client certificate thumbprint configured");
            return null;
        }

        var thumbprint = RemoteServiceOptionsValidator.NormalizeThumbprint(certificateOptions.Thumbprint);
        if (thumbprint.Length is not (40 or 64) ||
            thumbprint.Any(static c => !char.IsAsciiHexDigit(c)))
        {
            throw new RemoteTransportConfigurationException(
                "The configured client certificate thumbprint is invalid.");
        }

        if (!Enum.TryParse<StoreName>(certificateOptions.StoreName, ignoreCase: true, out var storeName))
        {
            throw new RemoteTransportConfigurationException(
                $"Client certificate store name '{certificateOptions.StoreName}' is invalid.");
        }

        var storeLocation = certificateOptions.StoreLocation;
        _logger.LogInformation(
            "Looking up client certificate in {StoreLocation}\\{StoreName} by thumbprint {ThumbprintPrefix}…",
            storeLocation,
            storeName,
            thumbprint[..Math.Min(8, thumbprint.Length)]);

        IReadOnlyList<X509Certificate2> candidates;
        try
        {
            using var store = _storeFactory.Create();
            store.Open(storeName, storeLocation);
            candidates = store.FindByThumbprint(thumbprint);
        }
        catch (RemoteTransportConfigurationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or ArgumentException)
        {
            _logger.LogError(ex, "Unable to open certificate store {StoreLocation}\\{StoreName}", storeLocation, storeName);
            throw new RemoteTransportConfigurationException(
                "Unable to open the configured Windows certificate store.",
                ex);
        }

        if (candidates.Count == 0)
        {
            throw new RemoteTransportConfigurationException(
                "The configured client certificate thumbprint was not found in the Windows certificate store.");
        }

        if (candidates.Count > 1)
        {
            throw new RemoteTransportConfigurationException(
                "Multiple certificates match the configured thumbprint. Correct the certificate store configuration.");
        }

        var selected = candidates[0];
        ValidateCertificate(selected);
        ValidateExpectedIdentity(selected, certificateOptions);

        // Duplicate so the store can close without invalidating the private-key handle.
        // Do not export PKCS#12 (avoids copying private-key material into a byte[]).
        var owned = new X509Certificate2(selected);

        _logger.LogInformation(
            "Selected client certificate Subject={Subject} Issuer={Issuer} ThumbprintPrefix={ThumbprintPrefix}… NotBefore={NotBefore:u} NotAfter={NotAfter:u} HasPrivateKey={HasPrivateKey} Store={StoreLocation}\\{StoreName}",
            owned.Subject,
            owned.Issuer,
            thumbprint[..Math.Min(8, thumbprint.Length)],
            owned.NotBefore.ToUniversalTime(),
            owned.NotAfter.ToUniversalTime(),
            owned.HasPrivateKey,
            storeLocation,
            storeName);

        var daysRemaining = (owned.NotAfter.ToUniversalTime() - _timeProvider.GetUtcNow().UtcDateTime).TotalDays;
        if (daysRemaining is > 0 and <= 30)
        {
            _logger.LogWarning(
                "Client certificate expires in {DaysRemaining:0} day(s)",
                daysRemaining);
        }

        return owned;
    }

    /// <summary>
    /// Ensures the certificate is currently time-valid, has a private key, and supports client auth.
    /// </summary>
    /// <param name="certificate">Candidate from the store (not yet duplicated).</param>
    /// <exception cref="RemoteTransportConfigurationException">Validation failed.</exception>
    private void ValidateCertificate(X509Certificate2 certificate)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now < certificate.NotBefore.ToUniversalTime())
        {
            throw new RemoteTransportConfigurationException(
                "The configured client certificate is not valid yet.");
        }

        if (now > certificate.NotAfter.ToUniversalTime())
        {
            throw new RemoteTransportConfigurationException(
                "The configured client certificate has expired.");
        }

        if (!certificate.HasPrivateKey)
        {
            throw new RemoteTransportConfigurationException(
                "The configured client certificate does not have an associated private key.");
        }

        if (!SupportsClientAuthentication(certificate))
        {
            throw new RemoteTransportConfigurationException(
                "The configured certificate is not suitable for client authentication.");
        }

        _logger.LogInformation(
            "Client certificate EKU validation succeeded (clientAuth present or unconstrained)");
    }

    /// <summary>
    /// Optionally enforces configured expected subject and/or issuer DN strings.
    /// </summary>
    /// <param name="certificate">Selected certificate.</param>
    /// <param name="options">Client certificate options with optional expected identity.</param>
    /// <exception cref="RemoteTransportConfigurationException">Identity mismatch.</exception>
    private static void ValidateExpectedIdentity(X509Certificate2 certificate, ClientCertificateOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ExpectedSubject) &&
            !SubjectsEqual(certificate.Subject, options.ExpectedSubject))
        {
            throw new RemoteTransportConfigurationException(
                $"The configured client certificate subject '{certificate.Subject}' does not match the expected subject '{options.ExpectedSubject}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.ExpectedIssuer) &&
            !SubjectsEqual(certificate.Issuer, options.ExpectedIssuer))
        {
            throw new RemoteTransportConfigurationException(
                $"The configured client certificate issuer '{certificate.Issuer}' does not match the expected issuer '{options.ExpectedIssuer}'.");
        }
    }

    /// <summary>
    /// Compares distinguished names after trimming RDN commas/whitespace (ordinal ignore-case).
    /// </summary>
    private static bool SubjectsEqual(string left, string right)
    {
        static string Normalize(string value) =>
            string.Join(",", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when EKU is absent (unconstrained) or includes clientAuth.
    /// </summary>
    /// <param name="certificate">Certificate to inspect.</param>
    /// <returns><see langword="true"/> when usable as a TLS client certificate by EKU rules used here.</returns>
    private static bool SupportsClientAuthentication(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            // No EKU extension: typically usable as a client certificate.
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
