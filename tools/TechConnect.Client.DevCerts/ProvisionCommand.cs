using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class ProvisionCommand
{
    public static int Run(bool confirmTrust, bool assumeYes)
    {
        var repoRoot = RepoPaths.FindRepoRoot();
        Console.WriteLine($"Repository: {repoRoot}");
        Console.WriteLine();

        var registry = DevCertRegistry.TryLoad(RepoPaths.RegistryPath(repoRoot)) ?? new DevCertRegistry();
        var changes = new List<string>();
        var created = new List<string>();

        X509Certificate2? caWithKey = null;
        try
        {
            caWithKey = TryLoadValidCa(registry, out var caStatus);
            Console.WriteLine($"CA (CurrentUser\\My): {caStatus}");

            if (caWithKey is null)
            {
                if (!string.IsNullOrWhiteSpace(registry.CaThumbprint))
                {
                    Console.Error.WriteLine(
                        "Recorded CA thumbprint is present in the registry but the certificate is missing or invalid. " +
                        "Refusing to silently rotate. Use 'renew' if replacement is intentional.");
                    return 2;
                }

                Console.WriteLine("Creating development CA…");
                caWithKey = CertificateFactory.CreateDevelopmentCa();
                CertificateStoreAccess.Install(StoreName.My, StoreLocation.CurrentUser, caWithKey);
                registry.CaThumbprint = CertificateStoreAccess.NormalizeThumbprint(caWithKey.Thumbprint);
                created.Add($"CA private key → CurrentUser\\My ({registry.CaThumbprint})");
                changes.Add("Created CA");
            }

            var server = TryLoadValidLeaf(
                registry.ServerThumbprint,
                DevCertIdentities.ServerSubject,
                DevCertIdentities.CaSubject,
                DevCertIdentities.ServerAuthOid,
                out var serverStatus);
            Console.WriteLine($"Server (CurrentUser\\My): {serverStatus}");

            if (server is null)
            {
                if (!string.IsNullOrWhiteSpace(registry.ServerThumbprint))
                {
                    Console.Error.WriteLine(
                        "Recorded server thumbprint is missing/invalid. Refusing silent rotation. Use 'renew'.");
                    return 2;
                }

                Console.WriteLine("Creating development WSS server certificate…");
                server = CertificateFactory.CreateServerCertificate(caWithKey);
                CertificateStoreAccess.Install(StoreName.My, StoreLocation.CurrentUser, server);
                registry.ServerThumbprint = CertificateStoreAccess.NormalizeThumbprint(server.Thumbprint);
                created.Add($"Server → CurrentUser\\My ({registry.ServerThumbprint})");
                changes.Add("Created server certificate");
            }

            server.Dispose();

            var client = TryLoadValidLeaf(
                registry.ClientThumbprint,
                DevCertIdentities.ClientSubject,
                DevCertIdentities.CaSubject,
                DevCertIdentities.ClientAuthOid,
                out var clientStatus);
            Console.WriteLine($"Client (CurrentUser\\My): {clientStatus}");

            if (client is null)
            {
                if (!string.IsNullOrWhiteSpace(registry.ClientThumbprint))
                {
                    Console.Error.WriteLine(
                        "Recorded client thumbprint is missing/invalid. Refusing silent rotation. Use 'renew'.");
                    return 2;
                }

                Console.WriteLine("Creating development client certificate…");
                client = CertificateFactory.CreateClientCertificate(caWithKey);
                CertificateStoreAccess.Install(StoreName.My, StoreLocation.CurrentUser, client);
                registry.ClientThumbprint = CertificateStoreAccess.NormalizeThumbprint(client.Thumbprint);
                created.Add($"Client → CurrentUser\\My ({registry.ClientThumbprint})");
                changes.Add("Created client certificate");
            }

            client.Dispose();

            var trusted = CertificateStoreAccess.IsTrustedInRoot(registry.CaThumbprint);
            Console.WriteLine($"CA trust (CurrentUser\\Root): {(trusted ? "present" : "NOT installed")}");

            if (!trusted)
            {
                if (!confirmTrust)
                {
                    Console.WriteLine();
                    Console.WriteLine("CA public certificate is not yet trusted in CurrentUser\\Root.");
                    Console.WriteLine("No trust-store modification was performed.");
                    Console.WriteLine("Re-run with --confirm-trust after reviewing the planned change:");
                    Console.WriteLine();
                    Console.WriteLine("  dotnet run --project tools/TechConnect.Client.DevCerts -- provision --confirm-trust");
                    Console.WriteLine();
                    // Still write registry/config for My-store certs so operators can inspect thumbprints.
                    registry.CaSubject = DevCertIdentities.CaSubject;
                    registry.ServerSubject = DevCertIdentities.ServerSubject;
                    registry.ClientSubject = DevCertIdentities.ClientSubject;
                    ConfigWriter.WriteLocalConfigs(repoRoot, registry);
                    PrintSummary(created, changes, trustPending: true);
                    return 0;
                }

                if (!assumeYes)
                {
                    Console.WriteLine();
                    Console.WriteLine("About to install the development CA public certificate into CurrentUser\\Root.");
                    Console.WriteLine($"  Subject: {DevCertIdentities.CaSubject}");
                    Console.WriteLine($"  Thumbprint: {registry.CaThumbprint}");
                    Console.WriteLine("  No certificates will be removed.");
                    Console.Write("Type YES to continue: ");
                    var answer = Console.ReadLine();
                    if (!string.Equals(answer?.Trim(), "YES", StringComparison.Ordinal))
                    {
                        Console.WriteLine("Aborted. No trust-store change was made.");
                        return 3;
                    }
                }

                using var publicCa = CertificateFactory.PublicOnly(caWithKey);
                CertificateStoreAccess.Install(StoreName.Root, StoreLocation.CurrentUser, publicCa);
                created.Add($"CA public → CurrentUser\\Root ({registry.CaThumbprint})");
                changes.Add("Installed CA trust");
                Console.WriteLine("Installed CA public certificate into CurrentUser\\Root.");
            }

            registry.CaSubject = DevCertIdentities.CaSubject;
            registry.ServerSubject = DevCertIdentities.ServerSubject;
            registry.ClientSubject = DevCertIdentities.ClientSubject;
            ConfigWriter.WriteLocalConfigs(repoRoot, registry);

            if (changes.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("No changes needed. Development certificates are already provisioned and valid.");
                return 0;
            }

            PrintSummary(created, changes, trustPending: false);
            return 0;
        }
        finally
        {
            caWithKey?.Dispose();
        }
    }

    private static void PrintSummary(List<string> created, List<string> changes, bool trustPending)
    {
        Console.WriteLine();
        Console.WriteLine("Summary");
        Console.WriteLine("-------");
        foreach (var change in changes)
        {
            Console.WriteLine($"  • {change}");
        }

        foreach (var item in created)
        {
            Console.WriteLine($"  + {item}");
        }

        Console.WriteLine("  Certificates removed: none");
        if (trustPending)
        {
            Console.WriteLine("  Trust install: pending (--confirm-trust required)");
        }
    }

    private static X509Certificate2? TryLoadValidCa(DevCertRegistry registry, out string status)
    {
        if (string.IsNullOrWhiteSpace(registry.CaThumbprint))
        {
            status = "not recorded";
            return null;
        }

        var cert = CertificateStoreAccess.FindByThumbprint(
            StoreName.My,
            StoreLocation.CurrentUser,
            registry.CaThumbprint,
            requirePrivateKey: true);

        if (cert is null)
        {
            status = $"thumbprint {registry.CaThumbprint[..Math.Min(8, registry.CaThumbprint.Length)]}… not found";
            return null;
        }

        if (!CertificateInspector.MatchesExpectedIdentity(
                cert,
                DevCertIdentities.CaSubject,
                expectedIssuer: DevCertIdentities.CaSubject,
                requiredEkuOid: null,
                requirePrivateKey: true,
                requireCaBasicConstraints: true,
                out var reason))
        {
            status = $"found but invalid ({reason})";
            cert.Dispose();
            return null;
        }

        status = $"valid ({registry.CaThumbprint})";
        return cert;
    }

    private static X509Certificate2? TryLoadValidLeaf(
        string thumbprint,
        string expectedSubject,
        string expectedIssuer,
        string ekuOid,
        out string status)
    {
        if (string.IsNullOrWhiteSpace(thumbprint))
        {
            status = "not recorded";
            return null;
        }

        var cert = CertificateStoreAccess.FindByThumbprint(
            StoreName.My,
            StoreLocation.CurrentUser,
            thumbprint,
            requirePrivateKey: true);

        if (cert is null)
        {
            status = $"thumbprint {thumbprint[..Math.Min(8, thumbprint.Length)]}… not found";
            return null;
        }

        if (!CertificateInspector.MatchesExpectedIdentity(
                cert,
                expectedSubject,
                expectedIssuer,
                ekuOid,
                requirePrivateKey: true,
                requireCaBasicConstraints: false,
                out var reason))
        {
            status = $"found but invalid ({reason})";
            cert.Dispose();
            return null;
        }

        status = $"valid ({thumbprint})";
        return cert;
    }
}
