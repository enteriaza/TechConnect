using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class RenewCommand
{
    public static int Run(bool assumeYes)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        var registry = DevCertRegistry.TryLoad(RepoPaths.RegistryPath(repoRoot));
        if (registry is null ||
            string.IsNullOrWhiteSpace(registry.CaThumbprint))
        {
            Console.Error.WriteLine("No existing development CA registry. Run 'provision' first.");
            return 1;
        }

        using var ca = CertificateStoreAccess.FindByThumbprint(
            StoreName.My,
            StoreLocation.CurrentUser,
            registry.CaThumbprint,
            requirePrivateKey: true);

        var caReason = "not found";
        if (ca is null ||
            !CertificateInspector.MatchesExpectedIdentity(
                ca,
                DevCertIdentities.CaSubject,
                DevCertIdentities.CaSubject,
                requiredEkuOid: null,
                requirePrivateKey: true,
                requireCaBasicConstraints: true,
                out caReason))
        {
            Console.Error.WriteLine($"Cannot renew: development CA is missing or invalid ({caReason}).");
            return 2;
        }

        Console.WriteLine("Renew will issue NEW server and client leaf certificates.");
        Console.WriteLine("Previous leaf certificates will NOT be removed.");
        Console.WriteLine("The development CA will NOT be replaced.");
        Console.WriteLine();
        Console.WriteLine($"Existing server thumbprint: {registry.ServerThumbprint}");
        Console.WriteLine($"Existing client thumbprint: {registry.ClientThumbprint}");
        Console.WriteLine();

        if (!assumeYes)
        {
            Console.Write("Type YES to issue replacement leaves: ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer?.Trim(), "YES", StringComparison.Ordinal))
            {
                Console.WriteLine("Aborted. No certificates were created or removed.");
                return 3;
            }
        }

        var server = CertificateFactory.CreateServerCertificate(ca);
        var client = CertificateFactory.CreateClientCertificate(ca);
        try
        {
            CertificateStoreAccess.Install(StoreName.My, StoreLocation.CurrentUser, server);
            CertificateStoreAccess.Install(StoreName.My, StoreLocation.CurrentUser, client);

            var previousServer = registry.ServerThumbprint;
            var previousClient = registry.ClientThumbprint;
            registry.ServerThumbprint = CertificateStoreAccess.NormalizeThumbprint(server.Thumbprint);
            registry.ClientThumbprint = CertificateStoreAccess.NormalizeThumbprint(client.Thumbprint);
            ConfigWriter.WriteLocalConfigs(repoRoot, registry);

            Console.WriteLine();
            Console.WriteLine($"New server thumbprint: {registry.ServerThumbprint} (previous {previousServer} retained in store)");
            Console.WriteLine($"New client thumbprint: {registry.ClientThumbprint} (previous {previousClient} retained in store)");
            Console.WriteLine("Local configuration files were updated to the new thumbprints.");
            Console.WriteLine("No certificates were removed.");
            return 0;
        }
        finally
        {
            server.Dispose();
            client.Dispose();
        }
    }
}
