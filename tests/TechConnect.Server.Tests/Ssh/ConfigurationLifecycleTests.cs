// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationLifecycleTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (ConfigurationLifecycleTests).
//
// -----------------------------------------------------------------------------
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Hosting;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for SSH host-key configuration lifecycle across environments.
/// </summary>
public sealed class ConfigurationLifecycleTests
{
    /// <summary>
    /// Verifies: Scenario A Development Generates Into Development Json Stable Across Restart.
    /// </summary>
    [Fact]
    public async Task ScenarioA_Development_GeneratesIntoDevelopmentJson_StableAcrossRestart()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        string fingerprint1;
        string persistedPem;

        await using (var host = await SshTestHost.StartAsync(
                         hostKey: null,
                         authorizedOperatorKeys: [operatorKey],
                         environmentName: Environments.Development))
        {
            host.PersistenceAppsettingsPath.Should().EndWith("appsettings.Development.json");
            File.Exists(host.PersistenceAppsettingsPath).Should().BeTrue();
            fingerprint1 = host.HostKeyFingerprint;
            persistedPem = JsonNode.Parse(File.ReadAllText(host.PersistenceAppsettingsPath))!
                ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!;
            persistedPem.Should().NotBeNullOrWhiteSpace();

            // Base file should not have received the generated key.
            var baseKey = JsonNode.Parse(File.ReadAllText(host.BaseAppsettingsPath))!
                ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]?.GetValue<string>();
            baseKey.Should().BeNullOrWhiteSpace();
        }

        var restartedKey = SshKeyMaterial.ImportHostPrivateKey(persistedPem);
        await using var host2 = await SshTestHost.StartAsync(
            restartedKey,
            [operatorKey],
            environmentName: Environments.Development);

        host2.HostKeyFingerprint.Should().Be(fingerprint1);
        var (ok, output) = await SshTestHost.ConnectAndRunAsync(
            host2.Port, restartedKey, "noc", operatorKey, ["help"]);
        ok.Should().BeTrue();
        output.Should().Contain("Available commands");
    }

    /// <summary>
    /// Verifies: Scenario B Production Generates Into Base Appsettings Stable Across Restart.
    /// </summary>
    [Fact]
    public async Task ScenarioB_Production_GeneratesIntoBaseAppsettings_StableAcrossRestart()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        string fingerprint1;
        string persistedPem;

        await using (var host = await SshTestHost.StartAsync(
                         hostKey: null,
                         authorizedOperatorKeys: [operatorKey],
                         environmentName: Environments.Production))
        {
            host.PersistenceAppsettingsPath.Should().EndWith("appsettings.json");
            fingerprint1 = host.HostKeyFingerprint;
            persistedPem = JsonNode.Parse(File.ReadAllText(host.BaseAppsettingsPath))!
                ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!;
        }

        var restartedKey = SshKeyMaterial.ImportHostPrivateKey(persistedPem);
        await using var host2 = await SshTestHost.StartAsync(
            restartedKey,
            [operatorKey],
            environmentName: Environments.Production);

        host2.HostKeyFingerprint.Should().Be(fingerprint1);
    }

    /// <summary>
    /// Verifies: Empty Development Override Uses Base Key No Regeneration.
    /// </summary>
    [Fact]
    public async Task EmptyDevelopmentOverride_UsesBaseKey_NoRegeneration()
    {
        var baseKey = SshTestKeys.CreateEcdsaKeyPair("base-host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        var expected = SshKeyMaterial.ComputeFingerprint(baseKey);

        var contentRoot = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);
        try
        {
            var port = SshTestHost.GetFreeTcpPort();
            var auth = SshTestKeys.ExportPublicKeyLine(operatorKey);
            SshTestHost.WriteAppsettings(
                Path.Combine(contentRoot, "appsettings.json"),
                SshTestKeys.ExportPrivateKeyPem(baseKey),
                [auth],
                listenPort: port);
            SshTestHost.WriteAppsettings(
                Path.Combine(contentRoot, "appsettings.Development.json"),
                hostPrivateKey: string.Empty,
                authorizedPublicKeys: [auth],
                listenPort: port);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = contentRoot,
                EnvironmentName = Environments.Development,
                Args = []
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TechConnectServer:Ssh:Port"] = port.ToString(),
                ["Logging:File:Directory"] = Path.Combine(contentRoot, "logs"),
                ["Logging:File:FileNamePrefix"] = "test",
                ["Serilog:MinimumLevel:Default"] = "Warning"
            });
            builder.Logging.ClearProviders();
            builder.AddTechConnectServerHost();

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var provider = host.Services.GetRequiredService<SshHostKeyProvider>();
                provider.Fingerprint.Should().Be(expected);
                provider.Source.Should().EndWith("appsettings.json");

                // Development file must still be empty — no accidental generation.
                JsonNode.Parse(File.ReadAllText(Path.Combine(contentRoot, "appsettings.Development.json")))!
                    ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()
                    .Should().BeNullOrWhiteSpace();
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
            try { Directory.Delete(contentRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Verifies: Scenario C Clean Rebuild Preserve Newest Keeps Generated Key.
    /// </summary>
    [Fact]
    public async Task ScenarioC_CleanRebuild_PreserveNewestKeepsGeneratedKey()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        await using var host = await SshTestHost.StartAsync(
            hostKey: null,
            authorizedOperatorKeys: [operatorKey],
            environmentName: Environments.Production);

        var fingerprint = host.HostKeyFingerprint;
        var appsettingsPath = host.BaseAppsettingsPath;
        var generatedJson = File.ReadAllText(appsettingsPath);
        var contentRoot = host.ContentRoot;

        // Simulate a clean rebuild copying an older/empty source template over a newer output
        // only when source is newer — PreserveNewest keeps destination when it is newer.
        var sourceTemplate = Path.Combine(contentRoot, "source-template-appsettings.json");
        SshTestHost.WriteAppsettings(
            sourceTemplate,
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [SshTestKeys.ExportPublicKeyLine(operatorKey)]);

        // Make destination newer than source.
        File.SetLastWriteTimeUtc(sourceTemplate, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(appsettingsPath, DateTime.UtcNow);

        // Emulate PreserveNewest: copy only if source is newer.
        if (File.GetLastWriteTimeUtc(sourceTemplate) > File.GetLastWriteTimeUtc(appsettingsPath))
        {
            File.Copy(sourceTemplate, appsettingsPath, overwrite: true);
        }

        File.ReadAllText(appsettingsPath).Should().Be(generatedJson);
        SshKeyMaterial.ComputeFingerprint(
            SshKeyMaterial.ImportHostPrivateKey(
                JsonNode.Parse(generatedJson)!["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!))
            .Should().Be(fingerprint);
    }

    /// <summary>
    /// Verifies: Scenario D Publish Overwrite Without Preservation Loses Key With Backup Restore Keeps Key.
    /// </summary>
    [Fact]
    public async Task ScenarioD_PublishOverwrite_WithoutPreservation_LosesKey_WithBackupRestore_KeepsKey()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        await using var host = await SshTestHost.StartAsync(
            hostKey: null,
            authorizedOperatorKeys: [operatorKey],
            environmentName: Environments.Production);

        var fingerprint = host.HostKeyFingerprint;
        var deployDir = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", "deploy-" + Guid.NewGuid().ToString("N"));
        var publishDir = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", "publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(deployDir);
        Directory.CreateDirectory(publishDir);

        try
        {
            // Initial deployment contains generated config.
            File.Copy(host.BaseAppsettingsPath, Path.Combine(deployDir, "appsettings.json"));

            // New publish output has empty host key (fresh project template).
            SshTestHost.WriteAppsettings(
                Path.Combine(publishDir, "appsettings.json"),
                hostPrivateKey: string.Empty,
                authorizedPublicKeys: [SshTestKeys.ExportPublicKeyLine(operatorKey)]);

            // Naive overwrite loses the key.
            File.Copy(
                Path.Combine(publishDir, "appsettings.json"),
                Path.Combine(deployDir, "appsettings.json"),
                overwrite: true);
            JsonNode.Parse(File.ReadAllText(Path.Combine(deployDir, "appsettings.json")))!
                ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()
                .Should().BeNullOrWhiteSpace();

            // Recommended procedure: backup before publish, restore after.
            File.Copy(host.BaseAppsettingsPath, Path.Combine(deployDir, "appsettings.json"), overwrite: true);
            var backup = Path.Combine(deployDir, "appsettings.json.bak");
            File.Copy(Path.Combine(deployDir, "appsettings.json"), backup, overwrite: true);

            // Simulate publish overwrite again...
            File.Copy(
                Path.Combine(publishDir, "appsettings.json"),
                Path.Combine(deployDir, "appsettings.json"),
                overwrite: true);
            // ...then restore.
            File.Copy(backup, Path.Combine(deployDir, "appsettings.json"), overwrite: true);

            var restored = JsonNode.Parse(File.ReadAllText(Path.Combine(deployDir, "appsettings.json")))!
                ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!;
            SshKeyMaterial.ComputeFingerprint(SshKeyMaterial.ImportHostPrivateKey(restored))
                .Should().Be(fingerprint);
        }
        finally
        {
            try { Directory.Delete(deployDir, recursive: true); } catch { /* best-effort */ }
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
