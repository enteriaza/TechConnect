// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ICertificateStore.cs
//
// Purpose:
// Minimal Windows certificate-store seam for opening a store location and
// finding certificates by normalized thumbprint.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Minimal Windows certificate-store seam.
/// </summary>
/// <remarks>
/// <para>
/// Caller owns returned certificates' disposal policy. Production opens the
/// store read-only and does not export PKCS#12 material.
/// </para>
/// <para>
/// Dispose closes the underlying store handle. Not safe for concurrent use on
/// the same instance without external synchronization.
/// </para>
/// </remarks>
internal interface ICertificateStore : IDisposable
{
    /// <summary>
    /// Opens (or reopens) the named Windows certificate store for reading.
    /// </summary>
    /// <param name="storeName">X509 store name (for example <see cref="StoreName.My"/>).</param>
    /// <param name="storeLocation">Store location (<see cref="StoreLocation.CurrentUser"/> or LocalMachine).</param>
    void Open(StoreName storeName, StoreLocation storeLocation);

    /// <summary>
    /// Finds certificates whose thumbprint matches <paramref name="normalizedThumbprint"/>.
    /// </summary>
    /// <param name="normalizedThumbprint">
    /// Uppercase hex thumbprint with spaces/colons already removed (40 or 64 characters).
    /// </param>
    /// <returns>
    /// Matching certificates (possibly empty). Validity filtering is not applied here;
    /// callers perform EKU/time/private-key checks.
    /// </returns>
    /// <exception cref="InvalidOperationException">The store has not been opened.</exception>
    IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint);
}
