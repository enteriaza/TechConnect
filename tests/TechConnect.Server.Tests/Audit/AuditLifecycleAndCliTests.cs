// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AuditLifecycleAndCliTests.cs
//
// Purpose:
// Regression tests and helpers covering AuditLifecycleAndCli behaviour.
// Fixture: AuditLifecycleAndCliTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Audit;
/// <summary>
/// xUnit test fixture for AuditLifecycleAndCli.
/// </summary>

public sealed class AuditLifecycleAndCliTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T15:00:00Z");
    /// <summary>
    /// Verifies Connector Lifecycle Register Duplicate Disconnect Records Expected Outcomes.
    /// </summary>

    [Fact]
    public void ConnectorLifecycle_RegisterDuplicateDisconnect_RecordsExpectedOutcomes()
    {
        var audit = CliTestFactory.CreateAudit();
        var registry = new ConnectorSessionRegistry();

        var first = registry.TryRegister("CERT-A", "c1", "10.0.0.1:9", "rtr1", "1.0", "COM1", "Tls12", T0);
        first.Succeeded.Should().BeTrue();
        audit.TryWrite(
            AuditEventType.ConnectorRegistered,
            AuditEventOutcome.Success,
            "Connector authenticated and registered",
            new AuditEventContext
            {
                SessionId = first.Session!.SessionId,
                ConnectorIdentity = "CERT-A",
                RemoteEndpoint = "10.0.0.1:9",
                Details = new Dictionary<string, string> { ["hostname"] = "rtr1" }
            });

        var dup = registry.TryRegister("CERT-A", "c2", "10.0.0.2:9", "rtr2", null, null, null, T0);
        dup.Status.Should().Be(ConnectorSessionRegistrationStatus.DuplicateIdentity);
        audit.TryWrite(
            AuditEventType.ConnectorRegistrationRejected,
            AuditEventOutcome.Denied,
            "Duplicate connector identity rejected",
            new AuditEventContext { ConnectorIdentity = "CERT-A", RemoteEndpoint = "10.0.0.2:9" });

        registry.TryUnregisterByConnectionId("c1", out var removed).Should().BeTrue();
        audit.TryWrite(
            AuditEventType.ConnectorDisconnected,
            AuditEventOutcome.Informational,
            "Connector disconnected and unregistered",
            new AuditEventContext { SessionId = removed!.SessionId, ConnectorIdentity = "CERT-A" });

        var bySession = audit.GetBySessionId(removed.SessionId, 50);
        bySession.Select(e => e.EventType).Should().Equal(
            AuditEventType.ConnectorDisconnected,
            AuditEventType.ConnectorRegistered);
        bySession.Should().NotContain(e => e.Outcome == AuditEventOutcome.Failure);
    }
    /// <summary>
    /// Verifies Operator Attach Detach And Automatic Cleanup Record Distinct Events.
    /// </summary>

    [Fact]
    public void OperatorAttachDetach_AndAutomaticCleanup_RecordDistinctEvents()
    {
        var audit = CliTestFactory.CreateAudit();
        var registry = new ConnectorSessionRegistry();
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var identity = TestAuthenticatedOperator.Create("ops", operatorSessionId: "op-session");

        manager.TryAttach(identity.OperatorSessionId, session.SessionId, "c1", identity).Succeeded.Should().BeTrue();
        audit.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "Operator attached to connector",
            new AuditEventContext { ActorUsername = "ops", SessionId = session.SessionId });

        audit.TryWrite(
            AuditEventType.TerminalModeEntered,
            AuditEventOutcome.Informational,
            "Terminal mode entered",
            new AuditEventContext { ActorUsername = "ops", SessionId = session.SessionId });
        audit.TryWrite(
            AuditEventType.TerminalModeExited,
            AuditEventOutcome.Informational,
            "Terminal mode exited",
            new AuditEventContext { ActorUsername = "ops", SessionId = session.SessionId });

        manager.TryDetachByOperatorSessionId(
            identity.OperatorSessionId,
            AttachmentDetachReason.OperatorRequest,
            out _).Should().BeTrue();
        audit.TryWrite(
            AuditEventType.OperatorDetached,
            AuditEventOutcome.Success,
            "Operator detached from connector",
            new AuditEventContext { ActorUsername = "ops", SessionId = session.SessionId });

        manager.TryAttach(identity.OperatorSessionId, session.SessionId, "c1", identity).Succeeded.Should().BeTrue();
        manager.TryDetachByConnectionId(
            "c1",
            AttachmentDetachReason.ConnectorDisconnect,
            out var detached).Should().BeTrue();
        audit.TryWrite(
            AuditEventType.AttachmentReleasedConnectorDisconnect,
            AuditEventOutcome.Informational,
            "Attachment released after connector disconnect",
            new AuditEventContext
            {
                ActorUsername = detached!.Operator?.Username,
                SessionId = detached.SessionId
            });

        manager.TryAttach(identity.OperatorSessionId, session.SessionId, "c1", identity).Succeeded.Should().BeTrue();
        manager.TryDetachByOperatorSessionId(
            identity.OperatorSessionId,
            AttachmentDetachReason.OperatorDisconnect,
            out _).Should().BeTrue();
        audit.TryWrite(
            AuditEventType.AttachmentReleasedSshDisconnect,
            AuditEventOutcome.Informational,
            "Attachment released after SSH disconnect",
            new AuditEventContext { ActorUsername = "ops", SessionId = session.SessionId });

        var events = audit.GetByUsername("ops", 50);
        events.Should().Contain(e => e.EventType == AuditEventType.OperatorAttached);
        events.Should().Contain(e => e.EventType == AuditEventType.OperatorDetached);
        events.Should().Contain(e => e.EventType == AuditEventType.AttachmentReleasedConnectorDisconnect);
        events.Should().Contain(e => e.EventType == AuditEventType.AttachmentReleasedSshDisconnect);
        events.Should().Contain(e => e.EventType == AuditEventType.TerminalModeEntered);
        events.Should().Contain(e => e.EventType == AuditEventType.TerminalModeExited);
        events.Should().NotContain(e =>
            e.EventType == AuditEventType.OperatorDetached &&
            e.Description.Contains("connector disconnect", StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies Baud Request Success Failure Timeout Record Distinct Types.
    /// </summary>

    [Fact]
    public void BaudRequest_SuccessFailureTimeout_RecordDistinctTypes()
    {
        var audit = CliTestFactory.CreateAudit();
        var details = new Dictionary<string, string> { ["baud"] = "9600" };
        audit.TryWrite(
            AuditEventType.BaudRequestStarted,
            AuditEventOutcome.Informational,
            "Remote baud-rate request initiated",
            new AuditEventContext { ActorUsername = "ops", SessionId = "sess1", Details = details });
        audit.TryWrite(
            AuditEventType.BaudRequestSucceeded,
            AuditEventOutcome.Success,
            "Remote baud-rate request succeeded",
            new AuditEventContext { ActorUsername = "ops", SessionId = "sess1", Details = details });
        audit.TryWrite(
            AuditEventType.BaudRequestFailed,
            AuditEventOutcome.Failure,
            "Remote baud-rate request failed",
            new AuditEventContext { ActorUsername = "ops", SessionId = "sess1", Details = details });
        audit.TryWrite(
            AuditEventType.BaudRequestTimedOut,
            AuditEventOutcome.Failure,
            "Remote baud-rate request timed out",
            new AuditEventContext { ActorUsername = "ops", SessionId = "sess1", Details = details });

        var timedOut = new PendingBaudResult
        {
            Success = false,
            TimedOut = true,
            Error = "Baud-rate change timed out waiting for the connector."
        };
        timedOut.TimedOut.Should().BeTrue();

        audit.GetBySessionId("sess1", 10).Select(e => e.EventType).Should().Equal(
            AuditEventType.BaudRequestTimedOut,
            AuditEventType.BaudRequestFailed,
            AuditEventType.BaudRequestSucceeded,
            AuditEventType.BaudRequestStarted);
    }
    /// <summary>
    /// Verifies Authorization Denied Is Audited Successful Show Is Not.
    /// </summary>

    [Fact]
    public void AuthorizationDenied_IsAudited_SuccessfulShowIsNot()
    {
        var audit = CliTestFactory.CreateAudit();
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(audit);
        var readOnly = CliTestFactory.Operator(PrivilegeLevel.ReadOnlyBoundary, "ro");

        dispatcher.Execute("show sessions", readOnly).Output.Should().Contain("No active");
        dispatcher.Execute("attach", readOnly).Action.Should().Be(CliCommandAction.Attach);
        dispatcher.Execute("show serial", readOnly).Output.Should().Be(
            OperatorAuthorizationService.PermissionDeniedMessage);

        var events = audit.GetRecent(20);
        events.Should().ContainSingle();
        events[0].EventType.Should().Be(AuditEventType.AuthorizationDenied);
        events[0].ActorUsername.Should().Be("ro");
        events[0].Details!["command"].Should().Be("show serial");
    }
    /// <summary>
    /// Verifies Show History Allowed At Minimum And Above.
    /// </summary>

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(15)]
    public void ShowHistory_AllowedAtMinimumAndAbove(int privilege)
    {
        var audit = CliTestFactory.CreateAudit();
        audit.TryWrite(
            AuditEventType.SshPasswordAuthSuccess,
            AuditEventOutcome.Success,
            "SSH password authentication succeeded",
            new AuditEventContext { ActorUsername = "viewer" });
        audit.TryWrite(
            AuditEventType.SshPasswordAuthSuccess,
            AuditEventOutcome.Success,
            "SSH password authentication succeeded",
            new AuditEventContext { ActorUsername = "other" });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(audit);
        var op = CliTestFactory.Operator(PrivilegeLevel.FromInt(privilege), "viewer");

        var result = dispatcher.Execute("show history", op);
        result.Output.Should().Contain("ssh-password-ok");
        result.Output.Should().Contain("viewer");
        result.Output.Should().NotContain("other");
    }
    /// <summary>
    /// Verifies Show History Session Denied Below Minimum.
    /// </summary>

    [Fact]
    public void ShowHistorySession_DeniedBelowMinimum()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var op = CliTestFactory.Operator(PrivilegeLevel.FromInt(4), "viewer");
        dispatcher.Execute("show history session abc", op).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Verifies Show History Empty Unknown Filters Evicted Disabled Narrow.
    /// </summary>

    [Fact]
    public void ShowHistory_Empty_UnknownFilters_Evicted_Disabled_Narrow()
    {
        var emptyAudit = CliTestFactory.CreateAudit();
        var (emptyDispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(emptyAudit);
        emptyDispatcher.Execute("show history", CliTestFactory.Operator()).Output
            .Should().Contain("No audit events matching user 'op'");
        emptyDispatcher.Execute("show history session deadbeef", CliTestFactory.Operator()).Output
            .Should().Contain("No audit events matching session");
        emptyDispatcher.Execute("show history user nobody", CliTestFactory.Operator()).Output
            .Should().Contain("No audit events matching user");

        var capped = CliTestFactory.CreateAudit(maximumEvents: 2);
        capped.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "one",
            new AuditEventContext { SessionId = "s1", ActorUsername = "op" });
        capped.TryWrite(AuditEventType.OperatorDetached, AuditEventOutcome.Success, "two",
            new AuditEventContext { SessionId = "s1", ActorUsername = "op" });
        capped.TryWrite(AuditEventType.TerminalModeEntered, AuditEventOutcome.Informational, "three",
            new AuditEventContext { SessionId = "s1", ActorUsername = "op" });
        var (cappedDispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(capped);
        var evicted = cappedDispatcher.Execute("show history", CliTestFactory.Operator()).Output!;
        evicted.Should().Contain("older events were evicted");
        evicted.Should().Contain("three");
        evicted.Should().NotContain("one");

        var disabled = CliTestFactory.CreateAudit(enabled: false);
        var (disabledDispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(disabled);
        disabledDispatcher.Execute("show history", CliTestFactory.Operator()).Output
            .Should().Be(AuditHistoryFormatter.FormatDisabled());

        var narrow = AuditHistoryFormatter.Format(
            capped.GetRecent(50),
            hasEvictedEvents: true,
            maximumCapacity: 2,
            terminalColumns: 40);
        narrow.Should().Contain("Actor:");
        narrow.Should().Contain("---");
    }
    /// <summary>
    /// Verifies Show History User Exact Match Includes Requested Username Failures.
    /// </summary>

    [Fact]
    public void ShowHistoryUser_ExactMatch_IncludesRequestedUsernameFailures()
    {
        var audit = CliTestFactory.CreateAudit();
        audit.TryWrite(
            AuditEventType.SshPasswordAuthFailure,
            AuditEventOutcome.Failure,
            "SSH password authentication failed",
            new AuditEventContext { RequestedUsername = "Alice" });
        audit.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "Operator attached to connector",
            new AuditEventContext { ActorUsername = "alice", SessionId = "abcd1234" });
        audit.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "other",
            new AuditEventContext { ActorUsername = "bob", SessionId = "zzzz9999" });

        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(audit);
        var output = dispatcher.Execute(
                "show history user alice",
                CliTestFactory.Operator(PrivilegeLevel.FromInt(11)))
            .Output!;
        output.Should().Contain("alice");
        output.Should().Contain("abcd1234");
        output.Should().NotContain("bob");
        output.Should().NotContain("zzzz9999");
    }
    /// <summary>
    /// Verifies Show History Session Works After Session Removed From Registry.
    /// </summary>

    [Fact]
    public void ShowHistorySession_WorksAfterSessionRemovedFromRegistry()
    {
        var audit = CliTestFactory.CreateAudit();
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        audit.TryWrite(
            AuditEventType.ConnectorRegistered,
            AuditEventOutcome.Success,
            "Connector authenticated and registered",
            new AuditEventContext { SessionId = session.SessionId });
        registry.TryUnregisterByConnectionId("c1", out _).Should().BeTrue();

        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(audit);
        var output = dispatcher.Execute($"show history session {session.SessionId}", CliTestFactory.Operator())
            .Output!;
        output.Should().Contain(session.SessionId);
        output.Should().Contain("connector-reg");
    }
    /// <summary>
    /// Verifies Help History Visibility Matches Privilege Mins.
    /// </summary>

    [Fact]
    public void Help_HistoryVisibility_MatchesPrivilegeMins()
    {
        var at1 = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(1));
        at1.Should().Contain("show history");
        at1.Should().NotContain("show history session <session-id>");
        var at5 = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(5));
        at5.Should().Contain("show history");
        at5.Should().Contain("show history session <session-id>");
        at5.Should().NotContain("show history user <username>");
        CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(11))
            .Should().Contain("show history user <username>");
    }
}
