// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyCommandAuthorizationTests.cs
//
// Purpose:
// Verifies privilege-1 copy visibility and dispatcher authorization gates for
// destination, session existence, and attachment ownership.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Authorization and attachment-gating tests for the <c>copy</c> CLI command.
/// </summary>
public sealed class CopyCommandAuthorizationTests
{
    /// <summary>Fixed timestamp for session registration in these tests.</summary>
    /// <summary>Fixed timestamp for session registration fixtures.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Verifies <c>copy</c> is allowed and discoverable at privilege level 1.
    /// </summary>
    [Fact]
    public void Copy_IsDiscoverableAndExecutable_AtPrivilege1()
    {
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.Copy).Should().BeTrue();
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(1), 80);
        help.Should().Contain("copy");

        var roots = CliCommandCatalog.GetVisibleRoots(PrivilegeLevel.FromInt(1))
            .Select(r => r.Token)
            .ToList();
        roots.Should().Contain("copy");
    }

    /// <summary>
    /// Verifies non-<c>serial:</c> destinations are rejected without dispatching copy.
    /// </summary>
    [Fact]
    public void Copy_InvalidDestination_Rejected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var op = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1));
        var result = dispatcher.Execute("copy http://h/f.bin flash:foo", op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("serial:");
    }

    /// <summary>
    /// Verifies unknown serial session ids are rejected.
    /// </summary>
    [Fact]
    public void Copy_UnknownSession_Rejected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var op = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1));
        var result = dispatcher.Execute("copy http://h/f.bin serial:deadbeef", op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("not found");
    }

    /// <summary>
    /// Verifies copy requires the operator to already be attached to the target session.
    /// </summary>
    [Fact]
    public void Copy_Unattached_Rejected()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-a");
        var result = dispatcher.Execute($"copy http://h/f.bin serial:{session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("attachment");
    }

    /// <summary>
    /// Verifies an attached operator targeting their own session receives <see cref="CliCommandAction.Copy"/>.
    /// </summary>
    [Fact]
    public void Copy_AttachedMatchingSession_ReturnsCopyAction()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-a");
        var attach = attachments.TryAttach("op-a", session.SessionId, "c1", op);
        attach.Succeeded.Should().BeTrue();

        var result = dispatcher.Execute($"copy http://192.0.2.10/firmware.bin serial:{session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.Copy);
        result.CopyRequest.Should().NotBeNull();
        result.CopyRequest!.SessionId.Should().Be(session.SessionId);
        result.CopyRequest.Source.Host.Should().Be("192.0.2.10");
        result.CopyRequest.Source.DisplayUri.Should().Be("http://192.0.2.10/firmware.bin");
    }

    /// <summary>
    /// Verifies another operator cannot copy into a session they do not own.
    /// </summary>
    [Fact]
    public void Copy_OtherOperatorsAttachment_Rejected()
    {
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var owner = TestAuthenticatedOperator.Create("owner", PrivilegeLevel.FromInt(1), operatorSessionId: "op-owner");
        attachments.TryAttach("op-owner", session.SessionId, "c1", owner).Succeeded.Should().BeTrue();

        var other = TestAuthenticatedOperator.Create("other", PrivilegeLevel.FromInt(1), operatorSessionId: "op-other");
        var result = dispatcher.Execute($"copy xmodem: serial:{session.SessionId}", other);
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("attachment");
    }
}
