// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TechConnectCliDispatcherTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (TechConnectCliDispatcherTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for TechConnectCliDispatcher command parsing and authorization.
/// </summary>
public sealed class TechConnectCliDispatcherTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T10:30:00Z");
    /// <summary>
    /// Field <c>Op</c> used by this implementation.
    /// </summary>
    private static readonly AuthenticatedOperator Op =
        TestAuthenticatedOperator.Create("op", operatorSessionId: "op-test");
    /// <summary>
    /// Performs create.
    /// </summary>
    /// <returns>The operation result.</returns>

    private static (TechConnectCliDispatcher Dispatcher, ConnectorSessionRegistry Registry,
        OperatorAttachmentManager Attachments, ConnectorSerialConfigurationCache SerialCache) Create()
    {
        var created = CliTestFactory.CreateDispatcher();
        return (created.Dispatcher, created.Registry, created.Attachments, created.SerialCache);
    }

    /// <summary>
    /// Verifies: Empty Input Continues Without Output.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInput_ContinuesWithoutOutput(string? input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);

        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().BeNull();
    }

    /// <summary>
    /// Verifies: Help Commands Include Session Connect Attach And Disconnect Commands.
    /// </summary>
    [Theory]
    [InlineData("help")]
    [InlineData("HELP")]
    [InlineData("?")]
    public void HelpCommands_IncludeSessionConnectAttachAndDisconnectCommands(string input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);

        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("Available commands");
        result.Output.Should().Contain("show sessions");
        result.Output.Should().Contain("show session <session-id>");
        result.Output.Should().Contain("show serial");
        result.Output.Should().Contain("show history");
        result.Output.Should().Contain("connect <session-id>");
        result.Output.Should().NotContain("set baud");
        result.Output.Should().MatchRegex(@"(?m)^\s*attach\b");
        result.Output.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        result.Output.Should().NotMatchRegex(@"(?m)^\s*(detach|terminal)\b");
        result.Output.Should().Contain("exit");
    }

    /// <summary>
    /// Verifies: Exit Commands Request Disconnect.
    /// </summary>
    [Theory]
    [InlineData("exit")]
    [InlineData("EXIT")]
    [InlineData("quit")]
    public void ExitCommands_RequestDisconnect(string input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);

        result.Action.Should().Be(CliCommandAction.Exit);
        result.Output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Verifies: Show Sessions When Empty Returns Friendly Message.
    /// </summary>
    [Fact]
    public void ShowSessions_WhenEmpty_ReturnsFriendlyMessage()
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute("show sessions", Op);

        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be("No active connector sessions.");
    }

    /// <summary>
    /// Verifies: Show Sessions Formats Registered Sessions.
    /// </summary>
    [Fact]
    public void ShowSessions_FormatsRegisteredSessions()
    {
        var (dispatcher, registry, _, _) = Create();
        var a = registry.TryRegister(
            "7B3A1111D902AAAA",
            "c1",
            "10.0.0.10:49152",
            "router-001",
            "1.0.0",
            "COM1",
            "Tls13",
            T0).Session!;
        var b = registry.TryRegister(
            "AABB2222CCDD3333",
            "c2",
            "10.0.0.11:50000",
            "router-002",
            "1.0.1",
            "COM2",
            "Tls12",
            T0.AddSeconds(1)).Session!;

        var result = dispatcher.Execute("show sessions", Op);
        result.Output.Should().Contain("SESSION");
        result.Output.Should().Contain(a.SessionId);
        result.Output.Should().Contain(b.SessionId);
        result.Output.Should().Contain("router-001");
        result.Output.Should().Contain("2 active sessions");
    }

    /// <summary>
    /// Verifies: Show Session Returns Details Case Insensitive.
    /// </summary>
    [Fact]
    public void ShowSession_ReturnsDetails_CaseInsensitive()
    {
        var (dispatcher, registry, _, _) = Create();
        var session = registry.TryRegister(
            "7B3A1111D902AAAA",
            "c1",
            "10.0.0.10:49152",
            "router-001",
            "1.0.0",
            "COM1",
            "Tls13",
            T0).Session!;

        var result = dispatcher.Execute($"show session {session.SessionId.ToUpperInvariant()}", Op);
        result.Output.Should().Contain("Connector Information");
        result.Output.Should().Contain(session.SessionId);
        result.Output.Should().Contain("router-001");
        result.Output.Should().Contain("Connected");
        result.Output.Should().Contain("Operator Attachment");
        result.Output.Should().Contain("Serial Information");
        result.Output.Should().Contain("(computed)");
    }

    /// <summary>
    /// Verifies: Show Session Unknown Id Returns Friendly Error.
    /// </summary>
    [Fact]
    public void ShowSession_UnknownId_ReturnsFriendlyError()
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute("show session deadbeef", Op);
        result.Output.Should().Be("% Session 'deadbeef' not found.");
    }

    /// <summary>
    /// Verifies: Show Serial When Not Attached Returns Error.
    /// </summary>
    [Fact]
    public void ShowSerial_WhenNotAttached_ReturnsError()
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute("show serial", Op);
        result.Output.Should().Contain("Not attached");
    }

    /// <summary>
    /// Verifies: Show Serial When Attached Without Config Says Unknown.
    /// </summary>
    [Fact]
    public void ShowSerial_WhenAttachedWithoutConfig_SaysUnknown()
    {
        var (dispatcher, registry, attachments, _) = Create();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM3", null, T0).Session!;
        attachments.TryAttach(Op.OperatorSessionId, session.SessionId, "c1").Succeeded.Should().BeTrue();

        var result = dispatcher.Execute("show serial", Op);
        result.Output.Should().Contain("Not reported");
    }

    /// <summary>
    /// Verifies: Show Serial When Known Formats Configuration.
    /// </summary>
    [Fact]
    public void ShowSerial_WhenKnown_FormatsConfiguration()
    {
        var (dispatcher, registry, attachments, cache) = Create();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM3", null, T0).Session!;
        attachments.TryAttach(Op.OperatorSessionId, session.SessionId, "c1").Succeeded.Should().BeTrue();
        cache.Set(new CachedSerialConfiguration
        {
            SessionId = session.SessionId,
            PortName = "COM3",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            Handshake = "None",
            ReportedAtUtc = T0
        });

        var result = dispatcher.Execute("show serial", Op);
        result.Output.Should().Contain("115200");
        result.Output.Should().Contain("COM3");
        result.Output.Should().Contain("Stop Bits");
        result.Output.Should().Contain("Reported At");
    }

    /// <summary>
    /// Verifies: Set Baud Is Removed And Invalid.
    /// </summary>
    [Fact]
    public void SetBaud_IsRemoved_AndInvalid()
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute("set baud 9600", Op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("% Invalid command");
    }

    /// <summary>
    /// Verifies: Connect Without Serial Parameters Uses Defaults9600 8N1.
    /// </summary>
    [Fact]
    public void Connect_WithoutSerialParameters_UsesDefaults9600_8N1()
    {
        var (dispatcher, registry, _, _) = Create();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM3", null, T0).Session!;

        var result = dispatcher.Execute($"connect {session.SessionId}", Op);
        result.Action.Should().Be(CliCommandAction.Connect);
        result.ConnectSessionId.Should().Be(session.SessionId);
        result.SerialSettings.Should().Be(ConnectSerialSettings.Defaults);
    }

    /// <summary>
    /// Verifies: Connect With Serial Parameters Returns Parsed Settings.
    /// </summary>
    [Fact]
    public void Connect_WithSerialParameters_ReturnsParsedSettings()
    {
        var (dispatcher, registry, _, _) = Create();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM3", null, T0).Session!;

        var result = dispatcher.Execute($"connect {session.SessionId} baud 19200 parity odd", Op);
        result.Action.Should().Be(CliCommandAction.Connect);
        result.SerialSettings!.BaudRate.Should().Be(19200);
        result.SerialSettings.DataBits.Should().Be(8);
        result.SerialSettings.Parity.Should().Be("Odd");
        result.SerialSettings.StopBits.Should().Be("One");
    }

    /// <summary>
    /// Verifies: Connect Unsupported Baud Rate Returns Error.
    /// </summary>
    [Fact]
    public void Connect_UnsupportedBaudRate_ReturnsError()
    {
        var (dispatcher, registry, _, _) = Create();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM3", null, T0).Session!;

        var result = dispatcher.Execute($"connect {session.SessionId} baud 12345", Op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("Unsupported baud rate");
    }

    /// <summary>
    /// Verifies: Incomplete Show Syntax Returns Incomplete.
    /// </summary>
    [Theory]
    [InlineData("show")]
    [InlineData("show session")]
    public void IncompleteShowSyntax_ReturnsIncomplete(string input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);
        result.Output.Should().Be("% Incomplete command");
    }

    /// <summary>
    /// Verifies: Invalid Show Trailing Tokens Returns Invalid Command.
    /// </summary>
    [Theory]
    [InlineData("show sessions extra")]
    [InlineData("show session a b")]
    public void InvalidShowTrailingTokens_ReturnsInvalidCommand(string input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);
        result.Output.Should().Contain("% Invalid command");
    }

    /// <summary>
    /// Verifies: Unknown Command Returns Friendly Error.
    /// </summary>
    [Fact]
    public void UnknownCommand_ReturnsFriendlyError()
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute("foobar", Op);
        result.Output.Should().Contain("% Invalid command");
        result.Output.Should().Contain("^");
    }

    /// <summary>
    /// Verifies: Incomplete Command Returns Incomplete Message.
    /// </summary>
    [Theory]
    [InlineData("show")]
    [InlineData("show session")]
    [InlineData("connect")]
    public void IncompleteCommand_ReturnsIncompleteMessage(string input)
    {
        var (dispatcher, _, _, _) = Create();
        var result = dispatcher.Execute(input, Op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be("% Incomplete command");
    }

    /// <summary>
    /// Verifies: Concurrent Inspection Is Safe.
    /// </summary>
    [Fact]
    public void ConcurrentInspection_IsSafe()
    {
        var (dispatcher, registry, _, _) = Create();
        for (var i = 0; i < 10; i++)
        {
            registry.TryRegister($"ID{i}", $"c{i}", $"10.0.0.{i}:1", $"h{i}", null, "COM1", null, T0);
        }

        Parallel.For(0, 50, _ =>
        {
            var list = dispatcher.Execute("show sessions", Op);
            list.Output.Should().Contain("10 active sessions");
            var detail = dispatcher.Execute($"show session {registry.List()[0].SessionId}", Op);
            detail.Output.Should().Contain("Connector Information");
        });
    }

    /// <summary>
    /// Verifies: Welcome And Prompt Constants Are Correct.
    /// </summary>
    [Fact]
    public void WelcomeAndPrompt_Constants_AreCorrect()
    {
        TechConnectCliDispatcher.WelcomeBanner.Should().Be("Connected to NMS TechConnect Server");
        TechConnectCliDispatcher.Prompt.Should().Be("TechConnect>");
        TechConnectCliDispatcher.GetPrompt(PrivilegeLevel.AdministratorBoundary).Should().Be("TechConnect#");
        TechConnectCliDispatcher.GetPrompt(PrivilegeLevel.OperatorBoundary).Should().Be("TechConnect>");
        TechConnectCliDispatcher.GetPrompt(PrivilegeLevel.ReadOnlyBoundary).Should().Be("TechConnect>");
    }
}
