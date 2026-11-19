// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    WindowsCertificateStore.cs
//
// Purpose:
// Read-only Windows X509Store adapter used for client-certificate thumbprint lookup.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;

namespace TechConnect.Client.Services;

/// <summary>
/// Production <see cref="ICertificateStore"/> over <see cref="X509Store"/>.
/// </summary>
/// <remarks>
/// <para>
/// Opens the named Windows store read-only and finds certificates by normalized
/// thumbprint. Does not validate suitability for mTLS; that is performed by
/// <see cref="ClientCertificateProvider"/> after selection.
/// </para>
/// <para>
/// Not thread-safe for concurrent open/find/dispose on the same instance.
/// Callers should treat each instance as single-owner and dispose promptly so
/// store handles are released.
/// </para>
/// </remarks>
internal sealed class WindowsCertificateStore : ICertificateStore
{
    /// <summary>
    /// Currently opened store, or <see langword="null"/> before <see cref="Open"/> / after dispose.
    /// </summary>
    private X509Store? _store;

    /// <summary>
    /// Opens (or re-opens) the specified Windows certificate store for read-only queries.
    /// </summary>
    /// <param name="storeName">Logical store name (for example My).</param>
    /// <param name="storeLocation">Store location (CurrentUser or LocalMachine).</param>
    /// <remarks>
    /// Uses <see cref="OpenFlags.ReadOnly"/> and <see cref="OpenFlags.OpenExistingOnly"/>.
    /// A previous open handle is closed before opening the new store.
    /// </remarks>
    public void Open(StoreName storeName, StoreLocation storeLocation)
    {
        _store?.Close();
        _store = new X509Store(storeName, storeLocation);
        _store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
    }

    /// <summary>
    /// Finds certificates whose thumbprint matches <paramref name="normalizedThumbprint"/>.
    /// </summary>
    /// <param name="normalizedThumbprint">
    /// Thumbprint already normalized (hex, no separators) by
    /// <see cref="RemoteServiceOptionsValidator.NormalizeThumbprint"/>.
    /// </param>
    /// <returns>
    /// Matching certificates still attached to the open store. The store must remain
    /// open until callers finish duplicating any certificate they intend to keep.
    /// </returns>
    /// <exception cref="InvalidOperationException">The store has not been opened.</exception>
    /// <remarks>
    /// Uses <c>validOnly: false</c> so expired/not-yet-valid certs can still be found and
    /// rejected with explicit errors by <see cref="ClientCertificateProvider"/>.
    /// Results are filtered again with normalized thumbprint comparison for consistency.
    /// </remarks>
    public IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint)
    {
        if (_store is null)
        {
            throw new InvalidOperationException("Certificate store has not been opened.");
        }

        var matches = _store.Certificates.Find(X509FindType.FindByThumbprint, normalizedThumbprint, validOnly: false);
        return matches
            .OfType<X509Certificate2>()
            .Where(cert => string.Equals(
                RemoteServiceOptionsValidator.NormalizeThumbprint(cert.Thumbprint ?? string.Empty),
                normalizedThumbprint,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <summary>
    /// Closes and disposes the underlying <see cref="X509Store"/> if open.
    /// </summary>
    public void Dispose()
    {
        _store?.Close();
        _store?.Dispose();
        _store = null;
    }
}
