// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    IClientCertificateProvider.cs
//
// Purpose:
// Loads a client certificate from the Windows certificate store for mutual TLS
// without exposing or logging private-key material.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Loads a client certificate from the Windows certificate store for mTLS.
/// </summary>
/// <remarks>
/// Never returns or logs private key material as exportable bytes. When a certificate
/// is returned, the caller owns disposal. Implementations typically duplicate the
/// store certificate so the store can close without invalidating the private-key handle.
/// </remarks>
public interface IClientCertificateProvider
{
    /// <summary>
    /// Whether a client-certificate thumbprint is configured.
    /// </summary>
    /// <value>
    /// <see langword="true"/> when <c>RemoteService:ClientCertificate:Thumbprint</c>
    /// is non-empty; otherwise <see langword="false"/>.
    /// </value>
    bool IsConfigured { get; }

    /// <summary>
    /// Loads the configured client certificate.
    /// </summary>
    /// <returns>
    /// An owned <see cref="X509Certificate2"/> when a thumbprint is configured and valid,
    /// or <see langword="null"/> when no thumbprint is configured.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A null return means no thumbprint is configured. TechConnect.Client’s TCP/mTLS
    /// transport requires a client certificate for mutual authentication and will reject
    /// null with a configuration error — mTLS is mandatory for the remote console path.
    /// </para>
    /// <para>
    /// Throws when a thumbprint is configured but the certificate cannot be used
    /// (missing, ambiguous, expired, lacking a private key, failing EKU/subject/issuer checks,
    /// or store open failures). Does not log private keys or full secrets.
    /// </para>
    /// </remarks>
    /// <exception cref="TechConnect.Client.Transport.RemoteTransportConfigurationException">
    /// A thumbprint is configured but the certificate cannot be loaded or validated.
    /// </exception>
    X509Certificate2? GetClientCertificate();
}
