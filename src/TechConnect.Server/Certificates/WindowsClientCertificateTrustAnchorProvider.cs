// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    WindowsClientCertificateTrustAnchorProvider.cs
//
// Purpose:
// Resolves configured CA thumbprints to trust-anchor certificates from Windows
// certificate stores without installing anchors.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Certificates;

// CertificateThumbprint lives in Configuration.

/// <summary>
/// Windows store-backed <see cref="IClientCertificateTrustAnchorProvider"/>.
/// </summary>
/// <remarks>
/// Searches a fixed set of CurrentUser/LocalMachine stores. Missing thumbprints
/// throw <see cref="InvalidOperationException"/> — this application never installs
/// trust anchors. Ambiguous matches (multiple certs for one thumbprint) also throw.
/// </remarks>
internal sealed class WindowsClientCertificateTrustAnchorProvider : IClientCertificateTrustAnchorProvider
{
    /// <summary>
    /// Candidate stores searched in order for each allowed CA thumbprint.
    /// </summary>
    private static readonly (StoreName Name, StoreLocation Location)[] CandidateStores =
    [
        (StoreName.Root, StoreLocation.CurrentUser),
        (StoreName.CertificateAuthority, StoreLocation.CurrentUser),
        (StoreName.Root, StoreLocation.LocalMachine),
        (StoreName.CertificateAuthority, StoreLocation.LocalMachine),
        (StoreName.My, StoreLocation.CurrentUser)
    ];

    /// <summary>
    /// Factory used to open short-lived store instances during resolution.
    /// </summary>
    private readonly ICertificateStoreFactory _storeFactory;

    /// <summary>
    /// Logger for non-fatal store search failures (debug).
    /// </summary>
    private readonly ILogger<WindowsClientCertificateTrustAnchorProvider> _logger;

    /// <summary>
    /// Creates a provider over the given store factory.
    /// </summary>
    /// <param name="storeFactory">Factory for per-search store instances.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException">A dependency is <see langword="null"/>.</exception>
    public WindowsClientCertificateTrustAnchorProvider(
        ICertificateStoreFactory storeFactory,
        ILogger<WindowsClientCertificateTrustAnchorProvider> logger)
    {
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolves each allowed CA thumbprint to exactly one certificate from candidate stores.
    /// </summary>
    /// <param name="allowedAuthorityThumbprints">Configured CA thumbprints (raw or normalized).</param>
    /// <returns>
    /// Collection of trust anchors; empty when <paramref name="allowedAuthorityThumbprints"/> is empty.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// A thumbprint is missing from all stores, or multiple certificates match in one store.
    /// </exception>
    public X509Certificate2Collection Resolve(IReadOnlyList<string> allowedAuthorityThumbprints)
    {
        var result = new X509Certificate2Collection();
        if (allowedAuthorityThumbprints.Count == 0)
        {
            return result;
        }

        foreach (var raw in allowedAuthorityThumbprints)
        {
            var thumbprint = CertificateThumbprint.Normalize(raw);
            if (thumbprint.Length is not (40 or 64))
            {
                continue;
            }

            X509Certificate2? found = null;
            foreach (var (storeName, location) in CandidateStores)
            {
                try
                {
                    using var store = _storeFactory.Create();
                    store.Open(storeName, location);
                    var matches = store.FindByThumbprint(thumbprint);
                    if (matches.Count == 1)
                    {
                        found = new X509Certificate2(matches[0]);
                        break;
                    }

                    if (matches.Count > 1)
                    {
                        throw new InvalidOperationException(
                            $"Multiple certificates match allowed CA thumbprint {thumbprint} in {location}\\{storeName}.");
                    }
                }
                catch (InvalidOperationException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Unable to search {StoreLocation}\\{StoreName} for allowed CA {ThumbprintPrefix}…",
                        location,
                        storeName,
                        thumbprint[..Math.Min(8, thumbprint.Length)]);
                }
            }

            if (found is null)
            {
                throw new InvalidOperationException(
                    $"Allowed certificate authority thumbprint {thumbprint} was not found in the Windows certificate stores. " +
                    "This application never installs trust anchors.");
            }

            result.Add(found);
        }

        return result;
    }
}
