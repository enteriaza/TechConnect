// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliAuthorizationExecutionTests.cs
//
// Purpose:
// Regression tests and helpers covering CliAuthorizationExecution behaviour.
// Fixture: CliAuthorizationExecutionTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authorization;
/// <summary>
/// xUnit test fixture for CliAuthorizationExecution.
/// </summary>

public sealed class CliAuthorizationExecutionTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
    /// <summary>
    /// Verifies Level1 Attach Is Allowed Without Attachment Side Effects.
    /// </summary>

    [Theory]
    [InlineData("attach")]
    [InlineData("ATTACH")]
    public void Level1_Attach_IsAllowed_WithoutAttachmentSideEffects(string input)
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary, operatorSessionId: "ro-1");

        var result = dispatcher.Execute(input, identity);
        result.Action.Should().Be(CliCommandAction.Attach);
        result.ConnectSessionId.Should().BeNull();
        result.SerialSettings.Should().BeNull();
        attachments.Count.Should().Be(0);
        attachments.TryGetBySessionId(session.SessionId, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Removed Commands Are Invalid Without Side Effects.
    /// </summary>

    [Theory]
    [InlineData("detach")]
    [InlineData("terminal")]
    [InlineData("set baud 9600")]
    [InlineData("SET BAUD 9600")]
    public void RemovedCommands_AreInvalid_WithoutSideEffects(string input)
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary, operatorSessionId: "a-1");

        var result = dispatcher.Execute(input, identity);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("% Invalid command");
        result.ConnectSessionId.Should().BeNull();
        result.SerialSettings.Should().BeNull();
        attachments.Count.Should().Be(0);
        attachments.TryGetBySessionId(session.SessionId, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Level1 Connect Returns Connect Action With Default Serial Settings.
    /// </summary>

    [Fact]
    public void Level1_Connect_ReturnsConnectAction_WithDefaultSerialSettings()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        var result = dispatcher.Execute($"connect {session.SessionId}", identity);
        result.Action.Should().Be(CliCommandAction.Connect);
        result.ConnectSessionId.Should().Be(session.SessionId);
        result.SerialSettings.Should().Be(ConnectSerialSettings.Defaults);
        result.SerialSettings!.BaudRate.Should().Be(9600);
        result.SerialSettings.DataBits.Should().Be(8);
        result.SerialSettings.Parity.Should().Be("None");
        result.SerialSettings.StopBits.Should().Be("One");
        // Dispatcher never attaches; the CLI session performs attachment and configuration.
        attachments.Count.Should().Be(0);
    }
    /// <summary>
    /// Verifies Level1 Connect With Serial Parameters Returns Parsed Settings.
    /// </summary>

    [Fact]
    public void Level1_Connect_WithSerialParameters_ReturnsParsedSettings()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        var result = dispatcher.Execute(
            $"connect {session.SessionId} baud 115200 databits 7 parity even stopbits 2",
            identity);
        result.Action.Should().Be(CliCommandAction.Connect);
        result.ConnectSessionId.Should().Be(session.SessionId);
        result.SerialSettings.Should().NotBeNull();
        result.SerialSettings!.BaudRate.Should().Be(115200);
        result.SerialSettings.DataBits.Should().Be(7);
        result.SerialSettings.Parity.Should().Be("Even");
        result.SerialSettings.StopBits.Should().Be("Two");
    }
    /// <summary>
    /// Verifies Level1 Connect With Invalid Serial Parameter Returns Continue With Error.
    /// </summary>

    [Fact]
    public void Level1_Connect_WithInvalidSerialParameter_ReturnsContinueWithError()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        var result = dispatcher.Execute($"connect {session.SessionId} baud 1234", identity);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("baud");
        result.ConnectSessionId.Should().BeNull();
        attachments.Count.Should().Be(0);
    }
    /// <summary>
    /// Verifies Operator Connect Returns Connect Action.
    /// </summary>

    [Fact]
    public void Operator_Connect_ReturnsConnectAction()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.OperatorBoundary);

        var result = dispatcher.Execute($"connect {session.SessionId}", identity);
        result.Action.Should().Be(CliCommandAction.Connect);
        result.ConnectSessionId.Should().Be(session.SessionId);
        result.SerialSettings.Should().Be(ConnectSerialSettings.Defaults);
    }
    /// <summary>
    /// Verifies Level1 Attach Returns Attach Action.
    /// </summary>

    [Fact]
    public void Level1_Attach_ReturnsAttachAction()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        dispatcher.Execute("attach", identity).Action.Should().Be(CliCommandAction.Attach);
    }
    /// <summary>
    /// Verifies Operator Attach Returns Attach Action.
    /// </summary>

    [Fact]
    public void Operator_Attach_ReturnsAttachAction()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.OperatorBoundary);

        dispatcher.Execute("attach", identity).Action.Should().Be(CliCommandAction.Attach);
    }
    /// <summary>
    /// Verifies Level1 Disconnect Returns Release Attachment Action.
    /// </summary>

    [Fact]
    public void Level1_Disconnect_ReturnsReleaseAttachmentAction()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        var result = dispatcher.Execute("disconnect", identity);
        result.Action.Should().Be(CliCommandAction.ReleaseAttachment);
        result.DisconnectSessionId.Should().BeNull();
    }
    /// <summary>
    /// Verifies Administrator Disconnect Session Returns Admin Disconnect Action.
    /// </summary>

    [Fact]
    public void Administrator_DisconnectSession_ReturnsAdminDisconnectAction()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);

        var result = dispatcher.Execute($"disconnect {session.SessionId}", identity);
        result.Action.Should().Be(CliCommandAction.AdminDisconnect);
        result.DisconnectSessionId.Should().Be(session.SessionId);
    }
    /// <summary>
    /// Verifies Level1 Disconnect Session Is Denied.
    /// </summary>

    [Fact]
    public void Level1_DisconnectSession_IsDenied()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.ReadOnlyBoundary);

        var result = dispatcher.Execute($"disconnect {session.SessionId}", identity);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Verifies Alias Question Mark Uses Same Help Policy.
    /// </summary>

    [Fact]
    public void Alias_QuestionMark_UsesSameHelpPolicy()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        var help = dispatcher.Execute("?", ro).Output!;
        help.Should().Contain("show sessions");
        help.Should().Contain("connect <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().NotContain("set baud");
        help.Should().NotContain("show serial");
    }
    /// <summary>
    /// Verifies Quit Alias Is Authorized Like Exit.
    /// </summary>

    [Fact]
    public void QuitAlias_IsAuthorizedLikeExit()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        dispatcher.Execute("quit", ro).Action.Should().Be(CliCommandAction.Exit);
    }
    /// <summary>
    /// Verifies Extra Arguments Are Invalid Before Authorization.
    /// </summary>

    [Fact]
    public void ExtraArguments_AreInvalid_BeforeAuthorization()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        dispatcher.Execute("attach abc", ro).Output.Should().Contain("% Invalid command");
        dispatcher.Execute("disconnect a b", ro).Output.Should().Contain("% Invalid command");
        dispatcher.Execute("connect a b c", ro).Output.Should().Contain("Unknown serial parameter");
        dispatcher.Execute("connect abc", ro).Output.Should().Contain("not found");
    }
    /// <summary>
    /// Verifies Unknown Command Remains Unknown Not Permission Denied.
    /// </summary>

    [Fact]
    public void UnknownCommand_RemainsUnknown_NotPermissionDenied()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var admin = TestAuthenticatedOperator.Create();
        dispatcher.Execute("foobar", admin).Output.Should().Contain("% Invalid command");
    }
    /// <summary>
    /// Verifies Unrelated Set Subcommand Is Unknown Default Deny.
    /// </summary>

    [Fact]
    public void UnrelatedSetSubcommand_IsUnknown_DefaultDeny()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var admin = TestAuthenticatedOperator.Create();
        dispatcher.Execute("set foo", admin).Output.Should().Contain("% Invalid command");
    }
    /// <summary>
    /// Verifies Case Normalization Cannot Bypass For Allowed Commands.
    /// </summary>

    [Theory]
    [InlineData("help")]
    [InlineData("?")]
    [InlineData("HELP")]
    public void CaseNormalization_CannotBypass_ForAllowedCommands(string input)
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        dispatcher.Execute(input, ro).Output.Should().Contain("Available commands:");
    }
    /// <summary>
    /// Verifies Help And Execution Share Same Policy For Level1.
    /// </summary>

    [Fact]
    public void HelpAndExecution_ShareSamePolicy_ForLevel1()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "h", null, "COM1", null, T0).Session!;
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);

        var help = dispatcher.Execute("help", ro).Output!;
        help.Should().Contain("show sessions");
        help.Should().Contain("connect <session-id>");
        help.Should().Contain("enable");
        help.Should().Contain("disable");
        help.Should().NotContain("set baud");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().NotMatchRegex(@"(?m)^\s*(terminal|detach)\b");
        help.Should().Contain("show session <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        help.Should().NotContain("disconnect <session-id>");
        help.Should().Contain("show history");
        help.Should().NotContain("show history session <session-id>");
        help.Should().NotContain("show serial");

        dispatcher.Execute($"connect {session.SessionId}", ro).Action.Should().Be(CliCommandAction.Connect);
        dispatcher.Execute($"show session {session.SessionId}", ro).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("show history", ro).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("attach", ro).Action.Should().Be(CliCommandAction.Attach);
        dispatcher.Execute("disconnect", ro).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute("show serial", ro).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute($"disconnect {session.SessionId}", ro).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Verifies Sensitive Show Denied Below Minimum.
    /// </summary>

    [Theory]
    [InlineData("show history session deadbeef", 4)]
    [InlineData("show history user alice", 10)]
    [InlineData("show serial", 6)]
    [InlineData("disconnect deadbeef", 6)]
    public void SensitiveShow_DeniedBelowMinimum(string input, int privilege)
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(privilege));
        dispatcher.Execute(input, identity).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Verifies Sensitive Show Allowed At Minimum.
    /// </summary>

    [Theory]
    [InlineData("show session deadbeef", 1)]
    [InlineData("show history", 1)]
    [InlineData("show history session deadbeef", 5)]
    [InlineData("show history user alice", 11)]
    [InlineData("show serial", 7)]
    [InlineData("attach", 1)]
    [InlineData("disconnect", 1)]
    public void SensitiveShow_AllowedAtMinimum(string input, int privilege)
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(privilege));
        var result = dispatcher.Execute(input, identity);
        result.Output.Should().NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Verifies Enable Disable Return Actions At Level1.
    /// </summary>

    [Fact]
    public void EnableDisable_ReturnActions_AtLevel1()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1));
        dispatcher.Execute("enable", identity).Action.Should().Be(CliCommandAction.Enable);
        dispatcher.Execute("enable", identity).EnableTargetLevel.Should().Be(15);
        dispatcher.Execute("enable 7", identity).EnableTargetLevel.Should().Be(7);
        dispatcher.Execute("disable", identity).Action.Should().Be(CliCommandAction.Disable);
    }
    /// <summary>
    /// Verifies Level7 Retains Former Operator Capabilities Including Show Serial.
    /// </summary>

    [Fact]
    public void Level7_RetainsFormerOperatorCapabilities_IncludingShowSerial()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7), operatorSessionId: "op-1");

        dispatcher.Execute($"connect {session.SessionId}", identity).Action.Should().Be(CliCommandAction.Connect);
        attachments.TryAttach(identity.OperatorSessionId, session.SessionId, "c1").Succeeded.Should().BeTrue();
        dispatcher.Execute("attach", identity).Action.Should().Be(CliCommandAction.Attach);
        dispatcher.Execute("show serial", identity).Output.Should().NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("disconnect", identity).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute($"disconnect {session.SessionId}", identity).Action.Should()
            .Be(CliCommandAction.AdminDisconnect);
    }
}
