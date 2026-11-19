// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AdminDisconnectCliTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (AdminDisconnectCliTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for administrative disconnect CLI parsing and authorization.
/// </summary>
public sealed class AdminDisconnectCliTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T16:30:00Z");

    /// <summary>
    /// Verifies: Dispatcher Administrator Returns Admin Disconnect Action.
    /// </summary>
    [Fact]
    public void Dispatcher_Administrator_ReturnsAdminDisconnectAction()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var admin = CliTestFactory.Operator(PrivilegeLevel.AdministratorBoundary, "admin");

        var result = dispatcher.Execute($"disconnect {session.SessionId}", admin);
        result.Action.Should().Be(CliCommandAction.AdminDisconnect);
        result.DisconnectSessionId.Should().Be(session.SessionId);
    }

    /// <summary>
    /// Verifies: Dispatcher Below Privilege7 Admin Disconnect Is Denied.
    /// </summary>
    [Theory]
    [InlineData(6)]
    [InlineData(1)]
    public void Dispatcher_BelowPrivilege7_AdminDisconnectIsDenied(int privilege)
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0);
        var op = CliTestFactory.Operator(PrivilegeLevel.FromInt(privilege), "user");

        var result = dispatcher.Execute("disconnect deadbeef", op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }

    /// <summary>
    /// Verifies: Dispatcher Privilege7 Admin Disconnect Is Allowed.
    /// </summary>
    [Fact]
    public void Dispatcher_Privilege7_AdminDisconnectIsAllowed()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = CliTestFactory.Operator(PrivilegeLevel.FromInt(7), "ops");

        var result = dispatcher.Execute($"disconnect {session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.AdminDisconnect);
        result.DisconnectSessionId.Should().Be(session.SessionId);
    }

    /// <summary>
    /// Verifies: Dispatcher Unknown Session Reports Not Connected.
    /// </summary>
    [Fact]
    public void Dispatcher_UnknownSession_ReportsNotConnected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var admin = CliTestFactory.Operator(PrivilegeLevel.AdministratorBoundary, "admin");

        var result = dispatcher.Execute("disconnect deadbeef", admin);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be("Session deadbeef is not connected.");
    }

    /// <summary>
    /// Verifies: Policy Admin Disconnect Requires Privilege7.
    /// </summary>
    [Fact]
    public void Policy_AdminDisconnect_RequiresPrivilege7()
    {
        CliCommandPolicy.IsAllowed(PrivilegeLevel.AdministratorBoundary, CliCommandId.Disconnect).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.OperatorBoundary, CliCommandId.Disconnect).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.Disconnect).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.ReadOnlyBoundary, CliCommandId.Disconnect).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.ReadOnlyBoundary, CliCommandId.DisconnectSelf).Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Help And Completion Show Disconnect Forms By Privilege.
    /// </summary>
    [Fact]
    public void HelpAndCompletion_ShowDisconnectFormsByPrivilege()
    {
        var helpOp = CliCommandPolicy.BuildHelpText(PrivilegeLevel.OperatorBoundary);
        helpOp.Should().Contain("disconnect <session-id>");
        var helpRo = CliCommandPolicy.BuildHelpText(PrivilegeLevel.ReadOnlyBoundary);
        helpRo.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        helpRo.Should().NotContain("disconnect <session-id>");
        var helpAdmin = CliCommandPolicy.BuildHelpText(PrivilegeLevel.AdministratorBoundary);
        helpAdmin.Should().Contain("disconnect <session-id>");

        var discovery = new CliCommandDiscovery();
        var adminRoots = discovery.Help("", 0, CliTestFactory.Operator(PrivilegeLevel.AdministratorBoundary), 80);
        adminRoots.Tokens.Should().Contain("disconnect");

        var opRoots = discovery.Help("", 0, CliTestFactory.Operator(PrivilegeLevel.OperatorBoundary), 80);
        opRoots.Tokens.Should().Contain("disconnect");

        var roRoots = discovery.Help("", 0, CliTestFactory.Operator(PrivilegeLevel.ReadOnlyBoundary), 80);
        roRoots.Tokens.Should().Contain("disconnect");
    }

    /// <summary>
    /// Verifies: Parser Resolves Disconnect Forms.
    /// </summary>
    [Fact]
    public void Parser_ResolvesDisconnectForms()
    {
        var withSession = CliCommandParser.Parse("disconnect abcd1234");
        withSession.Status.Should().Be(CliParseStatus.Resolved);
        withSession.CommandId.Should().Be(CliCommandId.Disconnect);

        var bare = CliCommandParser.Parse("disconnect");
        bare.Status.Should().Be(CliParseStatus.Resolved);
        bare.CommandId.Should().Be(CliCommandId.DisconnectSelf);
    }
}
