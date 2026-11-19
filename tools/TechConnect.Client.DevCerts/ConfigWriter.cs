using System.Text.Json;
using System.Text.Json.Nodes;

namespace TechConnect.Client.DevCerts;

internal static class ConfigWriter
{
    public static void WriteLocalConfigs(string repoRoot, DevCertRegistry registry, bool reportOnly = false)
    {
        registry.Save(RepoPaths.RegistryPath(repoRoot));
        WriteTestCompanion(RepoPaths.TestCompanionPath(repoRoot), registry);
        WriteAppsettingsDevelopment(RepoPaths.AppsettingsDevelopmentPath(repoRoot), registry);

        Console.WriteLine($"Wrote registry: {RepoPaths.RegistryPath(repoRoot)}");
        Console.WriteLine($"Wrote test companion: {RepoPaths.TestCompanionPath(repoRoot)}");
        Console.WriteLine($"Updated: {RepoPaths.AppsettingsDevelopmentPath(repoRoot)}");
        _ = reportOnly;
    }

    private static void WriteTestCompanion(string path, DevCertRegistry registry)
    {
        var node = new JsonObject
        {
            ["caSubject"] = registry.CaSubject,
            ["serverSubject"] = registry.ServerSubject,
            ["clientSubject"] = registry.ClientSubject,
            ["caThumbprint"] = registry.CaThumbprint,
            ["serverThumbprint"] = registry.ServerThumbprint,
            ["clientThumbprint"] = registry.ClientThumbprint,
            ["updatedUtc"] = registry.UpdatedUtc
        };

        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteAppsettingsDevelopment(string path, DevCertRegistry registry)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        var remote = root["RemoteService"] as JsonObject ?? new JsonObject();
        root["RemoteService"] = remote;

        var clientCert = remote["ClientCertificate"] as JsonObject ?? new JsonObject();
        remote["ClientCertificate"] = clientCert;
        clientCert["Thumbprint"] = registry.ClientThumbprint;
        clientCert["StoreName"] = "My";
        clientCert["StoreLocation"] = "CurrentUser";
        clientCert["ExpectedSubject"] = DevCertIdentities.ClientSubject;
        clientCert["ExpectedIssuer"] = DevCertIdentities.CaSubject;

        if (string.IsNullOrWhiteSpace(remote["BaseUrl"]?.GetValue<string>()))
        {
            // Placeholder for local pre-prod; operator fills host/port when running a local WSS endpoint.
            remote["BaseUrl"] = "https://127.0.0.1:7443";
            remote["WebSocketPath"] = "/ws";
        }

        var development = root["DevelopmentCertificates"] as JsonObject ?? new JsonObject();
        root["DevelopmentCertificates"] = development;
        development["CaThumbprint"] = registry.CaThumbprint;
        development["ServerThumbprint"] = registry.ServerThumbprint;
        development["ClientThumbprint"] = registry.ClientThumbprint;
        development["CaSubject"] = DevCertIdentities.CaSubject;
        development["ServerSubject"] = DevCertIdentities.ServerSubject;
        development["ClientSubject"] = DevCertIdentities.ClientSubject;

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
