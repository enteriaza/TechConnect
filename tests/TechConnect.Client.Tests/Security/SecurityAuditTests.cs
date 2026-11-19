// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SecurityAuditTests.cs
//
// Purpose:
// Static security audits of TechConnect.Client sources and test support: no cert-validation bypass, no plaintext HTTP fallbacks, no hardcoded credentials, no store mutation in tests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TechConnect.Client.Tests.Security;

/// <summary>
/// Static audits of TechConnect.Client sources and tests for TLS/credential/store safety invariants.
/// </summary>
public sealed class SecurityAuditTests
{
    /// <summary>
    /// Walks up from the test BaseDirectory until TechConnect.slnx/TechConnect.sln or src/TechConnect.Client is found.
    /// </summary>
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TechConnect.slnx")) ||
                    File.Exists(Path.Combine(dir.FullName, "TechConnect.sln")) ||
                    Directory.Exists(Path.Combine(dir.FullName, "src", "TechConnect.Client")))
                {
                    return dir.FullName;
                }

                dir = dir.Parent;
            }

            // Fallback: tests project lives under tests/TechConnect.Client.Tests
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        }
    }

    /// <summary>
    /// Absolute path to src/TechConnect.Client under <see cref="RepoRoot"/>.
    /// </summary>
    private static string TechConnectClientSrc => Path.Combine(RepoRoot, "src", "TechConnect.Client");

    /// <summary>
    /// Scenario: read TcpMtlsRemoteConsoleTransport.cs from source.
    /// Expected: no DangerousAcceptAnyServerCertificateValidator or custom RemoteCertificateValidationCallback.
    /// </summary>
    [Fact]
    public void TcpMtlsTransport_DoesNotBypassCertificateValidation()
    {
        var path = Path.Combine(TechConnectClientSrc, "Transport", "TcpMtlsRemoteConsoleTransport.cs");
        File.Exists(path).Should().BeTrue($"expected source at {path}");
        var source = File.ReadAllText(path);

        source.Should().NotContain("DangerousAcceptAnyServerCertificateValidator");
        source.Should().NotContain("RemoteCertificateValidationCallback =");
        source.Should().NotContain(".RemoteCertificateValidationCallback");
    }

    /// <summary>
    /// Scenario: read RemoteEndpoint.cs from source.
    /// Expected: no http://, ws://, or wss:// scheme fallbacks.
    /// </summary>
    [Fact]
    public void RemoteEndpoint_HasNoHttpOrWebSocketFallbacks()
    {
        var path = Path.Combine(TechConnectClientSrc, "Transport", "RemoteEndpoint.cs");
        var source = File.ReadAllText(path);

        Regex.IsMatch(source, @"[""']http://", RegexOptions.IgnoreCase).Should().BeFalse();
        Regex.IsMatch(source, @"[""']ws://", RegexOptions.IgnoreCase).Should().BeFalse();
        Regex.IsMatch(source, @"[""']wss://", RegexOptions.IgnoreCase).Should().BeFalse();
        source.Should().NotContain("CreateWebSocketUri");
        source.Should().NotContain("WebSocket");
    }

    /// <summary>
    /// Scenario: parse tracked appsettings.json under TechConnect.Client.
    /// Expected: credential-named fields empty; RemoteService Host and Thumbprint empty.
    /// </summary>
    [Fact]
    public void Appsettings_HasNoHardcodedCredentials()
    {
        var path = Path.Combine(TechConnectClientSrc, "appsettings.json");
        File.Exists(path).Should().BeTrue();
        var json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);

        AssertNoSecretValues(doc.RootElement, path);

        // Explicit RemoteService checks.
        var remote = doc.RootElement.GetProperty("RemoteService");
        remote.GetProperty("Host").GetString().Should().BeNullOrEmpty();
        remote.GetProperty("ClientCertificate").GetProperty("Thumbprint").GetString().Should().BeNullOrEmpty();
    }

    /// <summary>
    /// Scenario: scan Support/*.cs for store mutation APIs.
    /// Expected: no store.Add/Remove, InstallCaTrust, RemoveInstalledCertificates, or trustCaInCurrentUserRoot.
    /// </summary>
    [Fact]
    public void Tests_DoNotInstallOrRemoveWindowsCertificates()
    {
        var supportDir = Path.Combine(RepoRoot, "tests", "TechConnect.Client.Tests", "Support");
        Directory.Exists(supportDir).Should().BeTrue();

        foreach (var file in Directory.EnumerateFiles(supportDir, "*.cs"))
        {
            var source = File.ReadAllText(file);
            source.Should().NotContain("store.Add(", because: $"{Path.GetFileName(file)} must not mutate certificate stores");
            source.Should().NotContain("store.Remove(", because: $"{Path.GetFileName(file)} must not mutate certificate stores");
            source.Should().NotContain("InstallCaTrust", because: "ephemeral CA trust installation was removed");
            source.Should().NotContain("RemoveInstalledCertificates", because: "certificate-store cleanup was removed");
            source.Should().NotContain("trustCaInCurrentUserRoot", because: $"{Path.GetFileName(file)} must not install CA trust");
        }
    }

    /// <summary>
    /// Recursively asserts credential-named JSON string properties are null/whitespace.
    /// </summary>
    private static void AssertNoSecretValues(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    var name = prop.Name;
                    if (IsCredentialName(name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        value.Should().BeNullOrWhiteSpace(
                            because: $"{path} must not hardcode credential field '{name}'");
                    }

                    AssertNoSecretValues(prop.Value, path);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoSecretValues(item, path);
                }

                break;
        }
    }

    /// <summary>
    /// Returns true when a JSON property name looks like a credential field.
    /// </summary>
    private static bool IsCredentialName(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Thumbprint", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("connectionstring", StringComparison.OrdinalIgnoreCase);
}
