// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AppsettingsHostKeyStoreTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (AppsettingsHostKeyStoreTests).
//
// -----------------------------------------------------------------------------
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Configuration;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for appsettings host-key resolve/persist and empty-override semantics.
/// </summary>
public sealed class AppsettingsHostKeyStoreTests
{
    /// <summary>
    /// In Production, generated host keys are written to base <c>appsettings.json</c>
    /// without disturbing unrelated JSON sections.
    /// </summary>
    [Fact]
    public void Production_PersistsToBaseAppsettings()
    {
        using var dir = new TempDirectory();
        var op = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.json"),
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [op],
            extraMarker: "keep-this-value");

        var store = CreateStore(dir.Path, Environments.Production);
        store.PersistenceFilePath.Should().EndWith("appsettings.json");

        var key = SshKeyMaterial.GenerateHostKeyPair();
        store.PersistGeneratedHostPrivateKey(SshKeyMaterial.ExportPrivateKeyPem(key));

        var root = JsonNode.Parse(File.ReadAllText(store.PersistenceFilePath))!;
        root["UnrelatedSection"]!["Marker"]!.GetValue<string>().Should().Be("keep-this-value");
        SshKeyMaterial.PublicKeysEqual(
            SshKeyMaterial.ImportHostPrivateKey(root["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!),
            key).Should().BeTrue();
    }

    /// <summary>
    /// In Development, generated keys land in <c>appsettings.Development.json</c>
    /// and leave the base file's empty HostPrivateKey untouched.
    /// </summary>
    [Fact]
    public void Development_PersistsToEnvironmentAppsettings()
    {
        using var dir = new TempDirectory();
        var op = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.json"),
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [op]);
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.Development.json"),
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [op],
            extraMarker: "dev-marker");

        var store = CreateStore(dir.Path, Environments.Development);
        store.PersistenceFilePath.Should().EndWith("appsettings.Development.json");

        var key = SshKeyMaterial.GenerateHostKeyPair();
        store.PersistGeneratedHostPrivateKey(SshKeyMaterial.ExportPrivateKeyPem(key));

        File.Exists(store.PersistenceFilePath).Should().BeTrue();
        JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "appsettings.json")))!
            ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()
            .Should().BeNullOrWhiteSpace();

        JsonNode.Parse(File.ReadAllText(store.PersistenceFilePath))!
            ["UnrelatedSection"]!["Marker"]!.GetValue<string>().Should().Be("dev-marker");
    }

    /// <summary>
    /// An empty Development HostPrivateKey must not shadow a populated base key;
    /// resolution reports the base file as the effective source.
    /// </summary>
    [Fact]
    public void EmptyDevelopmentOverride_DoesNotShadowBaseKey()
    {
        using var dir = new TempDirectory();
        var baseKey = SshTestKeys.CreateEcdsaKeyPair("base");
        var op = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.json"),
            hostPrivateKey: SshTestKeys.ExportPrivateKeyPem(baseKey),
            authorizedPublicKeys: [op]);
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.Development.json"),
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [op]);

        var store = CreateStore(dir.Path, Environments.Development);
        var effective = store.TryResolveEffectiveHostPrivateKey();

        effective.Should().NotBeNull();
        effective!.SourceFilePath.Should().EndWith("appsettings.json");
        SshKeyMaterial.ComputeFingerprint(SshKeyMaterial.ImportHostPrivateKey(effective.PrivateKeyPem))
            .Should().Be(SshKeyMaterial.ComputeFingerprint(baseKey));
    }

    /// <summary>
    /// A non-empty Development HostPrivateKey wins over the base key fingerprint.
    /// </summary>
    [Fact]
    public void DevelopmentKey_OverridesBaseKey()
    {
        using var dir = new TempDirectory();
        var baseKey = SshTestKeys.CreateEcdsaKeyPair("base");
        var devKey = SshTestKeys.CreateEcdsaKeyPair("dev");
        var op = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.json"),
            hostPrivateKey: SshTestKeys.ExportPrivateKeyPem(baseKey),
            authorizedPublicKeys: [op]);
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.Development.json"),
            hostPrivateKey: SshTestKeys.ExportPrivateKeyPem(devKey),
            authorizedPublicKeys: [op]);

        var store = CreateStore(dir.Path, Environments.Development);
        var effective = store.TryResolveEffectiveHostPrivateKey();

        effective.Should().NotBeNull();
        effective!.SourceFilePath.Should().EndWith("appsettings.Development.json");
        SshKeyMaterial.ComputeFingerprint(SshKeyMaterial.ImportHostPrivateKey(effective.PrivateKeyPem))
            .Should().Be(SshKeyMaterial.ComputeFingerprint(devKey));
    }

    /// <summary>
    /// Persist refuses to overwrite an already-configured host private key.
    /// </summary>
    [Fact]
    public void PersistGeneratedHostPrivateKey_RefusesOverwrite()
    {
        using var dir = new TempDirectory();
        var existing = SshTestKeys.CreateEcdsaKeyPair("existing");
        var op = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));
        SshTestHost.WriteAppsettings(
            Path.Combine(dir.Path, "appsettings.json"),
            hostPrivateKey: SshTestKeys.ExportPrivateKeyPem(existing),
            authorizedPublicKeys: [op]);

        var store = CreateStore(dir.Path, Environments.Production);
        var act = () => store.PersistGeneratedHostPrivateKey(
            SshKeyMaterial.ExportPrivateKeyPem(SshKeyMaterial.GenerateHostKeyPair()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to overwrite*");
    }

    /// <summary>
    /// When the target file is locked, persist fails closed and leaves the original
    /// bytes (and no leftover <c>.tmp</c> files).
    /// </summary>
    [Fact]
    public void PersistGeneratedHostPrivateKey_Failure_LeavesOriginalIntact()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "appsettings.json");
        SshTestHost.WriteAppsettings(
            path,
            hostPrivateKey: string.Empty,
            authorizedPublicKeys: [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"))],
            extraMarker: "original");

        var store = CreateStore(dir.Path, Environments.Production);
        var original = File.ReadAllText(path);

        using (var lockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => store.PersistGeneratedHostPrivateKey(
                SshKeyMaterial.ExportPrivateKeyPem(SshKeyMaterial.GenerateHostKeyPair()));

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Failed to persist*");
        }

        File.ReadAllText(path).Should().Be(original);
        Directory.GetFiles(dir.Path, "*.tmp").Should().BeEmpty();
    }
    /// <summary>Builds an <see cref="AppsettingsHostKeyStore"/> against a temp content root.</summary>
    private static AppsettingsHostKeyStore CreateStore(string contentRoot, string environmentName)
    {
        return new AppsettingsHostKeyStore(
            new TestHostEnvironment(contentRoot, environmentName),
            NullLogger<AppsettingsHostKeyStore>.Instance);
    }
    /// <summary>Minimal <see cref="IHostEnvironment"/> pointing at a temp content root.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <summary>Creates a host environment for the given content root and ASP.NET environment name.</summary>
        public TestHostEnvironment(string contentRoot, string environmentName)
        {
            ContentRootPath = contentRoot;
            EnvironmentName = environmentName;
        }

        /// <inheritdoc />
        public string EnvironmentName { get; set; }
        /// <inheritdoc />
        public string ApplicationName { get; set; } = "TechConnect.Server.Tests";
        /// <inheritdoc />
        public string ContentRootPath { get; set; }
        /// <inheritdoc />
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    /// <summary>Unique temp directory deleted on dispose (best-effort).</summary>
    private sealed class TempDirectory : IDisposable
    {
        /// <summary>Creates and owns a unique directory under the process temp path.</summary>
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Absolute path of the owned temp directory.</summary>
        public string Path { get; }

        /// <summary>Best-effort recursive delete of the owned directory.</summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
