// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IClientCertificateTrustAnchorProvider.cs
//
// Purpose:
// Abstraction for resolving optional custom trust anchors used when validating
// connector client certificates.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Server.Certificates;

/// <summary>
/// Resolves optional custom trust anchors for connector client-certificate validation.
/// </summary>
/// <remarks>
/// When no allowed CA thumbprints are configured, system trust is used instead
/// (callers skip this provider). Implementations should not log private key material.
/// </remarks>
internal interface IClientCertificateTrustAnchorProvider
{
    /// <summary>
    /// Resolves trust-anchor certificates for the supplied allowed CA thumbprints.
    /// </summary>
    /// <param name="allowedAuthorityThumbprints">Configured CA thumbprints to load.</param>
    /// <returns>Collection of trust-anchor certificates (may be empty if none resolve).</returns>
    X509Certificate2Collection Resolve(IReadOnlyList<string> allowedAuthorityThumbprints);
}
