using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class CertificateFactory
{
    public static X509Certificate2 CreateDevelopmentCa()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            DevCertIdentities.CaSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(DevCertIdentities.Validity);
        using var created = request.CreateSelfSigned(notBefore, notAfter);
        return PersistUserKey(created);
    }

    public static X509Certificate2 CreateServerCertificate(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            DevCertIdentities.ServerSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(DevCertIdentities.ServerAuthOid) }, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(DevCertIdentities.Validity);
        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(ca, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(rsa);
        return PersistUserKey(withKey);
    }

    public static X509Certificate2 CreateClientCertificate(X509Certificate2 ca)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            DevCertIdentities.ClientSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(DevCertIdentities.ClientAuthOid) }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.Add(DevCertIdentities.Validity);
        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(ca, notBefore, notAfter, serial);
        using var withKey = issued.CopyWithPrivateKey(rsa);
        return PersistUserKey(withKey);
    }

    public static X509Certificate2 PublicOnly(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));

    /// <summary>
    /// Materialise into a Windows user key container without Exportable so the private key
    /// is non-exportable for routine use after store installation.
    /// </summary>
    private static X509Certificate2 PersistUserKey(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            (string?)null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
    }
}
