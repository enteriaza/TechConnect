// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FakeCertificateStore.cs
//
// Purpose:
// In-memory certificate store/factory/trust-anchor fakes replacing
// Windows store dependencies in connector tests.
//
// -----------------------------------------------------------------------------
using System.Security.Cryptography.X509Certificates;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// In-memory <see cref="ICertificateStore"/> fake (no Windows certificate store).
/// </summary>
/// <remarks>
/// Must call <see cref="Open"/> before <see cref="FindByThumbprint"/>. Dispose is a no-op.
/// </remarks>
internal sealed class FakeCertificateStore : ICertificateStore
{
    /// <summary>Certificates available for thumbprint lookup.</summary>
    private readonly List<X509Certificate2> _certificates = [];

    /// <summary>Whether <see cref="Open"/> has been called.</summary>
    private bool _opened;

    /// <summary>
    /// Adds a certificate to the in-memory store.
    /// </summary>
    /// <param name="certificate">Certificate to add.</param>
    public void Add(X509Certificate2 certificate) => _certificates.Add(certificate);

    /// <summary>
    /// Marks the store as opened (store name/location are ignored).
    /// </summary>
    /// <param name="storeName">Ignored store name.</param>
    /// <param name="storeLocation">Ignored store location.</param>
    public void Open(StoreName storeName, StoreLocation storeLocation) => _opened = true;

    /// <summary>
    /// Finds certificates matching the normalized thumbprint.
    /// </summary>
    /// <param name="normalizedThumbprint">Normalized hex thumbprint.</param>
    /// <returns>Matching certificates.</returns>
    /// <exception cref="InvalidOperationException">Store was not opened.</exception>
    public IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint)
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Store not opened.");
        }

        return _certificates
            .Where(c => string.Equals(
                CertificateThumbprint.Normalize(c.Thumbprint ?? string.Empty),
                normalizedThumbprint,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// No-op dispose (certificates are not owned/disposed by this fake).
    /// </summary>
    public void Dispose()
    {
    }
}

/// <summary>
/// <see cref="ICertificateStoreFactory"/> that always returns the same <see cref="FakeCertificateStore"/>.
/// </summary>
internal sealed class FakeCertificateStoreFactory : ICertificateStoreFactory
{
    /// <summary>Shared fake store instance.</summary>
    private readonly FakeCertificateStore _store;

    /// <summary>
    /// Initializes the factory around a shared fake store.
    /// </summary>
    /// <param name="store">Store returned by <see cref="Create"/>.</param>
    public FakeCertificateStoreFactory(FakeCertificateStore store) => _store = store;

    /// <summary>
    /// Returns the shared fake store.
    /// </summary>
    /// <returns>The configured <see cref="FakeCertificateStore"/>.</returns>
    public ICertificateStore Create() => _store;
}

/// <summary>
/// In-memory trust-anchor provider matching allowed CA thumbprints to configured anchors.
/// </summary>
internal sealed class FakeTrustAnchorProvider : IClientCertificateTrustAnchorProvider
{
    /// <summary>Configured trust anchors.</summary>
    private readonly X509Certificate2Collection _anchors;

    /// <summary>
    /// Initializes the provider with the given anchors.
    /// </summary>
    /// <param name="anchors">CA certificates available for matching.</param>
    public FakeTrustAnchorProvider(params X509Certificate2[] anchors)
    {
        _anchors = [];
        foreach (var anchor in anchors)
        {
            _anchors.Add(anchor);
        }
    }

    /// <summary>
    /// Resolves anchors whose thumbprints appear in <paramref name="allowedAuthorityThumbprints"/>.
    /// </summary>
    /// <param name="allowedAuthorityThumbprints">Allowed CA thumbprints from options.</param>
    /// <returns>Matching anchors; empty when the allow-list is empty.</returns>
    /// <exception cref="InvalidOperationException">An allow-list thumbprint did not match any anchor.</exception>
    public X509Certificate2Collection Resolve(IReadOnlyList<string> allowedAuthorityThumbprints)
    {
        if (allowedAuthorityThumbprints.Count == 0)
        {
            return [];
        }

        var result = new X509Certificate2Collection();
        foreach (var raw in allowedAuthorityThumbprints)
        {
            var thumbprint = CertificateThumbprint.Normalize(raw);
            foreach (var anchor in _anchors)
            {
                if (string.Equals(
                        CertificateThumbprint.Normalize(anchor.Thumbprint ?? string.Empty),
                        thumbprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(anchor);
                }
            }
        }

        if (result.Count == 0)
        {
            throw new InvalidOperationException("Allowed certificate authority thumbprint was not found.");
        }

        return result;
    }
}
