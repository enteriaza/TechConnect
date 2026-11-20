// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationManagementServiceTests.cs
//
// Purpose:
// Candidate isolation, validated atomic persistence, conflict detection,
// failure/cancellation safety, runtime application, restart reporting, and
// secret-free logging for ConfigurationManagementService.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Tests for <see cref="ConfigurationManagementService"/>.
/// </summary>
public sealed class ConfigurationManagementServiceTests : IDisposable
{
    /// <summary>Fixture.</summary>
    private readonly ConfigurationFixture _fixture = new();

    /// <inheritdoc />
    public void Dispose() => _fixture.Dispose();

    /// <summary>Finds a schema setting by section path and keyword.</summary>
    private static ConfigSetting Setting(string sectionKeyword, string keyword) => ConfigPaths.Setting(sectionKeyword, keyword);

    /// <summary>Finds a section by space-separated keyword path.</summary>
    private static ConfigSection Section(string path) => ConfigPaths.Section(path);

    /// <summary>Candidate object for a section.</summary>
    private static JsonObject Node(JsonObject model, string path) => ConfigPaths.Node(model, path);

    /// <summary>Sets a scalar in the candidate.</summary>
    private static void Set(ConfigurationEditSession edit, string section, string keyword, JsonNode? value) =>
        ConfigPaths.Set(edit, section, keyword, value);

    /// <summary>Editing a candidate never changes the effective configuration, the file, or another candidate.</summary>
    [Fact]
    public void Candidate_IsPrivate_AndDoesNotTouchLiveConfigurationOrFile()
    {
        var service = _fixture.CreateService();
        var before = _fixture.ReadFile();
        var first = service.BeginEdit(ConfigurationFixture.Admin("a"), out _);
        var second = service.BeginEdit(ConfigurationFixture.Admin("b"), out _);

        Set(first, "ssh", "port", JsonValue.Create(2200L));

        first.HasUnsavedChanges.Should().BeTrue();
        second.HasUnsavedChanges.Should().BeFalse();
        _fixture.Root["TechConnectServer:Ssh:Port"].Should().Be("2222");
        _fixture.ReadFile().Should().Be(before);
        service.RenderRunning().Should().Contain("port 2222").And.NotContain("port 2200");
    }

    /// <summary>A valid write merges into the file, preserves unrelated content, and reports restart-required settings.</summary>
    [Fact]
    public async Task Write_Valid_PersistsMergedFile_PreservesUnrelatedContent_ReportsRestart()
    {
        var original = JsonNode.Parse(_fixture.ReadFile())!.AsObject();
        original["Unrelated"] = new JsonObject { ["Keep"] = "me" };
        original["TechConnectServer"]!["Ssh"]!["AuthorizedPublicKeys"] = new JsonArray();
        File.WriteAllText(_fixture.FilePath, original.ToJsonString());
        _fixture.Root.Reload();
        var service = _fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "ssh", "port", JsonValue.Create(2200L));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.RestartRequired.Should().Contain("ssh port");
        edit.HasUnsavedChanges.Should().BeFalse();
        var saved = JsonNode.Parse(_fixture.ReadFile())!;
        saved["TechConnectServer"]!["Ssh"]!["Port"]!.GetValue<long>().Should().Be(2200);
        saved["Unrelated"]!["Keep"]!.GetValue<string>().Should().Be("me");
        saved["TechConnectServer"]!["Ssh"]!["AuthorizedPublicKeys"].Should().NotBeNull("unmanaged properties are preserved");
        Directory.GetFiles(_fixture.ContentRoot, "*.tmp").Should().BeEmpty();

        // Restart-required: the running view keeps the old value and lists the pending change.
        var running = service.RenderRunning();
        running.Should().Contain("port 2222");
        running.Should().Contain("Saved changes awaiting a server restart").And.Contain("ssh port");
    }

    /// <summary>Refused writes (validation and conflict) leave running, startup, and pending-restart views unchanged.</summary>
    [Fact]
    public async Task RefusedWrites_LeaveAllViewsUnchanged()
    {
        var service = _fixture.CreateService();
        var running = service.RenderRunning();
        var startup = service.RenderStartup();
        var invalid = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(invalid, "ssh", "port", JsonValue.Create(2200L));
        ((JsonObject)((JsonArray)Node(invalid.Candidate, "authentication local")["LocalUsers"]!)[0]!)["PasswordHash"] = string.Empty;
        var stale = service.BeginEdit(ConfigurationFixture.Admin("other"), out _);
        Set(stale, "ssh", "port", JsonValue.Create(2300L));

        (await service.WriteAsync(invalid, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.ValidationFailed);
        File.WriteAllText(_fixture.FilePath, _fixture.ReadFile() + " ");
        _fixture.Root.Reload();
        (await service.WriteAsync(stale, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Conflict);

        service.RenderRunning().Should().Be(running);
        service.RenderStartup().Should().Be(startup);
        service.GetPendingRestart().Should().BeEmpty();
        stale.HasUnsavedChanges.Should().BeTrue();
    }

    /// <summary>After a successful write: startup shows the change, running does not (restart-required), pending lists it, and the candidate is clean.</summary>
    [Fact]
    public async Task SuccessfulWrite_ViewsAreConsistent()
    {
        var service = _fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "ssh", "port", JsonValue.Create(2244L));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.RestartRequired.Should().Contain("ssh port");
        result.AppliedAtRuntime.Should().NotContain("ssh port");
        edit.HasUnsavedChanges.Should().BeFalse();
        ConfigurationModel.Diff(edit.Baseline, edit.Candidate).Should().BeEmpty();
        service.RenderStartup().Should().Contain("port 2244");
        service.GetPendingRestart().Should().Contain(l => l.Contains("ssh port", StringComparison.Ordinal));
        var runningBody = service.RenderRunning().Split("! Saved changes awaiting a server restart")[0];
        runningBody.Should().NotContain("port 2244");
    }

    /// <summary>A runtime-applicable change whose service is unavailable is saved, reported as not applied, and never claimed as applied.</summary>
    [Fact]
    public async Task RuntimeApplyUnavailable_IsReportedAccurately()
    {
        var service = _fixture.CreateService(dns: null);
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.SetValue(Node(edit.Candidate, "dns"), Setting("dns", "servers"), new JsonArray("192.0.2.99"));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.RuntimeApplyFailures.Should().Contain("dns servers");
        result.AppliedAtRuntime.Should().NotContain("dns servers");
        result.RestartRequired.Should().NotContain("dns servers");
        ConfigurationModeProcessorText(result).Should().Contain("% Saved but not applied at runtime (takes effect after restart): dns servers");
        service.GetPendingRestart().Should().Contain(l => l.Contains("dns servers", StringComparison.Ordinal));
        _fixture.ReadFile().Should().Contain("192.0.2.99");
    }

    /// <summary>Formats a result as the CLI would.</summary>
    private static string ConfigurationModeProcessorText(ConfigurationWriteResult result) =>
        TechConnect.Server.Ssh.Cli.ConfigurationModeProcessor.FormatWriteResult(result);

    /// <summary>A candidate failing the real startup validators is refused; file and running config are unchanged.</summary>
    [Fact]
    public async Task Write_InvalidCandidate_IsRefused_FileAndRunningUnchanged()
    {
        var service = _fixture.CreateService();
        var before = _fixture.ReadFile();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        var users = (JsonArray)Node(edit.Candidate, "authentication local")["LocalUsers"]!;
        ((JsonObject)users[0]!)["PasswordHash"] = string.Empty;

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.ValidationFailed);
        result.Messages[0].Should().Be("% Configuration validation failed. Configuration not saved.");
        result.Messages.Should().Contain(m => m.Contains("neither a PasswordHash nor AuthorizedPublicKeys", StringComparison.Ordinal));
        _fixture.ReadFile().Should().Be(before);
        edit.HasUnsavedChanges.Should().BeTrue("a failed write keeps the candidate unsaved");
    }

    /// <summary>An external change to appsettings.json after entering configuration mode is not overwritten.</summary>
    [Fact]
    public async Task Write_AfterExternalFileChange_IsRefusedAsConflict()
    {
        var service = _fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "ssh", "port", JsonValue.Create(2200L));
        var external = _fixture.ReadFile().Replace("\"MaximumEvents\": 10000", "\"MaximumEvents\": 500", StringComparison.Ordinal);
        File.WriteAllText(_fixture.FilePath, external);

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Conflict);
        _fixture.ReadFile().Should().Be(external);
        edit.HasUnsavedChanges.Should().BeTrue();
    }

    /// <summary>Two operators: the second writer is refused rather than silently overwriting the first.</summary>
    [Fact]
    public async Task ConcurrentOperators_SecondWriteIsRefused()
    {
        var service = _fixture.CreateService();
        var first = service.BeginEdit(ConfigurationFixture.Admin("a"), out _);
        var second = service.BeginEdit(ConfigurationFixture.Admin("b"), out _);
        Set(first, "ssh", "port", JsonValue.Create(2200L));
        Set(second, "ssh", "maximum-concurrent-connections", JsonValue.Create(7L));

        (await service.WriteAsync(first, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);
        (await service.WriteAsync(second, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Conflict);

        var saved = JsonNode.Parse(_fixture.ReadFile())!["TechConnectServer"]!["Ssh"]!;
        saved["Port"]!.GetValue<long>().Should().Be(2200);
        saved["MaximumConcurrentConnections"]!.GetValue<long>().Should().Be(50);
    }

    /// <summary>Writes are serialized: a write waiting on the lock observes the first write's file hash.</summary>
    [Fact]
    public async Task ConcurrentWrites_AreSerialized()
    {
        var store = new FaultInjectingFileStore(new AppsettingsConfigurationFileStore(_fixture.ContentRoot))
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = _fixture.CreateService(store);
        var first = service.BeginEdit(ConfigurationFixture.Admin("a"), out _);
        var second = service.BeginEdit(ConfigurationFixture.Admin("b"), out _);
        Set(first, "ssh", "port", JsonValue.Create(2200L));
        Set(second, "ssh", "port", JsonValue.Create(2300L));

        var firstWrite = service.WriteAsync(first, CancellationToken.None);
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondWrite = service.WriteAsync(second, CancellationToken.None);
        secondWrite.IsCompleted.Should().BeFalse("the second write waits for the first");
        store.WriteGate.SetResult();

        (await firstWrite).Status.Should().Be(ConfigurationWriteStatus.Saved);
        (await secondWrite).Status.Should().Be(ConfigurationWriteStatus.Conflict);
        store.Writes.Should().Be(1);
    }

    /// <summary>An I/O failure leaves the file, running configuration, and saved state unchanged.</summary>
    [Fact]
    public async Task Write_IoFailure_LeavesEverythingUnchanged()
    {
        var store = new FaultInjectingFileStore(new AppsettingsConfigurationFileStore(_fixture.ContentRoot))
        {
            FailNextWrite = new IOException("disk full")
        };
        var dns = new ConfiguredDnsResolver([], TimeSpan.FromSeconds(3), NullLogger<ConfiguredDnsResolver>.Instance);
        var service = _fixture.CreateService(store, dns: dns);
        var before = _fixture.ReadFile();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        var servers = Setting("dns", "servers");
        ConfigurationModel.SetValue(Node(edit.Candidate, "dns"), servers, new JsonArray("192.0.2.53"));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Failed);
        result.Messages.Should().ContainSingle().Which.Should().Contain("existing file is unchanged");
        _fixture.ReadFile().Should().Be(before);
        dns.ConfiguredServers.Should().BeEmpty("runtime settings apply only after persistence succeeds");
        edit.HasUnsavedChanges.Should().BeTrue();

        // Retry succeeds with the same candidate (baseline hash was not advanced).
        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);
        dns.ConfiguredServers.Should().Equal(IPAddress.Parse("192.0.2.53"));
    }

    /// <summary>Cancellation before the replace leaves the file and saved state unchanged.</summary>
    [Fact]
    public async Task Write_Cancelled_LeavesFileUnchanged()
    {
        var store = new FaultInjectingFileStore(new AppsettingsConfigurationFileStore(_fixture.ContentRoot))
        {
            WriteGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = _fixture.CreateService(store);
        var before = _fixture.ReadFile();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "ssh", "port", JsonValue.Create(2200L));
        using var cts = new CancellationTokenSource();

        var write = service.WriteAsync(edit, cts.Token);
        await store.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        var result = await write;

        result.Status.Should().Be(ConfigurationWriteStatus.Cancelled);
        _fixture.ReadFile().Should().Be(before);
        edit.HasUnsavedChanges.Should().BeTrue();
    }

    /// <summary>DNS and audit changes apply to the running services after a successful write.</summary>
    [Fact]
    public async Task Write_RuntimeSettings_AreAppliedToServices()
    {
        var dns = new ConfiguredDnsResolver([], TimeSpan.FromSeconds(3), NullLogger<ConfiguredDnsResolver>.Instance);
        var audit = new InMemoryAuditEventStore(true, 10_000, TimeProvider.System, NullLogger<InMemoryAuditEventStore>.Instance);
        var service = _fixture.CreateService(dns: dns, audit: audit);
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.SetValue(Node(edit.Candidate, "dns"), Setting("dns", "servers"), new JsonArray("192.0.2.53", "2001:db8::53"));
        Set(edit, "dns", "query-timeout-seconds", JsonValue.Create(7L));
        Set(edit, "audit", "maximum-events", JsonValue.Create(5L));

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        result.AppliedAtRuntime.Should().BeEquivalentTo("dns servers", "dns query-timeout-seconds", "audit maximum-events");
        result.RestartRequired.Should().BeEmpty();
        dns.ConfiguredServers.Should().Equal(IPAddress.Parse("192.0.2.53"), IPAddress.Parse("2001:db8::53"));
        dns.QueryTimeout.Should().Be(TimeSpan.FromSeconds(7));
        audit.MaximumEvents.Should().Be(5);
        service.RenderRunning().Should().Contain("maximum-events 5").And.Contain("servers add 192.0.2.53");
        service.GetPendingRestart().Should().BeEmpty();
    }

    /// <summary>
    /// Serilog default level applies through configuration reload; a newly added override
    /// source is reported as restart-required (SerilogRuntimeReloadTests shows why).
    /// </summary>
    [Fact]
    public async Task Write_SerilogLevels_DefaultAppliesAtRuntime_AddedOverrideNeedsRestart()
    {
        using var logger = new LoggerConfiguration().ReadFrom.Configuration(_fixture.Root).CreateLogger();
        var service = _fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "serilog", "minimum-level", JsonValue.Create("Debug"));
        var overrides = (JsonObject)ConfigurationModel.GetValue(Node(edit.Candidate, "serilog"), Setting("serilog", "minimum-level-override"))!;
        overrides["Contoso"] = "Error";
        logger.IsEnabled(LogEventLevel.Debug).Should().BeFalse();

        var result = await service.WriteAsync(edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        logger.IsEnabled(LogEventLevel.Debug).Should().BeTrue();
        result.AppliedAtRuntime.Should().Contain("serilog minimum-level");
        result.RestartRequired.Should().Contain("serilog minimum-level-override Contoso");
        logger.ForContext(Constants.SourceContextPropertyName, "Contoso.App").IsEnabled(LogEventLevel.Information)
            .Should().BeTrue("the added source is not active until restart, as reported");
    }

    /// <summary>Discard and write logs contain paths and counts but never secret values.</summary>
    [Fact]
    public async Task Logs_NeverContainSecretValues()
    {
        var logger = new CapturingLogger<ConfigurationManagementService>();
        var service = _fixture.CreateService(logger: logger);
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        const string sharedSecret = "Rad1us-Sup3r-S3cret";
        var primary = Node(edit.Candidate, "authentication radius primary-server");
        ConfigurationModel.SetValue(primary, Section("authentication radius primary-server").Settings.First(s => s.Keyword == "shared-secret"), JsonValue.Create(sharedSecret));
        var hash = new OperatorPasswordHasher().HashEnableSecret("enable-pass");
        ConfigurationModel.SetValue(Node(edit.Candidate, "authentication local"), Setting("authentication local", "enable-secret"), JsonValue.Create(hash));

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);
        var discarded = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.SetValue(Node(discarded.Candidate, "authentication radius primary-server"), Section("authentication radius primary-server").Settings.First(s => s.Keyword == "shared-secret"), JsonValue.Create("Another-S3cret"));
        service.Discard(discarded, "end").Should().Be(1);

        logger.AllText.Should().NotContain(sharedSecret).And.NotContain("Another-S3cret").And.NotContain(hash).And.NotContain("enable-pass");
        logger.AllText.Should().Contain("authentication radius primary-server shared-secret");
        logger.Entries.Should().Contain(e => e.Message.Contains("Discarded 1 unsaved configuration change(s)", StringComparison.Ordinal));
    }

    /// <summary>Running and startup views mask secrets.</summary>
    [Fact]
    public async Task Views_MaskSecrets()
    {
        var service = _fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigurationModel.SetValue(Node(edit.Candidate, "authentication radius primary-server"), Section("authentication radius primary-server").Settings.First(s => s.Keyword == "shared-secret"), JsonValue.Create("Top-Secret-Value"));
        await service.WriteAsync(edit, CancellationToken.None);

        var startup = service.RenderStartup();
        startup.Should().Contain("shared-secret <hidden>").And.NotContain("Top-Secret-Value");
        startup.Should().Contain("password <hidden>").And.NotContain("AQAAAA");
        service.RenderRunning().Should().NotContain("AQAAAA");
    }

    /// <summary>Validate uses the same validators as write without persisting.</summary>
    [Fact]
    public void Validate_ReportsFailures_WithoutWriting()
    {
        var service = _fixture.CreateService();
        var before = _fixture.ReadFile();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        Set(edit, "authentication radius", "enabled", JsonValue.Create(true));

        var result = service.Validate(edit.Candidate);

        result.IsValid.Should().BeFalse();
        result.Failures.Should().NotBeEmpty();
        _fixture.ReadFile().Should().Be(before);
    }
}
