// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TestCertificateFactory.cs
//
// Purpose:
// Creates ephemeral CA/server/client X509 certificates for TLS
// connector and mutual-auth tests.
//
// -----------------------------------------------------------------------------
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Ephemeral X509 certificate factory for connector TLS tests.
/// </summary>
internal static class TestCertificateFactory
{
    /// <summary>
    /// Creates a self-signed CA with KeyCertSign/CrlSign and basic constraints CA=true.
    /// </summary>
    /// <param name="commonName">Subject CN.</param>
    /// <param name="notBefore">Not-before; defaults to UTC now minus one day.</param>
    /// <param name="notAfter">Not-after; defaults to UTC now plus two years.</param>
    /// <returns>Materialized certificate with exportable UserKeySet private key.</returns>
    public static X509Certificate2 CreateCa(
        string commonName = "TechConnect Test CA",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var selfSigned = request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddYears(2));
        return MaterializeWithPrivateKey(selfSigned);
    }
    /// <summary>
    /// Issues a server certificate from <paramref name="ca"/> with SAN DNS (and loopback IPs for localhost).
    /// </summary>
    /// <param name="ca">Issuing CA with private key.</param>
    /// <param name="hostName">CN and DNS SAN.</param>
    /// <param name="notBefore">Not-before; default UTC now minus one day.</param>
    /// <param name="notAfter">Not-after; default UTC now plus 30 days.</param>
    /// <param name="includeServerEku">
    /// When true, EKU is serverAuth; when false, clientAuth (negative-test material).
    /// </param>
    /// <param name="includePrivateKey">When false, returns public-only cert bytes.</param>
    /// <returns>Server certificate suitable for connector TLS tests.</returns>

    public static X509Certificate2 CreateServerCertificate(
        X509Certificate2 ca,
        string hostName = "localhost",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool includeServerEku = true,
        bool includePrivateKey = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));

        if (includeServerEku)
        {
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    false));
        }
        else
        {
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                    false));
        }

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        if (hostName is "localhost")
        {
            san.AddIpAddress(System.Net.IPAddress.Loopback);
            san.AddIpAddress(System.Net.IPAddress.IPv6Loopback);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(16);
        var certificate = request.Create(
            ca,
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30),
            serial);

        using var withKey = certificate.CopyWithPrivateKey(rsa);
        if (includePrivateKey)
        {
            return MaterializeWithPrivateKey(withKey);
        }

        return X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));
    }
    /// <summary>
    /// Creates a client certificate, optionally issued by <paramref name="ca"/> (else self-signed).
    /// </summary>
    /// <param name="ca">Optional issuer; null yields a self-signed client cert.</param>
    /// <param name="includeClientEku">
    /// When true, EKU is clientAuth; when false, serverAuth (negative-test material).
    /// </param>
    /// <param name="includePrivateKey">When false, returns public-only cert bytes.</param>
    /// <param name="notBefore">Not-before; default UTC now minus one day.</param>
    /// <param name="notAfter">Not-after; default UTC now plus 30 days.</param>
    /// <param name="commonName">Subject CN.</param>
    /// <returns>Client certificate for mutual-TLS connector tests.</returns>

    public static X509Certificate2 CreateClientCertificate(
        X509Certificate2? ca = null,
        bool includeClientEku = true,
        bool includePrivateKey = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string commonName = "TechConnect Test Client")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

        if (includeClientEku)
        {
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
                    false));
        }
        else
        {
            request.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    false));
        }

        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        X509Certificate2 certificate;
        if (ca is null)
        {
            certificate = request.CreateSelfSigned(
                notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
        }
        else
        {
            var serial = RandomNumberGenerator.GetBytes(16);
            certificate = request.Create(
                ca,
                notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
                notAfter ?? DateTimeOffset.UtcNow.AddDays(30),
                serial);
            certificate = certificate.CopyWithPrivateKey(rsa);
        }

        if (includePrivateKey)
        {
            return MaterializeWithPrivateKey(certificate);
        }

        return X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
    }
    /// <summary>
    /// Returns the normalized thumbprint string used in TechConnect configuration.
    /// </summary>
    /// <param name="certificate">Certificate whose thumbprint to normalize.</param>
    /// <returns>Value from <see cref="CertificateThumbprint.Normalize"/>.</returns>

    public static string Thumbprint(X509Certificate2 certificate) =>
        CertificateThumbprint.Normalize(certificate.Thumbprint ?? string.Empty);
    /// <summary>
    /// Re-imports a PFX with Exportable|UserKeySet so Schannel SslStream accepts the private key.
    /// </summary>
    /// <param name="certificate">Certificate with private key (disposed after export).</param>
    /// <returns>Reloaded certificate; keys are not installed into a Windows store.</returns>

    private static X509Certificate2 MaterializeWithPrivateKey(X509Certificate2 certificate)
    {
        // UserKeySet is required for SslStream AuthenticateAsServer on Windows Schannel.
        // EphemeralKeySet is rejected with "platform does not support ephemeral keys".
        // These keys are never installed into a certificate store.
        var pfx = certificate.Export(X509ContentType.Pfx);
        certificate.Dispose();
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
