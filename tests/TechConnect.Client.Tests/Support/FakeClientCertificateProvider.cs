// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    FakeClientCertificateProvider.cs
//
// Purpose:
// In-memory IClientCertificateProvider for transport tests that avoid the
// Windows certificate store. Never installs or removes certificates.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// In-memory client certificate provider for transport tests that do not need the Windows store.
/// Never installs or removes certificates.
/// </summary>
/// <remarks>
/// Limitation: holds at most one certificate; <see cref="GetClientCertificate"/> returns a
/// disposable duplicate of that in-memory cert only (no store lookup, renewal, or thumbprint search).
/// </remarks>
internal sealed class FakeClientCertificateProvider : IClientCertificateProvider, IDisposable
{
    /// <summary>
    /// Held in-memory client certificate, if any.
    /// </summary>
    private X509Certificate2? _certificate;

    /// <summary>
    /// Test-controlled flag indicating whether a client certificate is considered configured.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Replaces the held certificate with a copy of <paramref name="certificate"/>, disposing any previous one.
    /// </summary>
    /// <param name="certificate">Certificate to hold, or null to clear.</param>
    public void SetCertificate(X509Certificate2? certificate)
    {
        _certificate?.Dispose();
        _certificate = certificate is null ? null : new X509Certificate2(certificate);
    }

    /// <summary>
    /// Returns an owned duplicate of the held certificate so the transport can dispose it safely,
    /// or null when none is set.
    /// </summary>
    public X509Certificate2? GetClientCertificate()
    {
        if (_certificate is null)
        {
            return null;
        }

        // Return an owned duplicate so the transport can dispose safely.
        return new X509Certificate2(_certificate);
    }

    /// <summary>
    /// Disposes the held certificate and clears state.
    /// </summary>
    public void Dispose()
    {
        _certificate?.Dispose();
        _certificate = null;
    }
}
