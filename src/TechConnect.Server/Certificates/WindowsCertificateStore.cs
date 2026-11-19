// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    WindowsCertificateStore.cs
//
// Purpose:
// Windows X509Store-backed ICertificateStore and factory used to locate TLS
// certificates by normalized thumbprint.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Certificates;

/// <summary>
/// <see cref="ICertificateStore"/> implementation over <see cref="X509Store"/>.
/// </summary>
/// <remarks>
/// Not thread-safe for concurrent <see cref="Open"/>/<see cref="FindByThumbprint"/>/
/// <see cref="Dispose"/>. Open replaces any previously opened store.
/// </remarks>
internal sealed class WindowsCertificateStore : ICertificateStore
{
    /// <summary>
    /// Currently opened store, or <see langword="null"/> before open / after dispose.
    /// </summary>
    private X509Store? _store;

    /// <summary>
    /// Opens the named store read-only, disposing any previously opened store.
    /// </summary>
    /// <param name="storeName">Windows store name.</param>
    /// <param name="storeLocation">Windows store location.</param>
    public void Open(StoreName storeName, StoreLocation storeLocation)
    {
        _store?.Dispose();
        _store = new X509Store(storeName, storeLocation);
        _store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
    }

    /// <summary>
    /// Finds certificates by thumbprint, then filters with normalized comparison.
    /// </summary>
    /// <param name="normalizedThumbprint">Thumbprint already normalized via configuration helpers.</param>
    /// <returns>Matching certificates (validOnly=false on the store find).</returns>
    /// <exception cref="InvalidOperationException">The store has not been opened.</exception>
    public IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint)
    {
        if (_store is null)
        {
            throw new InvalidOperationException("Certificate store is not open.");
        }

        return _store.Certificates
            .Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => string.Equals(
                CertificateThumbprint.Normalize(c.Thumbprint ?? string.Empty),
                normalizedThumbprint,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Disposes the underlying <see cref="X509Store"/> if open.
    /// </summary>
    public void Dispose()
    {
        _store?.Dispose();
        _store = null;
    }
}

/// <summary>
/// Factory that creates <see cref="WindowsCertificateStore"/> instances.
/// </summary>
internal sealed class WindowsCertificateStoreFactory : ICertificateStoreFactory
{
    /// <summary>
    /// Creates a new undisposed <see cref="WindowsCertificateStore"/>.
    /// </summary>
    /// <returns>Store owned by the caller.</returns>
    public ICertificateStore Create() => new WindowsCertificateStore();
}
