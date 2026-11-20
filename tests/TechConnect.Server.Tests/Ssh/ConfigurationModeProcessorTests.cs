// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationModeProcessorTests.cs
//
// Purpose:
// Unit tests for the configuration-mode interpreter: per-line privilege
// re-check, navigation, typed validation, secret handling (hidden prompt,
// hash form, plaintext rejection, history removal), and the fixed write target.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Nodes;
using FluentAssertions;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Configuration.Management;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Tests for <see cref="ConfigurationModeProcessor"/>.
/// </summary>
public sealed class ConfigurationModeProcessorTests : IDisposable
{
    /// <summary>Fixture.</summary>
    private readonly ConfigurationFixture _fixture = new();

    /// <summary>Service.</summary>
    private readonly ConfigurationManagementService _service;

    /// <summary>Processor under test.</summary>
    private readonly ConfigurationModeProcessor _processor;

    /// <summary>Level-15 identity.</summary>
    private readonly AuthenticatedOperator _admin = ConfigurationFixture.Admin();

    /// <summary>Edit session.</summary>
    private readonly ConfigurationEditSession _edit;

    /// <summary>Creates the processor and an edit session.</summary>
    public ConfigurationModeProcessorTests()
    {
        _service = _fixture.CreateService();
        _processor = new ConfigurationModeProcessor(_service, CliTestFactory.CreateAuthorization());
        _edit = _service.BeginEdit(_admin, out _);
    }

    /// <inheritdoc />
    public void Dispose() => _fixture.Dispose();

    /// <summary>Runs a line as the admin.</summary>
    private ConfigurationLineResult Run(string line) => _processor.Execute(_edit, _admin, line);

    /// <summary>Every line is re-authorized: a lowered privilege leaves configuration mode with a denial.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void Execute_BelowLevel15_IsDenied_AndLeaves(int level)
    {
        var low = TestAuthenticatedOperator.Create("low", PrivilegeLevel.FromInt(level));

        var result = _processor.Execute(_edit, low, "ssh");

        result.Action.Should().Be(ConfigurationLineAction.Leave);
        result.Output.Should().Contain("Permission denied");
        _edit.Mode.Should().Be("config");
    }

    /// <summary>Navigation changes the prompt; exit pops one level; end leaves.</summary>
    [Fact]
    public void Navigation_Prompts_ExitAndEnd()
    {
        ConfigurationModeProcessor.GetPrompt(_edit).Should().Be("TechConnect(config)#");
        Run("ssh").Action.Should().Be(ConfigurationLineAction.Continue);
        ConfigurationModeProcessor.GetPrompt(_edit).Should().Be("TechConnect(config-ssh)#");
        Run("exit").Action.Should().Be(ConfigurationLineAction.Continue);
        ConfigurationModeProcessor.GetPrompt(_edit).Should().Be("TechConnect(config)#");
        Run("exit").Action.Should().Be(ConfigurationLineAction.Leave);
        Run("end").Action.Should().Be(ConfigurationLineAction.Leave);
    }

    /// <summary>write and write memory request a write; any other argument (for example a path) is rejected.</summary>
    [Theory]
    [InlineData("write", true)]
    [InlineData("write memory", true)]
    [InlineData("wr mem", true)]
    [InlineData(@"write C:\Windows\evil.json", false)]
    [InlineData("write ../appsettings.json", false)]
    [InlineData("write memory extra", false)]
    public void Write_AcceptsOnlyMemoryTarget(string line, bool accepted)
    {
        var result = Run(line);

        if (accepted)
        {
            result.Action.Should().Be(ConfigurationLineAction.Write);
        }
        else
        {
            result.Action.Should().Be(ConfigurationLineAction.Continue);
            result.Output.Should().Contain("% Invalid input detected");
        }
    }

    /// <summary>Typed validation rejects out-of-range and malformed values without changing the candidate.</summary>
    [Theory]
    [InlineData("port 0")]
    [InlineData("port 65536")]
    [InlineData("port abc")]
    [InlineData("listen-address 999.1.1.1")]
    [InlineData("enabled maybe")]
    public void InvalidValues_AreRejected_CandidateUnchanged(string line)
    {
        Run("ssh");
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.Output.Should().StartWith("%").And.NotBeNull();
        _edit.Candidate.ToJsonString().Should().Be(before);
    }

    /// <summary>Unknown and ambiguous keywords produce Cisco-style errors.</summary>
    [Fact]
    public void UnknownAndAmbiguousKeywords()
    {
        Run("bogus").Output.Should().Contain("% Invalid input detected at '^' marker.");
        Run("s").Output.Should().Contain("% Ambiguous command");
    }

    /// <summary>A secret keyword alone requests a hidden prompt; the candidate changes only via ApplySecret.</summary>
    [Fact]
    public void SecretKeyword_RequestsHiddenPrompt()
    {
        Run("authentication");
        Run("radius");
        Run("primary-server");

        var result = Run("shared-secret");

        result.Action.Should().Be(ConfigurationLineAction.PromptSecret);
        result.Secret!.Prompt.Should().Be("Enter secret: ");
        _processor.ApplySecret(result.Secret, "abc", "abd").Should().Contain("do not match");
        _processor.ApplySecret(result.Secret, "", "").Should().Contain("empty value");
        _processor.ApplySecret(result.Secret, "a\u0007b", "a\u0007b").Should().Contain("Invalid value");
        ConfigurationModel.AsString(ConfigurationModel.GetValue(ConfigPaths.Node(_edit.Candidate, "authentication radius primary-server"), ConfigPaths.Setting("authentication radius primary-server", "shared-secret")))
            .Should().BeNullOrEmpty();

        _processor.ApplySecret(result.Secret, "good-secret", "good-secret").Should().BeNull();
        ConfigurationModel.AsString(ConfigurationModel.GetValue(ConfigPaths.Node(_edit.Candidate, "authentication radius primary-server"), ConfigPaths.Setting("authentication radius primary-server", "shared-secret")))
            .Should().Be("good-secret");
    }

    /// <summary>Plaintext on the command line is rejected and marked for history removal.</summary>
    [Theory]
    [InlineData("shared-secret Plain-Text-1")]
    [InlineData("shared-secret two words")]
    public void PlaintextSecret_Rejected_ForgetsHistory(string line)
    {
        Run("authentication");
        Run("radius");
        Run("primary-server");
        var before = _edit.Candidate.ToJsonString();

        var result = Run(line);

        result.Action.Should().Be(ConfigurationLineAction.Continue);
        result.ForgetHistory.Should().BeTrue();
        result.Output.Should().Contain("secrets are not accepted on the command line").And.NotContain("Plain-Text-1");
        _edit.Candidate.ToJsonString().Should().Be(before);
    }

    /// <summary>The enable secret is stored as a verifiable Identity V3 hash; the hash form accepts only well-formed hashes.</summary>
    [Fact]
    public void EnableSecret_StoredAsHash_HashFormValidated()
    {
        Run("authentication");
        Run("local");
        var prompt = Run("enable-secret");
        prompt.Action.Should().Be(ConfigurationLineAction.PromptSecret);
        _processor.ApplySecret(prompt.Secret!, "En4ble!", "En4ble!").Should().BeNull();
        var setting = ConfigPaths.Setting("authentication local", "enable-secret");
        var node = ConfigPaths.Node(_edit.Candidate, "authentication local");
        var stored = ConfigurationModel.AsString(ConfigurationModel.GetValue(node, setting))!;
        var hasher = new OperatorPasswordHasher();
        stored.Should().NotBe("En4ble!");
        hasher.VerifyEnableSecret(stored, "En4ble!").Should().BeTrue();

        var bad = Run("enable-secret hash not-a-hash");
        bad.ForgetHistory.Should().BeTrue();
        bad.Output.Should().Contain("Identity V3 hash is required");
        ConfigurationModel.AsString(ConfigurationModel.GetValue(node, setting)).Should().Be(stored);

        var other = hasher.HashEnableSecret("Other!");
        var good = Run("enable-secret hash " + other);
        good.ForgetHistory.Should().BeTrue();
        good.Output.Should().BeNullOrEmpty();
        ConfigurationModel.AsString(ConfigurationModel.GetValue(node, setting)).Should().Be(other);
    }

    /// <summary>Views and diffs mask secrets (masking, not omission).</summary>
    [Fact]
    public void ShowViews_MaskSecrets()
    {
        Run("authentication");
        Run("radius");
        Run("primary-server");
        _processor.ApplySecret(Run("shared-secret").Secret!, "Mask-Me-123", "Mask-Me-123");

        var candidate = Run("show candidate-config").Output!;
        var changes = Run("show configuration changes").Output!;

        candidate.Should().Contain("shared-secret <hidden>").And.NotContain("Mask-Me-123");
        changes.Should().Contain("shared-secret <set>").And.NotContain("Mask-Me-123");
    }

    /// <summary>default restores the schema default for a scalar.</summary>
    [Fact]
    public void Default_RestoresSchemaDefault()
    {
        Run("ssh");
        Run("port 2400");
        Run("default port").Action.Should().Be(ConfigurationLineAction.Continue);
        ConfigurationModel.GetValue(ConfigPaths.Node(_edit.Candidate, "ssh"), ConfigPaths.Setting("ssh", "port"))!.ToJsonString().Should().Be("2222");
    }

    /// <summary>Candidate values never influence where startup-config is written.</summary>
    [Fact]
    public async Task CandidateValues_CannotChooseWriteTarget()
    {
        Run("logging");
        Run("file");
        Run(@"directory ..\..\outside").Action.Should().Be(ConfigurationLineAction.Continue);
        Run("file-name-prefix appsettings").Action.Should().Be(ConfigurationLineAction.Continue);
        var filesBefore = Directory.GetFiles(_fixture.ContentRoot);

        var result = await _service.WriteAsync(_edit, CancellationToken.None);

        result.Status.Should().Be(ConfigurationWriteStatus.Saved);
        _service.StartupFilePath.Should().Be(Path.GetFullPath(_fixture.FilePath));
        Directory.GetFiles(_fixture.ContentRoot).Should().BeEquivalentTo(filesBefore);
        Directory.Exists(Path.GetFullPath(Path.Combine(_fixture.ContentRoot, "..", "..", "outside"))).Should().BeFalse();
    }
}
