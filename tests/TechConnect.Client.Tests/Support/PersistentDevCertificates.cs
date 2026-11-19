// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    PersistentDevCertificates.cs
//
// Purpose:
// Loads provisioned TechConnect.Client development certificates from the Windows store for happy-path WSS tests. Never installs or removes certificates.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TechConnect.Client.Configuration;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Loads the provisioned TechConnect.Client development certificates from the Windows store.
/// Never installs or removes certificates.
/// </summary>
internal static class PersistentDevCertificates
{
    /// <summary>
    /// Expected CA subject distinguished name.
    /// </summary>
    public const string CaSubject = "CN=TechConnector Development CA";
    /// <summary>
    /// Expected WSS server subject distinguished name.
    /// </summary>
    public const string ServerSubject = "CN=TechConnector Development WSS";
    /// <summary>
    /// Expected client subject distinguished name.
    /// </summary>
    public const string ClientSubject = "CN=TechConnector Development Client";

    /// <summary>
    /// Validates companion thumbprints and store presence; throws with provision instructions when incomplete.
    /// </summary>
    public static DevCertThumbprints RequireProvisioned()
    {
        var path = FindCompanionPath();
        if (path is null || !File.Exists(path))
        {
            throw new InvalidOperationException(
                "TechConnect.Client development certificates are not provisioned. " +
                "Run: dotnet run --project tools/TechConnect.Client.DevCerts -- provision " +
                "then: dotnet run --project tools/TechConnect.Client.DevCerts -- provision --confirm-trust. " +
                "See docs/TechConnect.Client/development-certificates.md. " +
                "Tests never auto-provision or modify the Windows certificate store.");
        }

        var json = File.ReadAllText(path);
        var registry = JsonSerializer.Deserialize<DevCertThumbprints>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException($"Unable to parse {path}.");

        if (string.IsNullOrWhiteSpace(registry.CaThumbprint) ||
            string.IsNullOrWhiteSpace(registry.ServerThumbprint) ||
            string.IsNullOrWhiteSpace(registry.ClientThumbprint) ||
            registry.CaThumbprint.Contains('<', StringComparison.Ordinal) ||
            registry.ServerThumbprint.Contains('<', StringComparison.Ordinal) ||
            registry.ClientThumbprint.Contains('<', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Development certificate thumbprints in {path} are placeholders or incomplete. " +
                "Run tools/TechConnect.Client.DevCerts provision (with --confirm-trust for Root).");
        }

        using var caRoot = FindRequired(
            StoreName.Root,
            registry.CaThumbprint,
            CaSubject,
            CaSubject,
            requirePrivateKey: false);
        using var caMy = FindRequired(
            StoreName.My,
            registry.CaThumbprint,
            CaSubject,
            CaSubject,
            requirePrivateKey: true);
        using var server = FindRequired(
            StoreName.My,
            registry.ServerThumbprint,
            ServerSubject,
            CaSubject,
            requirePrivateKey: true);
        using var client = FindRequired(
            StoreName.My,
            registry.ClientThumbprint,
            ClientSubject,
            CaSubject,
            requirePrivateKey: true);

        return registry;
    }

    /// <summary>
    /// Loads the provisioned server certificate with private key from CurrentUser\\My.
    /// </summary>
    public static X509Certificate2 LoadServerCertificate()
    {
        var registry = RequireProvisioned();
        return FindRequired(
            StoreName.My,
            registry.ServerThumbprint,
            ServerSubject,
            CaSubject,
            requirePrivateKey: true);
    }

    /// <summary>
    /// Loads the provisioned client certificate with private key from CurrentUser\\My.
    /// </summary>
    public static X509Certificate2 LoadClientCertificate()
    {
        var registry = RequireProvisioned();
        return FindRequired(
            StoreName.My,
            registry.ClientThumbprint,
            ClientSubject,
            CaSubject,
            requirePrivateKey: true);
    }

    /// <summary>
    /// Loads the provisioned CA public certificate from CurrentUser\\Root.
    /// </summary>
    public static X509Certificate2 LoadCaPublicCertificate()
    {
        var registry = RequireProvisioned();
        return FindRequired(
            StoreName.Root,
            registry.CaThumbprint,
            CaSubject,
            CaSubject,
            requirePrivateKey: false);
    }

    /// <summary>
    /// Builds RemoteServiceOptions for WSS tests using the provisioned client thumbprint.
    /// </summary>
    public static RemoteServiceOptions CreateClientOptions(string baseUrl, string webSocketPath = "/ws")
    {
        var registry = RequireProvisioned();
        return new RemoteServiceOptions
        {
            // Legacy WSS fields retained for existing WebSocket test fixtures.
            BaseUrl = baseUrl.TrimEnd('/'),
            WebSocketPath = webSocketPath,
            ConnectTimeoutSeconds = 5,
            HeartbeatIntervalSeconds = 15,
            ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = registry.ClientThumbprint,
                StoreName = "My",
                StoreLocation = StoreLocation.CurrentUser,
                ExpectedSubject = ClientSubject,
                ExpectedIssuer = CaSubject
            }
        };
    }

    /// <summary>
    /// Builds RemoteServiceOptions for TCP mTLS tests using the provisioned client thumbprint.
    /// </summary>
    public static RemoteServiceOptions CreateTcpClientOptions(string host, int port, string tlsHostName = "localhost")
    {
        var registry = RequireProvisioned();
        return new RemoteServiceOptions
        {
            Host = host,
            Port = port,
            TlsHostName = tlsHostName,
            ConnectTimeoutSeconds = 5,
            HeartbeatIntervalSeconds = 15,
            ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = registry.ClientThumbprint,
                StoreName = "My",
                StoreLocation = StoreLocation.CurrentUser,
                ExpectedSubject = ClientSubject,
                ExpectedIssuer = CaSubject
            }
        };
    }

    /// <summary>
    /// Finds exactly one matching certificate by thumbprint/subject/issuer; optionally requires a private key.
    /// </summary>
    private static X509Certificate2 FindRequired(
        StoreName storeName,
        string thumbprint,
        string expectedSubject,
        string expectedIssuer,
        bool requirePrivateKey)
    {
        var normalized = RemoteServiceOptionsValidator.NormalizeThumbprint(thumbprint);
        using var store = new X509Store(storeName, StoreLocation.CurrentUser);
        try
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Unable to open CurrentUser\\{storeName} while loading development certificates.",
                ex);
        }

        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(c => string.Equals(
                RemoteServiceOptionsValidator.NormalizeThumbprint(c.Thumbprint ?? string.Empty),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"Development certificate {normalized} was not found in CurrentUser\\{storeName}. " +
                "Run tools/TechConnect.Client.DevCerts provision / provision --confirm-trust.");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple certificates match development thumbprint {normalized} in CurrentUser\\{storeName}.");
        }

        var cert = matches[0];
        if (!SubjectsEqual(cert.Subject, expectedSubject) ||
            !SubjectsEqual(cert.Issuer, expectedIssuer))
        {
            throw new InvalidOperationException(
                $"Certificate {normalized} does not match the expected development identity " +
                $"(subject '{expectedSubject}', issuer '{expectedIssuer}').");
        }

        if (requirePrivateKey && !cert.HasPrivateKey)
        {
            throw new InvalidOperationException(
                $"Development certificate {normalized} in CurrentUser\\{storeName} has no private key.");
        }

        return new X509Certificate2(cert);
    }

    /// <summary>
    /// Locates dev-certs.json beside the test output or under the tests project.
    /// </summary>
    private static string? FindCompanionPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "dev-certs.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "dev-certs.json")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tests", "TechConnect.Client.Tests", "dev-certs.json"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Case-insensitive DN equality after normalizing comma spacing.
    /// </summary>
    private static bool SubjectsEqual(string left, string right)
    {
        static string Normalize(string value) =>
            string.Join(",", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// DTO for provisioned CA/server/client subjects and thumbprints from dev-certs.json.
/// </summary>
internal sealed class DevCertThumbprints
{
    /// <summary>CA certificate subject from the companion registry (informational).</summary>
    public string CaSubject { get; set; } = string.Empty;

    /// <summary>Server certificate subject from the companion registry (informational).</summary>
    public string ServerSubject { get; set; } = string.Empty;

    /// <summary>Client certificate subject from the companion registry (informational).</summary>
    public string ClientSubject { get; set; } = string.Empty;

    /// <summary>Normalized CA thumbprint used to locate CurrentUser Root/My entries.</summary>
    public string CaThumbprint { get; set; } = string.Empty;

    /// <summary>Normalized server thumbprint used to locate CurrentUser\\My.</summary>
    public string ServerThumbprint { get; set; } = string.Empty;

    /// <summary>Normalized client thumbprint used to locate CurrentUser\\My.</summary>
    public string ClientThumbprint { get; set; } = string.Empty;
}
