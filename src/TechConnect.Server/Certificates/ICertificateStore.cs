// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ICertificateStore.cs
//
// Purpose:
// Abstractions for opening a platform certificate store and locating
// certificates by thumbprint for TLS server certificate loading.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Server.Certificates;

/// <summary>
/// Thin wrapper over a platform certificate store used to locate server certificates.
/// </summary>
/// <remarks>
/// Callers must <see cref="Open"/> before <see cref="FindByThumbprint"/> and dispose
/// the instance when finished. Implementations are not assumed thread-safe for
/// concurrent open/find/dispose.
/// </remarks>
internal interface ICertificateStore : IDisposable
{
    /// <summary>
    /// Opens the named store at the given location.
    /// </summary>
    /// <param name="storeName">Store name (for example My).</param>
    /// <param name="storeLocation">Store location (CurrentUser or LocalMachine).</param>
    void Open(StoreName storeName, StoreLocation storeLocation);

    /// <summary>
    /// Finds certificates whose thumbprint matches <paramref name="normalizedThumbprint"/>.
    /// </summary>
    /// <param name="normalizedThumbprint">Thumbprint string in the store's expected normalized form.</param>
    /// <returns>Matching certificates; empty when none found.</returns>
    IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint);
}

/// <summary>
/// Factory that creates <see cref="ICertificateStore"/> instances.
/// </summary>
internal interface ICertificateStoreFactory
{
    /// <summary>
    /// Creates a new undisposed certificate store wrapper.
    /// </summary>
    /// <returns>A store that the caller must dispose.</returns>
    ICertificateStore Create();
}
