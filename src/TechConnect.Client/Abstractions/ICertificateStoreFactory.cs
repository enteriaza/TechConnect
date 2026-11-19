// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ICertificateStoreFactory.cs
//
// Purpose:
// Creates disposable ICertificateStore instances for Windows certificate lookup
// without coupling callers to X509Store construction.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Creates <see cref="ICertificateStore"/> instances for certificate lookup.
/// </summary>
/// <remarks>
/// Each call returns a new store wrapper. The caller owns disposal of the returned instance.
/// </remarks>
internal interface ICertificateStoreFactory
{
    /// <summary>
    /// Creates a new, unopened certificate-store seam.
    /// </summary>
    /// <returns>A disposable store; caller must dispose after use.</returns>
    ICertificateStore Create();
}
