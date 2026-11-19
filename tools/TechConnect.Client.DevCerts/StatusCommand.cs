using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class StatusCommand
{
    public static int Run()
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var registry = DevCertRegistry.TryLoad(RepoPaths.RegistryPath(repoRoot));

        Console.WriteLine("TechConnect.Client development certificate status (read-only)");
        Console.WriteLine($"Repository: {repoRoot}");
        Console.WriteLine($"Registry: {(registry is null ? "(missing — run provision)" : RepoPaths.RegistryPath(repoRoot))}");
        Console.WriteLine();

        if (registry is null)
        {
            Console.WriteLine("No local registry file. Certificates may still exist in the Windows store,");
            Console.WriteLine("but thumbprint-based identity matching requires provisioning.");
            return 1;
        }

        Report(
            "CA private",
            StoreName.My,
            registry.CaThumbprint,
            DevCertIdentities.CaSubject,
            DevCertIdentities.CaSubject,
            requiredEku: null,
            requirePrivateKey: true,
            requireCa: true);

        Report(
            "CA trust",
            StoreName.Root,
            registry.CaThumbprint,
            DevCertIdentities.CaSubject,
            DevCertIdentities.CaSubject,
            requiredEku: null,
            requirePrivateKey: false,
            requireCa: true);

        Report(
            "TCP/mTLS server",
            StoreName.My,
            registry.ServerThumbprint,
            DevCertIdentities.ServerSubject,
            DevCertIdentities.CaSubject,
            requiredEku: DevCertIdentities.ServerAuthOid,
            requirePrivateKey: true,
            requireCa: false);

        Report(
            "Client",
            StoreName.My,
            registry.ClientThumbprint,
            DevCertIdentities.ClientSubject,
            DevCertIdentities.CaSubject,
            requiredEku: DevCertIdentities.ClientAuthOid,
            requirePrivateKey: true,
            requireCa: false);

        Console.WriteLine();
        Console.WriteLine("This command performed no certificate-store modifications.");
        return 0;
    }

    private static void Report(
        string label,
        StoreName storeName,
        string thumbprint,
        string expectedSubject,
        string expectedIssuer,
        string? requiredEku,
        bool requirePrivateKey,
        bool requireCa)
    {
        Console.WriteLine($"[{label}] CurrentUser\\{storeName}");
        Console.WriteLine($"  Expected subject : {expectedSubject}");
        Console.WriteLine($"  Expected issuer  : {expectedIssuer}");
        Console.WriteLine($"  Registry thumb   : {thumbprint}");

        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            Console.WriteLine("  Status           : not recorded");
            Console.WriteLine();
            return;
        }

        using var cert = CertificateStoreAccess.FindByThumbprint(
            storeName,
            StoreLocation.CurrentUser,
            thumbprint,
            requirePrivateKey);

        if (cert is null)
        {
            Console.WriteLine("  Status           : NOT FOUND");
            Console.WriteLine();
            return;
        }

        var ok = CertificateInspector.MatchesExpectedIdentity(
            cert,
            expectedSubject,
            expectedIssuer,
            requiredEku,
            requirePrivateKey,
            requireCa,
            out var reason);

        Console.WriteLine($"  Found subject    : {cert.Subject}");
        Console.WriteLine($"  Found issuer     : {cert.Issuer}");
        Console.WriteLine($"  Thumbprint       : {CertificateStoreAccess.NormalizeThumbprint(cert.Thumbprint)}");
        Console.WriteLine($"  NotBefore        : {cert.NotBefore.ToUniversalTime():u}");
        Console.WriteLine($"  NotAfter         : {cert.NotAfter.ToUniversalTime():u}");
        Console.WriteLine($"  HasPrivateKey    : {cert.HasPrivateKey}");
        Console.WriteLine($"  EKUs             : {CertificateInspector.DescribeEkus(cert)}");
        Console.WriteLine($"  SANs             : {CertificateInspector.DescribeSans(cert)}");
        Console.WriteLine($"  Identity match   : {(ok ? "yes" : "NO — " + reason)}");
        Console.WriteLine();
    }
}
