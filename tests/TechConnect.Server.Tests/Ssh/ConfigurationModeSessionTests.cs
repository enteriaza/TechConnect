// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationModeSessionTests.cs
//
// Purpose:
// End-to-end SSH CLI session tests for configuration mode over an in-memory
// duplex pipe: privilege enforcement, prompts, transitions, hidden secret
// input, candidate lifecycle (discard on exit/end/disconnect/cancellation),
// operator isolation, and write persistence.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Configuration.Management;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Configuration-mode SSH session tests.
/// </summary>
public sealed class ConfigurationModeSessionTests : IDisposable
{
    /// <summary>Temp appsettings.json fixture.</summary>
    private readonly ConfigurationFixture _fixture = new();

    /// <summary>Shared audit store.</summary>
    private readonly InMemoryAuditEventStore _audit = CliTestFactory.CreateAudit();

    /// <summary>Capturing logger for the management service.</summary>
    private readonly CapturingLogger<ConfigurationManagementService> _logger = new();

    /// <summary>Service under test (shared by all sessions in a test).</summary>
    private readonly ConfigurationManagementService _service;

    /// <summary>Creates the service.</summary>
    public ConfigurationModeSessionTests() => _service = _fixture.CreateService(logger: _logger, audit: _audit);

    /// <inheritdoc />
    public void Dispose() => _fixture.Dispose();

    /// <summary>Configured SSH port in the shipped file.</summary>
    private int ShippedPort() => JsonNode.Parse(_fixture.ReadFile(), documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!
        ["TechConnectServer"]!["Ssh"]!["Port"]!.GetValue<int>();

    /// <summary>Prompts and mode transitions; exit/end discard the candidate with a warning.</summary>
    [Fact]
    public async Task Prompts_Transitions_And_EndDiscards()
    {
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        cli.Output.Should().Contain(ConfigurationModeProcessor.EntryMessage);
        await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await cli.SendAndWaitAsync("port 2299", "TechConnect(config-ssh)#", 2);
        await cli.SendAndWaitAsync("exit", "TechConnect(config)#", 2);
        await cli.SendAndWaitAsync("show configuration changes", "TechConnect(config)#", 3);
        cli.Output.Should().Contain("ssh port");
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().Contain(ConfigurationModeProcessor.DiscardedMessage);
        _fixture.ReadFile().Should().Be(before);
        await cli.SendAndWaitAsync("configure", "TechConnect(config)#", 4);
        await cli.SendAndWaitAsync("show configuration changes", "TechConnect(config)#", 5);
        await cli.SendAndWaitAsync("exit", "TechConnect#", 3);
        cli.Count(ConfigurationModeProcessor.DiscardedMessage).Should().Be(1, "the second candidate had no changes");
        _audit.GetRecent(50).Should().Contain(e => e.EventType == AuditEventType.ConfigurationDiscarded);
    }

    /// <summary>write persists; end afterwards does not warn; show startup-config reflects the file.</summary>
    [Fact]
    public async Task Write_Persists_And_ShowViewsReflectIt()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("conf t", "TechConnect(config)#");
        await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await cli.SendAndWaitAsync("port 2298", "TechConnect(config-ssh)#", 2);
        await cli.SendAndWaitAsync("write memory", "TechConnect(config-ssh)#", 3);
        cli.Output.Should().Contain("[OK]").And.Contain("Configuration saved.").And.Contain("% Server restart required for:");
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);
        await cli.SendAndWaitAsync("show startup-config", "TechConnect#", 3);
        await cli.SendAndWaitAsync("show running-config", "TechConnect#", 4);

        cli.Output.Should().NotContain(ConfigurationModeProcessor.DiscardedMessage);
        ShippedPort().Should().Be(2298);
        cli.Output.Should().Contain("port 2298");
        cli.Output.Should().Contain("Saved changes awaiting a server restart");
    }

    /// <summary>write outside configuration mode never persists and never prints [OK].</summary>
    [Fact]
    public async Task WriteOutsideConfigurationMode_DoesNotPersist()
    {
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("write", "TechConnect#", 2);

        cli.Output.Should().Contain("% No unsaved configuration changes").And.NotContain("[OK]");
        _fixture.ReadFile().Should().Be(before);
    }

    /// <summary>Below privilege 15, configure/write/show *-config are refused and the prompt stays user EXEC.</summary>
    [Fact]
    public async Task LowPrivilege_CannotEnterOrUseConfiguration()
    {
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(1));

        await cli.WaitForAsync("TechConnect>");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect>", 2);
        await cli.SendAndWaitAsync("write memory", "TechConnect>", 3);
        await cli.SendAndWaitAsync("show running-config", "TechConnect>", 4);
        await cli.SendAndWaitAsync("show startup-config", "TechConnect>", 5);

        cli.Output.Should().NotContain("TechConnect(config)#")
            .And.NotContain(ConfigurationModeProcessor.EntryMessage)
            .And.NotContain("Building configuration")
            .And.NotContain("startup-config (appsettings.json)");
        _fixture.ReadFile().Should().Be(before);
        _audit.GetRecent(100).Should().NotContain(e => e.EventType == AuditEventType.ConfigurationModeEntered);
    }

    /// <summary>Privilege 14 is also refused (the boundary is exactly 15).</summary>
    [Fact]
    public async Task Privilege14_CannotEnterConfiguration()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(14));

        await cli.WaitForAsync("TechConnect>");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect>", 2);

        cli.Output.Should().NotContain("TechConnect(config)#");
    }

    /// <summary>Hidden secret entry: never echoed, masked in every view, stored, and absent from logs and audit.</summary>
    [Fact]
    public async Task HiddenSecret_NotEchoed_Masked_Stored_NotLoggedOrAudited()
    {
        const string canary = "Sh4red-Secret-Canary";
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await NavigateToRadiusPrimaryAsync(cli);
        await cli.SendAndWaitAsync("shared-secret", "Enter secret: ");
        await cli.SendAndWaitAsync(canary, "Confirm secret: ");
        await cli.SendAndWaitAsync(canary, "TechConnect(config-radius-server)#", 2);
        await cli.SendAndWaitAsync("show configuration changes", "TechConnect(config-radius-server)#", 3);
        await cli.SendAndWaitAsync("show candidate-config", "TechConnect(config-radius-server)#", 4);
        await cli.SendAndWaitAsync("write", "TechConnect(config-radius-server)#", 5);
        await cli.SendAndWaitAsync("show running-config", "TechConnect(config-radius-server)#", 6);
        await cli.SendAndWaitAsync("show startup-config", "TechConnect(config-radius-server)#", 7);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().NotContain(canary);
        cli.Output.Should().Contain("shared-secret <hidden>").And.Contain("[OK]");
        cli.Output.Should().MatchRegex(@"\* authentication radius primary-server shared-secret <(set|changed)>");
        JsonNode.Parse(_fixture.ReadFile())!["TechConnectServer"]!["Authentication"]!["Radius"]!["PrimaryServer"]!["SharedSecret"]!
            .GetValue<string>().Should().Be(canary);
        _logger.AllText.Should().NotContain(canary);
        AuditText().Should().NotContain(canary);
    }

    /// <summary>Password entry stores an Identity V3 hash (never the plaintext) and mismatched confirmation changes nothing.</summary>
    [Fact]
    public async Task PasswordPrompt_StoresHash_MismatchChangesNothing()
    {
        const string password = "Op3rator-Pass-Canary";
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#");
        await cli.SendAndWaitAsync("username newop privilege 5", "Enter password: ");
        await cli.SendAndWaitAsync(password, "Confirm password: ");
        await cli.SendAndWaitAsync("different", "% Entries do not match. 'username newop' was not created.");
        await cli.SendAndWaitAsync("username newop privilege 5", "Enter password: ", 2);
        await cli.SendAndWaitAsync(password, "Confirm password: ", 2);
        await cli.SendAndWaitAsync(password, "TechConnect(config-auth-local)#", 3);
        await cli.SendAndWaitAsync("username newop password", "Enter password: ", 3);
        await cli.SendAndWaitAsync("Zq-Mismatch-1", "Confirm password: ", 3);
        await cli.SendAndWaitAsync("Zq-Mismatch-2", "% Entries do not match. Value not changed.");
        await cli.SendAndWaitAsync("write", "TechConnect(config-auth-local)#", 5);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().NotContain(password).And.NotContain("different").And.NotContain("Zq-Mismatch");
        var users = JsonNode.Parse(_fixture.ReadFile())!["TechConnectServer"]!["Authentication"]!["Local"]!["LocalUsers"]!.AsArray();
        var stored = users.Single(u => u!["Username"]!.GetValue<string>() == "newop")!["PasswordHash"]!.GetValue<string>();
        stored.Should().NotBe(password).And.NotBeNullOrEmpty();
        new TechConnect.Server.Authentication.Local.OperatorPasswordHasher().VerifyPassword("newop", stored, password).Should().BeTrue();
        _logger.AllText.Should().NotContain(password).And.NotContain(stored);
        AuditText().Should().NotContain(password).And.NotContain(stored);
    }

    /// <summary>Ctrl+C at the hidden prompt cancels cleanly and leaves the candidate unchanged.</summary>
    [Fact]
    public async Task SecretPrompt_CtrlC_CancelsCleanly()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await NavigateToRadiusPrimaryAsync(cli);
        await cli.SendAndWaitAsync("shared-secret", "Enter secret: ");
        await cli.SendRawAsync("zq9x\u0003");
        await cli.WaitForAsync("% Cancelled. Value not changed.");
        await cli.WaitForAsync("TechConnect(config-radius-server)#", 2);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().NotContain("zq9x").And.NotContain(ConfigurationModeProcessor.DiscardedMessage);
    }

    /// <summary>A plaintext secret on the command line is rejected and never stored.</summary>
    [Fact]
    public async Task PlaintextSecretOnCommandLine_IsRejected()
    {
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await NavigateToRadiusPrimaryAsync(cli);
        await cli.SendAndWaitAsync("shared-secret Inline-Value", "TechConnect(config-radius-server)#", 2);
        await cli.SendAndWaitAsync("show configuration changes", "TechConnect(config-radius-server)#", 3);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().Contain("secrets are not accepted on the command line");
        cli.Output.Should().NotContain(ConfigurationModeProcessor.DiscardedMessage, "the rejected line changed nothing");
        _fixture.ReadFile().Should().Be(before);
        _logger.AllText.Should().NotContain("Inline-Value");
    }

    /// <summary>
    /// <c>do show</c> runs EXEC show commands (through the dispatcher) from inside a sub-mode; <c>do</c> never
    /// runs configuration-changing or persisting commands, and the candidate stays unsaved.
    /// </summary>
    [Fact]
    public async Task Do_RunsExecShow_RejectsOthers_StaysInMode()
    {
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await cli.SendAndWaitAsync("port 2294", "TechConnect(config-ssh)#", 2);
        cli.Output.Should().NotContain("SSH operator");
        await cli.SendAndWaitAsync("do show users", "TechConnect(config-ssh)#", 3);
        cli.Output.Should().Contain("SSH operator", "the EXEC show users output was produced");
        await cli.SendAndWaitAsync("do show configuration changes", "TechConnect(config-ssh)#", 4);
        cli.Output.Should().Contain("ssh port");
        await cli.SendAndWaitAsync("do show startup-config", "TechConnect(config-ssh)#", 5);
        await cli.SendAndWaitAsync("do write memory", "TechConnect(config-ssh)#", 6);
        await cli.SendAndWaitAsync("do configure terminal", "TechConnect(config-ssh)#", 7);

        cli.Count("'do' runs read-only EXEC 'show' commands only").Should().Be(2);
        cli.Output.Should().NotContain("[OK]").And.NotContain("Building configuration");
        _fixture.ReadFile().Should().Be(before);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);
        cli.Output.Should().Contain(ConfigurationModeProcessor.DiscardedMessage);
    }

    /// <summary><c>exit</c> climbs one level at a time; <c>end</c> returns to privileged EXEC from any depth.</summary>
    [Fact]
    public async Task Navigation_ExitClimbsOneLevel_EndReturnsToExec()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#");
        await cli.SendAndWaitAsync("username admin", "TechConnect(config-user)#");
        await cli.SendAndWaitAsync("exit", "TechConnect(config-auth-local)#", 2);
        await cli.SendAndWaitAsync("exit", "TechConnect(config-auth)#", 2);
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#", 3);
        await cli.SendAndWaitAsync("username admin", "TechConnect(config-user)#", 2);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);

        cli.Output.Should().NotContain(ConfigurationModeProcessor.DiscardedMessage, "navigation alone changes nothing");
    }

    /// <summary><c>?</c> shows context help without executing; Tab completes in place; the line is kept intact.</summary>
    [Fact]
    public async Task HelpAndTab_OverSsh_PreserveLine_AndNeverExecute()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#");
        await cli.SendRawAsync("username newop ?");
        await cli.WaitForAsync("Privilege level of the new account (required)");
        cli.Output.Should().NotContain("Enter password: ");
        await cli.SendRawAsync("\u0015username adm\t");
        await cli.WaitForAsync("username admin ");
        await cli.SendRawAsync("priv\t9\r");
        await cli.SendAndWaitAsync("do show configuration changes", "username admin privilege-level");

        cli.Output.Should().NotContain("newop privilege-level", "help never creates or changes anything");
        cli.Output.Should().NotContain("% Invalid input").And.NotContain("% Incomplete");
    }

    /// <summary>Hidden password entry and secret-bearing lines never reach the screen or the command history.</summary>
    [Fact]
    public async Task SecretLines_NotEchoed_NotRecalledFromHistory()
    {
        const string password = "Hist0ry-Pass-Canary";
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#");
        await cli.SendAndWaitAsync("username newop privilege 5", "Enter password: ");
        await cli.SendAndWaitAsync(password, "Confirm password: ");
        await cli.SendAndWaitAsync(password, "TechConnect(config-auth-local)#", 2);
        await cli.SendAndWaitAsync("username newop password Inline-Secret-X", "secrets are not accepted on the command line");
        await cli.WaitForAsync("TechConnect(config-auth-local)#", 3);
        await cli.SendRawAsync("\u001b[A");
        await cli.WaitForAsync("username newop privilege 5", 2);
        await cli.SendRawAsync("\u001b[A");
        await cli.WaitForAsync("TechConnect(config-auth-local)# local");
        await cli.SendRawAsync("\u0015");

        cli.Count("Inline-Secret-X").Should().Be(1, "only the typed echo; the line was dropped from history");
        cli.Output.Should().NotContain(password);
        _logger.AllText.Should().NotContain(password).And.NotContain("Inline-Secret-X");
        AuditText().Should().NotContain(password).And.NotContain("Inline-Secret-X");
    }

    /// <summary>
    /// Privilege-specific enable secrets over SSH: hidden and confirmed, Ctrl+C creates nothing, the hash form is
    /// dropped from history, views stay masked, and the session never leaves (config-auth-local).
    /// </summary>
    [Fact]
    public async Task EnableSecretLevel_HiddenPrompt_Cancel_HistoryAndMasking()
    {
        const string secret = "L3vel-Secret-Canary";
        var hash = new TechConnect.Server.Authentication.Local.OperatorPasswordHasher().HashEnableSecret("Other-Level-Secret");
        var before = _fixture.ReadFile();
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("local", "TechConnect(config-auth-local)#");
        await cli.SendAndWaitAsync("enable-secret-level 6", "Enter secret: ");
        await cli.SendRawAsync("zq-abort\u0003");
        await cli.WaitForAsync("% Cancelled. 'enable-secret-level 6' was not created.");
        await cli.WaitForAsync("TechConnect(config-auth-local)#", 2);
        await cli.SendAndWaitAsync("enable-secret-level 5", "Enter secret: ", 2);
        await cli.SendAndWaitAsync(secret, "Confirm secret: ");
        await cli.SendAndWaitAsync(secret, "TechConnect(config-auth-local)#", 3);
        await cli.SendAndWaitAsync("enable-secret-level 7 hash " + hash, "TechConnect(config-auth-local)#", 4);
        await cli.SendRawAsync("\u001b[A");
        await cli.WaitForAsync("enable-secret-level 5", 2);
        await cli.SendRawAsync("\u0015");
        await cli.SendAndWaitAsync("do show candidate-config", "enable-secret-level 7 <hidden>");

        cli.Output.Should().Contain("enable-secret-level 5 <hidden>").And.NotContain("enable-secret-level 6 ")
            .And.NotContain("TechConnect(config-enable)#");
        cli.Count(hash).Should().Be(1, "only the typed echo; the hash line was dropped from history and is masked in views");
        cli.Output.Should().NotContain(secret).And.NotContain("zq-abort");
        _fixture.ReadFile().Should().Be(before, "nothing is persisted without write");
        _logger.AllText.Should().NotContain(secret).And.NotContain(hash);
        AuditText().Should().NotContain(secret).And.NotContain(hash);
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);
        cli.Output.Should().Contain(ConfigurationModeProcessor.DiscardedMessage);
    }

    /// <summary>Enters configuration mode and navigates to the RADIUS primary-server sub-mode.</summary>
    private static async Task NavigateToRadiusPrimaryAsync(CliHarness cli)
    {
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("authentication", "TechConnect(config-auth)#");
        await cli.SendAndWaitAsync("radius", "TechConnect(config-auth-radius)#");
        await cli.SendAndWaitAsync("primary-server", "TechConnect(config-radius-server)#");
    }

    /// <summary>Disconnecting with unsaved changes discards them (audited with the operator context) and never persists.</summary>
    [Fact]
    public async Task Disconnect_DiscardsUnsavedChanges()
    {
        var before = _fixture.ReadFile();
        var identity = TestAuthenticatedOperator.Create("dropper", PrivilegeLevel.FromInt(15), operatorSessionId: "drop-1");
        await using (var cli = await CliHarness.StartAsync(_service, _audit, identity))
        {
            await cli.WaitForAsync("TechConnect#");
            await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
            await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
            await cli.SendAndWaitAsync("port 2297", "TechConnect(config-ssh)#", 2);
            await cli.DisconnectAsync();
        }

        _fixture.ReadFile().Should().Be(before);
        _audit.GetRecent(50).Should().Contain(e =>
            e.EventType == AuditEventType.ConfigurationDiscarded &&
            e.ActorUsername == "dropper" &&
            e.SessionId == "drop-1");
    }

    /// <summary>Session cancellation (server shutdown) discards unsaved changes and never persists.</summary>
    [Fact]
    public async Task Cancellation_DiscardsUnsavedChanges()
    {
        var before = _fixture.ReadFile();
        await using (var cli = await CliHarness.StartAsync(_service, _audit, Level(15)))
        {
            await cli.WaitForAsync("TechConnect#");
            await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
            await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
            await cli.SendAndWaitAsync("port 2296", "TechConnect(config-ssh)#", 2);
            await cli.CancelAsync();
        }

        _fixture.ReadFile().Should().Be(before);
        _audit.GetRecent(50).Should().Contain(e => e.EventType == AuditEventType.ConfigurationDiscarded);
    }

    /// <summary>Two operators: neither sees the other's candidate; a stale candidate cannot overwrite a newer save.</summary>
    [Fact]
    public async Task TwoOperators_AreIsolated_AndStaleWriteConflicts()
    {
        var originalPort = ShippedPort();
        await using var alice = await CliHarness.StartAsync(_service, _audit, TestAuthenticatedOperator.Create("alice", PrivilegeLevel.FromInt(15)));
        await using var bob = await CliHarness.StartAsync(_service, _audit, TestAuthenticatedOperator.Create("bob", PrivilegeLevel.FromInt(15)));

        await alice.WaitForAsync("TechConnect#");
        await bob.WaitForAsync("TechConnect#");
        await alice.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await bob.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await alice.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await bob.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await alice.SendAndWaitAsync("port 2295", "TechConnect(config-ssh)#", 2);
        await bob.SendAndWaitAsync("show configuration changes", "TechConnect(config-ssh)#", 2);
        await bob.SendAndWaitAsync("show candidate-config", "TechConnect(config-ssh)#", 3);
        bob.Output.Should().NotContain("2295");

        await bob.SendAndWaitAsync("maximum-concurrent-connections 9", "TechConnect(config-ssh)#", 4);
        await alice.SendAndWaitAsync("write", "TechConnect(config-ssh)#", 3);
        alice.Output.Should().Contain("[OK]");
        await bob.SendAndWaitAsync("write", "TechConnect(config-ssh)#", 5);

        bob.Output.Should().Contain("was modified after this configuration session began").And.NotContain("[OK]");
        var ssh = JsonNode.Parse(_fixture.ReadFile())!["TechConnectServer"]!["Ssh"]!;
        ssh["Port"]!.GetValue<int>().Should().Be(2295).And.NotBe(originalPort);
        ssh["MaximumConcurrentConnections"]!.GetValue<int>().Should().NotBe(9);

        await bob.SendAndWaitAsync("end", "TechConnect#", 2);
        await bob.SendAndWaitAsync("configure terminal", "TechConnect(config)#", 2);
        await bob.SendAndWaitAsync("show candidate-config", "TechConnect(config)#", 3);
        bob.Output.Should().Contain("port 2295", "a new candidate starts from the latest startup-config");
    }

    /// <summary>Invalid configuration lines produce Cisco-style errors and stay in configuration mode.</summary>
    [Fact]
    public async Task InvalidInput_ReportsError_StaysInMode()
    {
        await using var cli = await CliHarness.StartAsync(_service, _audit, Level(15));

        await cli.WaitForAsync("TechConnect#");
        await cli.SendAndWaitAsync("configure terminal", "TechConnect(config)#");
        await cli.SendAndWaitAsync("bogus", "TechConnect(config)#", 2);
        await cli.SendAndWaitAsync("ssh", "TechConnect(config-ssh)#");
        await cli.SendAndWaitAsync("port 70000", "TechConnect(config-ssh)#", 2);
        await cli.SendAndWaitAsync("port", "TechConnect(config-ssh)#", 3);

        cli.Output.Should().Contain("% Invalid input detected at '^' marker.").And.Contain("% Incomplete command.");
        await cli.SendAndWaitAsync("end", "TechConnect#", 2);
        cli.Output.Should().NotContain(ConfigurationModeProcessor.DiscardedMessage);
    }

    /// <summary>Creates an identity at a level.</summary>
    private static AuthenticatedOperator Level(int level) =>
        TestAuthenticatedOperator.Create("op" + level, PrivilegeLevel.FromInt(level));

    /// <summary>Flattened audit text for leak checks.</summary>
    private string AuditText() => string.Join(
        "\n",
        _audit.GetRecent(1000).Select(e => e.Description + " " + string.Join(",", e.Details?.Select(d => d.Key + "=" + d.Value) ?? [])));

    /// <summary>
    /// One interactive CLI session over an in-memory pipe.
    /// </summary>
    private sealed class CliHarness : IAsyncDisposable
    {
        /// <summary>Pipe.</summary>
        private readonly DuplexMemoryPipe _pipe = new();

        /// <summary>Client stream.</summary>
        private readonly Stream _client;

        /// <summary>Session lifetime.</summary>
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

        /// <summary>Captured output.</summary>
        private readonly StringBuilder _output = new();

        /// <summary>Session task.</summary>
        private Task _run = Task.CompletedTask;

        /// <summary>Reader task.</summary>
        private Task _reader = Task.CompletedTask;

        /// <summary>Creates the harness.</summary>
        private CliHarness() => _client = _pipe.Client;

        /// <summary>Captured output so far.</summary>
        public string Output
        {
            get
            {
                lock (_output)
                {
                    return _output.ToString();
                }
            }
        }

        /// <summary>Starts a session for <paramref name="identity"/>.</summary>
        public static Task<CliHarness> StartAsync(ConfigurationManagementService service, InMemoryAuditEventStore audit, AuthenticatedOperator identity)
        {
            var harness = new CliHarness();
            var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
                CliTestFactory.CreateDispatcher(audit: audit, configuration: service);
            var session = new TechConnectCliSession(
                harness._pipe.Server,
                dispatcher,
                attachments,
                registry,
                tracker,
                serialCache,
                pendingBaud,
                authorization,
                disconnect,
                NullLogger.Instance,
                identity,
                enable,
                audit: audit,
                configurationManagement: service);
            harness._reader = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (true)
                {
                    int n;
                    try
                    {
                        n = await harness._client.ReadAsync(buffer, harness._cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    if (n == 0)
                    {
                        break;
                    }

                    lock (harness._output)
                    {
                        harness._output.Append(Encoding.UTF8.GetString(buffer, 0, n));
                    }
                }
            });
            harness._run = session.RunAsync(harness._cts.Token);
            return Task.FromResult(harness);
        }

        /// <summary>Occurrences of <paramref name="text"/> in the output.</summary>
        public int Count(string text)
        {
            var output = Output;
            var count = 0;
            for (var i = output.IndexOf(text, StringComparison.Ordinal); i >= 0; i = output.IndexOf(text, i + text.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        /// <summary>Waits until <paramref name="text"/> has appeared at least <paramref name="occurrences"/> times.</summary>
        public async Task WaitForAsync(string text, int occurrences = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count(text) < occurrences)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Timed out waiting for '{text}' x{occurrences}. Output:\n{Output}");
                }

                await Task.Delay(10);
            }
        }

        /// <summary>Sends raw bytes.</summary>
        public Task SendRawAsync(string text) => _client.WriteAsync(Encoding.UTF8.GetBytes(text), _cts.Token).AsTask();

        /// <summary>Sends a line and waits for the expected text.</summary>
        public async Task SendAndWaitAsync(string line, string expect, int occurrences = 1)
        {
            await SendRawAsync(line + "\r");
            await WaitForAsync(expect, occurrences);
        }

        /// <summary>Simulates a client disconnect (EOF) and waits for the session to end.</summary>
        public async Task DisconnectAsync()
        {
            await _pipe.DisposeAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>Cancels the session (server shutdown) and waits for it to end.</summary>
        public async Task CancelAsync()
        {
            await _cts.CancelAsync();
            try
            {
                await _run.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (!_run.IsCompleted)
            {
                try
                {
                    await SendRawAsync("end\rexit\r");
                }
                catch
                {
                }

                try
                {
                    await _run.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            await _cts.CancelAsync();
            await _pipe.DisposeAsync();
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _cts.Dispose();
        }
    }
}
