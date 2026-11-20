// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationSecretSourceTests.cs
//
// Purpose:
// Verifies that 'write' never copies values supplied by higher-precedence
// configuration providers (environment variables, appsettings.{Environment}.json)
// into the base appsettings.json — in particular secrets — while explicit
// operator changes are persisted in the correct form.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Nodes;
using FluentAssertions;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Secret provenance tests for configuration writes.
/// </summary>
public sealed class ConfigurationSecretSourceTests
{
    /// <summary>Configuration key of the primary RADIUS shared secret.</summary>
    private const string SharedSecretKey = "TechConnectServer:Authentication:Radius:PrimaryServer:SharedSecret";

    /// <summary>Configuration key of the first local user's password hash.</summary>
    private const string FirstUserHashKey = "TechConnectServer:Authentication:Local:LocalUsers:0:PasswordHash";

    /// <summary>Shipped file with the primary RADIUS shared secret set to <paramref name="secret"/>.</summary>
    private static string FileWithSharedSecret(string secret)
    {
        var json = JsonNode.Parse(ConfigurationFixture.ShippedAppsettings())!.AsObject();
        json["TechConnectServer"]!["Authentication"]!["Radius"]!["PrimaryServer"]!["SharedSecret"] = secret;
        return json.ToJsonString();
    }

    /// <summary>Reads a value from the saved file.</summary>
    private static JsonNode? FileValue(ConfigurationFixture fixture, params object[] path)
    {
        JsonNode? node = JsonNode.Parse(fixture.ReadFile());
        foreach (var segment in path)
        {
            node = segment is int index ? node![index] : node![(string)segment];
        }

        return node;
    }

    /// <summary>Primary RADIUS shared secret stored in the file.</summary>
    private static string? FileSharedSecret(ConfigurationFixture fixture) =>
        FileValue(fixture, "TechConnectServer", "Authentication", "Radius", "PrimaryServer", "SharedSecret")?.GetValue<string>();

    /// <summary>Changes an unrelated, restart-required setting.</summary>
    private static void ChangeUnrelated(ConfigurationEditSession edit) =>
        ConfigPaths.Set(edit, "ssh", "port", JsonValue.Create(2201L));

    /// <summary>A secret that exists only in an override provider is not written into the base file.</summary>
    [Fact]
    public async Task EnvironmentOnlySecret_IsNotWrittenToBaseFile()
    {
        using var fixture = new ConfigurationFixture(overrides: new Dictionary<string, string?> { [SharedSecretKey] = "Env-Only-Secret-1" });
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out var notes);
        ChangeUnrelated(edit);

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        fixture.ReadFile().Should().NotContain("Env-Only-Secret-1");
        FileSharedSecret(fixture).Should().BeEmpty();
        FileValue(fixture, "TechConnectServer", "Ssh", "Port")!.GetValue<int>().Should().Be(2201);
        string.Join("\n", notes).Should().NotContain("Env-Only-Secret-1");
    }

    /// <summary>An override does not replace the base-file secret during an unrelated change.</summary>
    [Fact]
    public async Task EnvironmentOverride_DoesNotReplaceBaseFileSecret()
    {
        using var fixture = new ConfigurationFixture(
            FileWithSharedSecret("File-Secret-2"),
            new Dictionary<string, string?> { [SharedSecretKey] = "Env-Secret-2" });
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ChangeUnrelated(edit);

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);

        FileSharedSecret(fixture).Should().Be("File-Secret-2");
        fixture.ReadFile().Should().NotContain("Env-Secret-2");
    }

    /// <summary>An overridden password hash in a collection entry is not copied over the file's hash.</summary>
    [Fact]
    public async Task EnvironmentOverride_OfCollectionSecret_DoesNotReplaceFileHash()
    {
        var envHash = new OperatorPasswordHasher().HashPassword("admin", "Env-Password-3");
        using var fixture = new ConfigurationFixture(overrides: new Dictionary<string, string?> { [FirstUserHashKey] = envHash });
        var originalHash = FileValue(fixture, "TechConnectServer", "Authentication", "Local", "LocalUsers", 0, "PasswordHash")!.GetValue<string>();
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ChangeUnrelated(edit);

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);

        FileValue(fixture, "TechConnectServer", "Authentication", "Local", "LocalUsers", 0, "PasswordHash")!.GetValue<string>()
            .Should().Be(originalHash);
        fixture.ReadFile().Should().NotContain(envHash);
    }

    /// <summary>A collection entry supplied only by an override provider is not copied into the file.</summary>
    [Fact]
    public async Task EnvironmentOnlyCollectionEntry_IsNotWrittenToBaseFile()
    {
        var envHash = new OperatorPasswordHasher().HashPassword("envuser", "Env-Password-4");
        using var fixture = new ConfigurationFixture(overrides: new Dictionary<string, string?>
        {
            ["TechConnectServer:Authentication:Local:LocalUsers:1:Username"] = "envuser",
            ["TechConnectServer:Authentication:Local:LocalUsers:1:PrivilegeLevel"] = "1",
            ["TechConnectServer:Authentication:Local:LocalUsers:1:PasswordHash"] = envHash
        });
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ChangeUnrelated(edit);

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);

        fixture.ReadFile().Should().NotContain("envuser").And.NotContain(envHash);
        FileValue(fixture, "TechConnectServer", "Authentication", "Local", "LocalUsers")!.AsArray().Should().HaveCount(1);
    }

    /// <summary>A base-file secret is preserved when the candidate does not change it.</summary>
    [Fact]
    public async Task BaseFileSecret_IsPreserved_WhenNotChanged()
    {
        using var fixture = new ConfigurationFixture(FileWithSharedSecret("File-Secret-5"));
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.AsString(ConfigurationModel.GetValue(
                ConfigPaths.Node(edit.Candidate, "authentication radius primary-server"),
                ConfigPaths.Setting("authentication radius primary-server", "shared-secret")))
            .Should().Be("File-Secret-5", "the candidate holds the real value, never a masked placeholder");
        ChangeUnrelated(edit);

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);

        FileSharedSecret(fixture).Should().Be("File-Secret-5");
        fixture.ReadFile().Should().NotContain(ConfigurationModel.HiddenSecret);
    }

    /// <summary>
    /// Explicitly entered secrets are persisted in the correct form (shared secret as entered,
    /// password as a verifiable hash); an operator change to an overridden setting is persisted
    /// and the operator is told the override still wins.
    /// </summary>
    [Fact]
    public async Task ExplicitSecrets_ArePersistedInCorrectForm_AndOverrideIsReported()
    {
        using var fixture = new ConfigurationFixture(
            FileWithSharedSecret("File-Secret-6"),
            new Dictionary<string, string?> { [SharedSecretKey] = "Env-Secret-6" });
        var service = fixture.CreateService();
        var processor = new ConfigurationModeProcessor(service, CliTestFactory.CreateAuthorization());
        var admin = ConfigurationFixture.Admin();
        var edit = service.BeginEdit(admin, out _);
        foreach (var line in new[] { "authentication", "radius", "primary-server" })
        {
            processor.Execute(edit, admin, line);
        }

        processor.ApplySecret(processor.Execute(edit, admin, "shared-secret").Secret!, "Operator-Secret-6", "Operator-Secret-6").Should().BeNull();
        for (var i = 0; i < 3; i++)
        {
            processor.Execute(edit, admin, "exit");
        }
        foreach (var line in new[] { "authentication", "local", "username admin" })
        {
            processor.Execute(edit, admin, line);
        }

        processor.ApplySecret(processor.Execute(edit, admin, "password").Secret!, "New-Admin-Pass-6", "New-Admin-Pass-6").Should().BeNull();

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        FileSharedSecret(fixture).Should().Be("Operator-Secret-6");
        fixture.ReadFile().Should().NotContain("Env-Secret-6").And.NotContain("New-Admin-Pass-6");
        var hash = FileValue(fixture, "TechConnectServer", "Authentication", "Local", "LocalUsers", 0, "PasswordHash")!.GetValue<string>();
        new OperatorPasswordHasher().VerifyPassword("admin", hash, "New-Admin-Pass-6").Should().BeTrue();
        var report = ConfigurationModeProcessor.FormatWriteResult(result);
        report.Should().Contain("authentication radius primary-server shared-secret").And.Contain("another configuration source").And.Contain("still takes precedence");
        report.Should().NotContain("Operator-Secret-6").And.NotContain("Env-Secret-6").And.NotContain("File-Secret-6");
    }

    /// <summary>
    /// A runtime setting that another provider overrides is saved to the file but neither pushed to the
    /// running service nor reported as applied; the override is reported instead.
    /// </summary>
    [Fact]
    public async Task OverriddenRuntimeSetting_IsNotClaimedAsApplied()
    {
        using var fixture = new ConfigurationFixture(overrides: new Dictionary<string, string?> { ["TechConnectServer:Dns:Servers:0"] = "192.0.2.10" });
        var resolver = new TechConnect.Server.NameResolution.ConfiguredDnsResolver(
            [System.Net.IPAddress.Parse("192.0.2.10")],
            TimeSpan.FromSeconds(3),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TechConnect.Server.NameResolution.ConfiguredDnsResolver>.Instance);
        var service = fixture.CreateService(dns: resolver);
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.SetValue(ConfigPaths.Node(edit.Candidate, "dns"), ConfigPaths.Setting("dns", "servers"), new JsonArray("192.0.2.20"));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.AppliedAtRuntime.Should().NotContain("dns servers");
        result.RestartRequired.Should().NotContain("dns servers");
        result.Messages.Should().ContainSingle(m => m.Contains("dns servers", StringComparison.Ordinal));
        resolver.ConfiguredServers.Should().Equal(System.Net.IPAddress.Parse("192.0.2.10"));
        FileValue(fixture, "TechConnectServer", "Dns", "Servers")!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("192.0.2.20");
    }

    /// <summary>Masked display values can never be entered back as secrets.</summary>
    [Fact]
    public void MaskedDisplayValues_CannotBeWrittenBack()
    {
        using var fixture = new ConfigurationFixture(FileWithSharedSecret("File-Secret-7"));
        var service = fixture.CreateService();
        var processor = new ConfigurationModeProcessor(service, CliTestFactory.CreateAuthorization());
        var admin = ConfigurationFixture.Admin();
        var edit = service.BeginEdit(admin, out _);
        foreach (var line in new[] { "authentication", "radius", "primary-server" })
        {
            processor.Execute(edit, admin, line);
        }

        processor.Execute(edit, admin, "shared-secret <hidden>").ForgetHistory.Should().BeTrue();
        for (var i = 0; i < 3; i++)
        {
            processor.Execute(edit, admin, "exit");
        }
        foreach (var line in new[] { "authentication", "local" })
        {
            processor.Execute(edit, admin, line);
        }

        processor.Execute(edit, admin, "enable-secret hash <hidden>").Output.Should().Contain("Identity V3 hash is required");
        edit.HasUnsavedChanges.Should().BeFalse();
    }

    /// <summary>File and override secrets never appear in views, diffs, notes, validation errors, logs, or audit.</summary>
    [Fact]
    public async Task SecretValues_NeverAppearInOutputLogsOrAudit()
    {
        using var fixture = new ConfigurationFixture(
            FileWithSharedSecret("File-Secret-8"),
            new Dictionary<string, string?>
            {
                ["TechConnectServer:Authentication:Radius:SecondaryServer:SharedSecret"] = "Env-Secret-8",
                ["TechConnectServer:Authentication:Radius:SecondaryServer:Address"] = "192.0.2.8"
            });
        var logger = new CapturingLogger<ConfigurationManagementService>();
        var audit = CliTestFactory.CreateAudit();
        var service = fixture.CreateService(logger: logger, audit: audit);
        var processor = new ConfigurationModeProcessor(service, CliTestFactory.CreateAuthorization());
        var admin = ConfigurationFixture.Admin();
        var edit = service.BeginEdit(admin, out var notes);
        var output = new List<string>(notes);
        foreach (var line in new[] { "authentication", "radius", "primary-server" })
        {
            output.Add(processor.Execute(edit, admin, line).Output ?? string.Empty);
        }

        processor.ApplySecret(processor.Execute(edit, admin, "shared-secret").Secret!, "Typed-Secret-8", "Typed-Secret-8");
        output.Add(processor.Execute(edit, admin, "show candidate-config").Output!);
        output.Add(processor.Execute(edit, admin, "show configuration changes").Output!);
        output.Add(service.RenderRunning());
        output.Add(service.RenderStartup());
        ((JsonObject)((JsonArray)ConfigPaths.Node(edit.Candidate, "authentication local")["LocalUsers"]!)[0]!)["PasswordHash"] = string.Empty;
        output.Add(processor.Execute(edit, admin, "validate").Output!);
        var failed = await service.WriteAsync(edit, CancellationToken.None);
        output.Add(ConfigurationModeProcessor.FormatWriteResult(failed));
        service.Discard(edit, "end");

        failed.Status.Should().Be(ConfigurationWriteStatus.ValidationFailed);
        var everything = string.Join("\n", output) + "\n" + logger.AllText + "\n" + string.Join(
            "\n",
            audit.GetRecent(100).Select(e => e.Description + " " + string.Join(",", e.Details?.Select(d => d.Key + "=" + d.Value) ?? [])));
        everything.Should().NotContain("File-Secret-8").And.NotContain("Env-Secret-8").And.NotContain("Typed-Secret-8");
        audit.GetRecent(100).Should().Contain(e => e.EventType == AuditEventType.ConfigurationSaveFailed);
    }

    /// <summary>Failed writes (validation and I/O) leave the base-file secret unchanged.</summary>
    [Fact]
    public async Task FailedWrites_LeaveBaseFileSecretUnchanged()
    {
        using var fixture = new ConfigurationFixture(
            FileWithSharedSecret("File-Secret-9"),
            new Dictionary<string, string?> { [SharedSecretKey] = "Env-Secret-9" });
        var store = new FaultInjectingFileStore(new AppsettingsConfigurationFileStore(fixture.ContentRoot))
        {
            FailNextWrite = new IOException("injected")
        };
        var service = fixture.CreateService(store: store);
        var before = fixture.ReadFile();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigPaths.Set(edit, "authentication radius primary-server", "shared-secret", JsonValue.Create("Operator-Secret-9"));

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Failed);
        fixture.ReadFile().Should().Be(before);

        ((JsonObject)((JsonArray)ConfigPaths.Node(edit.Candidate, "authentication local")["LocalUsers"]!)[0]!)["PasswordHash"] = string.Empty;
        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.ValidationFailed);

        fixture.ReadFile().Should().Be(before);
        FileSharedSecret(fixture).Should().Be("File-Secret-9");
    }
}
