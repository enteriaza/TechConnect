// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PingDispatcherTests.cs
//
// Purpose:
// Verifies ping dispatch/formatting behaviour with fakes (ICMP and session paths).
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Behaviour tests for ping command dispatch and output.
/// </summary>
public sealed class PingDispatcherTests
{
    /// <summary>
    /// <c>ping 8.8.8.8 repeat 3</c> returns <see cref="CliCommandAction.Ping"/> with IPv4 kind and repeat=3.
    /// </summary>
    [Fact]
    public void Execute_PingIpv4_ReturnsPingAction()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new AlwaysIpv6());

        var result = dispatcher.Execute("ping 8.8.8.8 repeat 3", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Ping);
        result.PingRequest.Should().NotBeNull();
        result.PingRequest!.Repeat.Should().Be(3);
        result.PingRequest.Kind.Should().Be(PingTargetKind.Ipv4);
    }

    /// <summary>
    /// When IPv6 is unavailable, an IPv6 literal ping stays Continue with an unavailable message.
    /// </summary>
    [Fact]
    public void Execute_PingIpv6Unavailable_ReturnsError()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new NeverIpv6());

        var result = dispatcher.Execute("ping 2001:db8::1", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("unavailable");
    }

    /// <summary>
    /// With IPv6 available, a literal destination returns Ping with <see cref="PingTargetKind.Ipv6"/>.
    /// </summary>
    [Fact]
    public void Execute_PingIpv6Literal_ReturnsPingAction_WhenAvailable()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new AlwaysIpv6());

        var result = dispatcher.Execute("ping 2001:db8::1", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Ping);
        result.PingRequest!.Kind.Should().Be(PingTargetKind.Ipv6);
    }

    /// <summary>
    /// Legacy <c>ping ipv6 &lt;addr&gt;</c> syntax is rejected (Continue + guidance mentioning ipv6).
    /// </summary>
    [Fact]
    public void Execute_LegacyPingIpv6Syntax_Rejected()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new AlwaysIpv6());

        var result = dispatcher.Execute("ping ipv6 2001:db8::1", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("ipv6");
    }

    /// <summary>
    /// Privilege 1 may dispatch IPv4 ping (no higher privilege required).
    /// </summary>
    [Fact]
    public void Execute_Ping_Privilege1_Allowed()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new NeverIpv6());

        var result = dispatcher.Execute(
            "ping 1.1.1.1",
            CliTestFactory.Operator(TechConnect.Server.Authentication.Models.PrivilegeLevel.FromInt(1)));
        result.Action.Should().Be(CliCommandAction.Ping);
    }

    /// <summary>
    /// Bare <c>ping</c> is incomplete and does not start a ping action.
    /// </summary>
    [Fact]
    public void Execute_IncompletePing_Fails()
    {
        var dispatcher = new TechConnectCliDispatcher(
            new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
            new TechConnect.Server.Attachment.OperatorAttachmentManager(
                new TechConnect.Server.Sessions.ConnectorSessionRegistry(),
                TimeProvider.System),
            new TechConnect.Server.Sessions.ConnectorSerialConfigurationCache(),
            CliTestFactory.CreateAuthorization(),
            ipv6Availability: new NeverIpv6());

        var result = dispatcher.Execute("ping", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("Incomplete");
    }
    /// <summary>Fake availability that always enables IPv6 ping and exposes one documentation address.</summary>
    private sealed class AlwaysIpv6 : IIPv6NetworkAvailability
    {
        /// <inheritdoc />
        public bool IsIpv6PingAvailable => true;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalIpv4Addresses() => [];
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalGlobalIpv6Addresses() =>
            [System.Net.IPAddress.Parse("2001:db8::1")];
    }

    /// <summary>Fake availability that disables IPv6 ping and returns no local addresses.</summary>
    private sealed class NeverIpv6 : IIPv6NetworkAvailability
    {
        /// <inheritdoc />
        public bool IsIpv6PingAvailable => false;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalIpv4Addresses() => [];
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalGlobalIpv6Addresses() => [];
    }
}
