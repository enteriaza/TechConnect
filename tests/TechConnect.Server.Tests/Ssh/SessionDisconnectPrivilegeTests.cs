// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SessionDisconnectPrivilegeTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (SessionDisconnectPrivilegeTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Privilege matrix and semantics for session inspection and dual disconnect forms.
/// </summary>
public sealed class SessionDisconnectPrivilegeTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T18:00:00Z");

    /// <summary>
    /// Verifies: Privilege1 Can Connect Show Session Show Sessions And Disconnect Self.
    /// </summary>
    [Fact]
    public void Privilege1_CanConnectShowSessionShowSessions_AndDisconnectSelf()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1), operatorSessionId: "op-1");

        dispatcher.Execute($"connect {session.SessionId}", identity).Action.Should().Be(CliCommandAction.Connect);
        attachments.TryAttach(identity.OperatorSessionId, session.SessionId, session.ConnectionId)
            .Succeeded.Should().BeTrue();

        dispatcher.Execute("show sessions", identity).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute($"show session {session.SessionId}", identity).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);

        dispatcher.Execute("disconnect", identity).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute("attach", identity).Action.Should().Be(CliCommandAction.Attach);
    }

    /// <summary>
    /// Verifies: Privilege1 Cannot Admin Disconnect Any Session.
    /// </summary>
    [Fact]
    public void Privilege1_CannotAdminDisconnectAnySession()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var mine = registry.TryRegister("ID-A", "c-a", null, "host-a", null, "COM1", null, T0).Session!;
        var other = registry.TryRegister("ID-B", "c-b", null, "host-b", null, "COM2", null, T0).Session!;
        var me = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1), operatorSessionId: "op-me");
        var them = TestAuthenticatedOperator.Create("other", PrivilegeLevel.FromInt(1), operatorSessionId: "op-them");

        attachments.TryAttach(me.OperatorSessionId, mine.SessionId, mine.ConnectionId).Succeeded.Should().BeTrue();
        attachments.TryAttach(them.OperatorSessionId, other.SessionId, other.ConnectionId).Succeeded.Should().BeTrue();

        dispatcher.Execute($"disconnect {mine.SessionId}", me).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute($"disconnect {other.SessionId}", me).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);

        attachments.TryGetByOperatorSessionId(me.OperatorSessionId, out _).Should().BeTrue();
        attachments.TryGetByOperatorSessionId(them.OperatorSessionId, out _).Should().BeTrue();
        registry.TryGetBySessionId(mine.SessionId, out _).Should().BeTrue();
        registry.TryGetBySessionId(other.SessionId, out _).Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Privilege7 Can Admin Disconnect Own Other And Unattached Sessions.
    /// </summary>
    [Fact]
    public void Privilege7_CanAdminDisconnectOwnOtherAndUnattachedSessions()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var mine = registry.TryRegister("ID-A", "c-a", null, "host-a", null, "COM1", null, T0).Session!;
        var other = registry.TryRegister("ID-B", "c-b", null, "host-b", null, "COM2", null, T0).Session!;
        var free = registry.TryRegister("ID-C", "c-c", null, "host-c", null, "COM3", null, T0).Session!;
        var ops = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7), operatorSessionId: "op-7");
        var peer = TestAuthenticatedOperator.Create("peer", PrivilegeLevel.FromInt(1), operatorSessionId: "op-peer");

        attachments.TryAttach(ops.OperatorSessionId, mine.SessionId, mine.ConnectionId).Succeeded.Should().BeTrue();
        attachments.TryAttach(peer.OperatorSessionId, other.SessionId, other.ConnectionId).Succeeded.Should().BeTrue();

        dispatcher.Execute($"disconnect {mine.SessionId}", ops).Action.Should().Be(CliCommandAction.AdminDisconnect);
        dispatcher.Execute($"disconnect {mine.SessionId}", ops).DisconnectSessionId.Should().Be(mine.SessionId);

        dispatcher.Execute($"disconnect {other.SessionId}", ops).Action.Should().Be(CliCommandAction.AdminDisconnect);
        dispatcher.Execute($"disconnect {other.SessionId}", ops).DisconnectSessionId.Should().Be(other.SessionId);

        dispatcher.Execute($"disconnect {free.SessionId}", ops).Action.Should().Be(CliCommandAction.AdminDisconnect);
        dispatcher.Execute($"disconnect {free.SessionId}", ops).DisconnectSessionId.Should().Be(free.SessionId);

        // Level 7 still retains self-service forms.
        dispatcher.Execute("disconnect", ops).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute("show sessions", ops).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
    }

    /// <summary>
    /// Verifies: Disconnect No Attachment Still Returns Release Attachment Action Without Side Effects.
    /// </summary>
    [Fact]
    public void Disconnect_NoAttachment_StillReturnsReleaseAttachmentAction_WithoutSideEffects()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1), operatorSessionId: "op-none");

        dispatcher.Execute("disconnect", identity).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        attachments.Count.Should().Be(0);
        registry.TryGetBySessionId(session.SessionId, out _).Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Disconnect Unknown Session At Privilege7 Reports Not Connected.
    /// </summary>
    [Fact]
    public void Disconnect_UnknownSession_AtPrivilege7_ReportsNotConnected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ops = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7));

        var result = dispatcher.Execute("disconnect deadbeef", ops);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Be("Session deadbeef is not connected.");
    }

    /// <summary>
    /// Verifies: Disconnect Invalid Argument Count Is Rejected.
    /// </summary>
    [Theory]
    [InlineData("disconnect a b")]
    [InlineData("disconnect a b c")]
    public void Disconnect_InvalidArgumentCount_IsRejected(string input)
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var ops = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7));

        var result = dispatcher.Execute(input, ops);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("% Invalid command");
    }

    /// <summary>
    /// Verifies: Parser Distinguishes Bare And Session Disconnect.
    /// </summary>
    [Fact]
    public void Parser_DistinguishesBareAndSessionDisconnect()
    {
        var bare = CliCommandParser.Parse("disconnect");
        bare.Status.Should().Be(CliParseStatus.Resolved);
        bare.CommandId.Should().Be(CliCommandId.DisconnectSelf);

        var withSession = CliCommandParser.Parse("disconnect abcd1234");
        withSession.Status.Should().Be(CliParseStatus.Resolved);
        withSession.CommandId.Should().Be(CliCommandId.Disconnect);

        CliCommandParser.Parse("disconnect a b").Status.Should().Be(CliParseStatus.Invalid);
    }

    /// <summary>
    /// Verifies: Help And Discovery Agree With Execution For Disconnect Forms.
    /// </summary>
    [Fact]
    public void HelpAndDiscovery_AgreeWithExecution_ForDisconnectForms()
    {
        var help1 = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(1));
        help1.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        help1.Should().Contain("Release your current connector attachment");
        help1.Should().NotContain("disconnect <session-id>");

        var help7 = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(7));
        help7.Should().Contain("disconnect <session-id>");
        help7.Should().MatchRegex(@"(?m)^\s*disconnect\b");

        var discovery = new CliCommandDiscovery();
        discovery.Help("", 0, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)), 80)
            .Tokens.Should().Contain("disconnect");
        discovery.Complete("disconnect ", 11, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)), 80)
            .Kind.Should().Be(CliDiscoveryKind.None);

        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "h", null, "COM1", null, T0).Session!;
        var level1 = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1));
        var level7 = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(7));

        dispatcher.Execute("disconnect", level1).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute($"disconnect {session.SessionId}", level1).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute($"disconnect {session.SessionId}", level7).Action.Should()
            .Be(CliCommandAction.AdminDisconnect);
    }

    /// <summary>
    /// Verifies: Active Privilege After Disable Denies Admin Disconnect.
    /// </summary>
    [Fact]
    public void ActivePrivilege_AfterDisable_DeniesAdminDisconnect()
    {
        var (dispatcher, registry, _, _, _, authorization, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "h", null, "COM1", null, T0).Session!;

        var at7 = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7));
        dispatcher.Execute($"disconnect {session.SessionId}", at7).Action.Should().Be(CliCommandAction.AdminDisconnect);

        var afterDisable = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(1));
        authorization.TryAuthorize(afterDisable, CliCommandId.Disconnect, out _).Should().BeFalse();
        dispatcher.Execute($"disconnect {session.SessionId}", afterDisable).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("disconnect", afterDisable).Action.Should().Be(CliCommandAction.ReleaseAttachment);
    }
}
