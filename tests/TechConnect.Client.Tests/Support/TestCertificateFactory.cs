// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    TestCertificateFactory.cs
//
// Purpose:
// Ephemeral in-memory X.509 test certificate helpers and fake certificate-store
// abstractions. Never installs or removes Windows store certificates.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Creates ephemeral CA/server/client certificates for negative TLS and unit tests.
/// Certificates are materialized via PFX + EphemeralKeySet so keys are not persisted to the user store.
/// </summary>
/// <remarks>
/// Limitation: never installs trust anchors; happy-path WSS tests should use
/// <see cref="PersistentDevCertificates"/> instead.
/// </remarks>
internal static class TestCertificateFactory
{
    /// <summary>
    /// Creates a self-signed CA certificate with KeyCertSign/CrlSign usage.
    /// </summary>
    public static X509Certificate2 CreateCa(
        string commonName = "TechConnect.Client Test CA",
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
    /// Creates a server certificate signed by <paramref name="ca"/> with server EKU and SAN for the host.
    /// </summary>
    public static X509Certificate2 CreateServerCertificate(
        X509Certificate2 ca,
        string hostName = "localhost",
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null)
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
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                false));

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
        return MaterializeWithPrivateKey(withKey);
    }

    /// <summary>
    /// Creates a client (or mis-EKU) certificate, optionally CA-signed and optionally without a private key.
    /// </summary>
    public static X509Certificate2 CreateClientCertificate(
        X509Certificate2? ca = null,
        bool includeClientEku = true,
        bool includePrivateKey = true,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string commonName = "TechConnect.Client Test Client")
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
    /// Re-import via PFX so Schannel/Kestrel keep a stable private-key handle after the
    /// creating <see cref="RSA"/> instance is disposed.
    /// EphemeralKeySet avoids persisting keys into the Windows user key store (which can
    /// prompt / hang under automation when installing trust anchors).
    /// </summary>
    private static X509Certificate2 MaterializeWithPrivateKey(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        certificate.Dispose();
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
}

/// <summary>
/// In-memory <see cref="TechConnect.Client.Abstractions.ICertificateStore"/> for client-certificate provider tests.
/// </summary>
/// <remarks>
/// Limitation: ignores store name/location; FindByThumbprint requires a prior successful Open;
/// Dispose is a no-op and does not dispose held certificates.
/// </remarks>
internal sealed class FakeCertificateStore : TechConnect.Client.Abstractions.ICertificateStore
{
    /// <summary>
    /// Certificates available after Open.
    /// </summary>
    private readonly List<X509Certificate2> _certificates = new();
    /// <summary>
    /// Whether Open succeeded.
    /// </summary>
    private bool _opened;
    /// <summary>
    /// Optional exception thrown from Open.
    /// </summary>
    private Exception? _openException;

    /// <summary>
    /// Configures <see cref="Open"/> to throw the given exception.
    /// </summary>
    public void SetOpenException(Exception exception) => _openException = exception;

    /// <summary>
    /// Adds a certificate that can be found by thumbprint after Open.
    /// </summary>
    public void Add(X509Certificate2 certificate) => _certificates.Add(certificate);

    /// <inheritdoc />
    public void Open(StoreName storeName, StoreLocation storeLocation)
    {
        if (_openException is not null)
        {
            throw _openException;
        }

        _opened = true;
    }

    /// <inheritdoc />
    public IReadOnlyList<X509Certificate2> FindByThumbprint(string normalizedThumbprint)
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Store not opened.");
        }

        return _certificates
            .Where(c => string.Equals(
                TechConnect.Client.Configuration.RemoteServiceOptionsValidator.NormalizeThumbprint(c.Thumbprint ?? string.Empty),
                normalizedThumbprint,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// <see cref="TechConnect.Client.Abstractions.ICertificateStoreFactory"/> that always returns the same fake store.
/// </summary>
internal sealed class FakeCertificateStoreFactory : TechConnect.Client.Abstractions.ICertificateStoreFactory
{
    /// <summary>
    /// Store returned by Create.
    /// </summary>
    private readonly FakeCertificateStore _store;

    /// <summary>
    /// Creates a factory that returns <paramref name="store"/> from every <see cref="Create"/> call.
    /// </summary>
    public FakeCertificateStoreFactory(FakeCertificateStore store) => _store = store;

    /// <inheritdoc />
    public TechConnect.Client.Abstractions.ICertificateStore Create() => _store;
}
