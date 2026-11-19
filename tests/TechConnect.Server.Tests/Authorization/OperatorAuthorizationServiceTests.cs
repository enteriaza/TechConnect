// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    OperatorAuthorizationServiceTests.cs
//
// Purpose:
// Regression tests and helpers covering OperatorAuthorizationService behaviour.
// Fixture: OperatorAuthorizationServiceTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authorization;
/// <summary>
/// xUnit test fixture for OperatorAuthorizationService.
/// </summary>

public sealed class OperatorAuthorizationServiceTests
{
    /// <summary>
    /// Field <c>_auth</c> used by this implementation.
    /// </summary>
    private readonly OperatorAuthorizationService _auth = CliTestFactory.CreateAuthorization();
    /// <summary>
    /// Verifies Missing Identity Fails Closed.
    /// </summary>

    [Fact]
    public void MissingIdentity_FailsClosed()
    {
        var decision = _auth.Authorize(null, CliCommandId.Help);
        decision.Outcome.Should().Be(AuthorizationOutcome.Denied);
    }
    /// <summary>
    /// Verifies Unknown Privilege Fails Closed.
    /// </summary>

    [Fact]
    public void UnknownPrivilege_FailsClosed()
    {
        // default(PrivilegeLevel) has Value 0 — outside 1–15; fail closed.
        var identity = TestAuthenticatedOperator.Create(privilege: default(PrivilegeLevel));
        _auth.Authorize(identity, CliCommandId.Help).Outcome.Should().Be(AuthorizationOutcome.Denied);
    }
    /// <summary>
    /// Verifies Level1 Allowed Connect Attach Disconnect Self Enable Denied Show Serial And Admin Disconnect.
    /// </summary>

    [Fact]
    public void Level1_AllowedConnectAttachDisconnectSelfEnable_DeniedShowSerialAndAdminDisconnect()
    {
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        _auth.TryAuthorize(identity, CliCommandId.Connect, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Attach, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.DisconnectSelf, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Enable, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Disable, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowSerial, out _).Should().BeFalse();
        _auth.TryAuthorize(identity, CliCommandId.Disconnect, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Level1 Allowed Basic Monitoring Connect And Attach.
    /// </summary>

    [Fact]
    public void Level1_AllowedBasicMonitoringConnectAndAttach()
    {
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1));
        _auth.TryAuthorize(identity, CliCommandId.Help, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowSessions, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Exit, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Connect, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Attach, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowSession, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.DisconnectSelf, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowHistory, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowHistorySession, out _).Should().BeFalse();
        _auth.TryAuthorize(identity, CliCommandId.ShowHistoryUser, out _).Should().BeFalse();
        _auth.TryAuthorize(identity, CliCommandId.ShowVersion, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Nslookup, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowSerial, out _).Should().BeFalse();
        _auth.TryAuthorize(identity, CliCommandId.Disconnect, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Level7 Retains Former Operator Capabilities.
    /// </summary>

    [Fact]
    public void Level7_RetainsFormerOperatorCapabilities()
    {
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(7));
        _auth.TryAuthorize(identity, CliCommandId.Connect, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Attach, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowSerial, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowHistory, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.ShowHistoryUser, out _).Should().BeFalse();
        _auth.TryAuthorize(identity, CliCommandId.DisconnectSelf, out _).Should().BeTrue();
        _auth.TryAuthorize(identity, CliCommandId.Disconnect, out _).Should().BeTrue();
    }
}
