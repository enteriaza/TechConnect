// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliCommandDiscoveryTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (CliCommandDiscoveryTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for Tab/? CLI discovery visibility and completions.
/// </summary>
public sealed class CliCommandDiscoveryTests
{
    /// <summary>Fixed timestamp for connector session registration fixtures.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Unique top-level prefix <c>sh</c> completes to <c>show</c> with a trailing space.
    /// </summary>
    [Fact]
    public void Complete_UniqueTopLevel()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("sh", 2, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        result.CompletionText.Should().Be("show");
        result.AppendTrailingSpace.Should().BeTrue();
    }

    /// <summary>
    /// Unique nested prefix after <c>show</c> completes <c>ser</c> to <c>serial</c>.
    /// </summary>
    [Fact]
    public void Complete_UniqueNested()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("show ser", 8, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        result.CompletionText.Should().Be("serial");
    }

    /// <summary>
    /// Ambiguous <c>show s</c> lists next tokens only (not full commands) and extends common prefix <c>se</c>.
    /// </summary>
    [Fact]
    public void Complete_AmbiguousNested_ListsNextTokensOnly()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("show s", 6, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.AmbiguousCompletion);
        result.Tokens.Should().Equal("serial", "session", "sessions", "startup-config");
        result.DisplayText.Should().Contain("sessions");
        result.DisplayText.Should().NotContain("show sessions");
        result.CompletionText.Should().Be("s");

        var narrowed = discovery.Complete("show se", 7, Admin(), 80);
        narrowed.Tokens.Should().Equal("serial", "session", "sessions");
        narrowed.CompletionText.Should().Be("se");
    }

    /// <summary>
    /// Unknown prefixes return <see cref="CliDiscoveryKind.None"/>.
    /// </summary>
    [Fact]
    public void Complete_NoMatch_ReturnsNone()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete("xyz", 3, Admin(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// Completing past a finished leaf command returns None (nothing further to offer).
    /// </summary>
    [Fact]
    public void Complete_AlreadyCompleteLeaf_ReturnsNone()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete("attach ", 7, Admin(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// Removed verbs (<c>set baud</c>, <c>detach</c>, <c>terminal</c>) are not discoverable.
    /// </summary>
    [Theory]
    [InlineData("set baud ")]
    [InlineData("detach ")]
    [InlineData("terminal ")]
    public void Complete_RemovedCommands_ReturnNone(string line)
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete(line, line.Length, Admin(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// After <c>connect &lt;session-id&gt;</c>, Tab lists baud/databits/parity/stopbits.
    /// </summary>
    [Fact]
    public void Complete_ConnectSerialKeywords_AfterSessionId()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);
        var line = $"connect {session.SessionId} ";
        var result = discovery.Complete(line, line.Length, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.AmbiguousCompletion);
        result.Tokens.Should().Equal("baud", "databits", "parity", "stopbits");
        result.CompletionText.Should().BeEmpty();
    }

    /// <summary>
    /// Serial keywords already supplied on the connect line are omitted from further completion.
    /// </summary>
    [Fact]
    public void Complete_ConnectSerialKeywords_OmitsAlreadySupplied()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);
        var line = $"connect {session.SessionId} baud 9600 ";
        var result = discovery.Complete(line, line.Length, Admin(), 80);
        result.Tokens.Should().Equal("databits", "parity", "stopbits");
        result.Tokens.Should().NotContain("baud");
    }

    /// <summary>
    /// Unique parity prefix <c>n</c> completes to <c>none</c>.
    /// </summary>
    [Fact]
    public void Complete_ConnectParityValue_UniquePrefix()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("connect deadbeef parity n", 26, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        result.CompletionText.Should().Be("none");
    }

    /// <summary>
    /// After <c>stopbits</c>, discovery lists supported values 1 / 1.5 / 2.
    /// </summary>
    [Fact]
    public void Complete_ConnectStopBits_ListsSupportedValues()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("connect deadbeef stopbits ", 26, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.AmbiguousCompletion);
        result.Tokens.Should().Equal("1", "1.5", "2");
    }

    /// <summary>
    /// Baud prefix <c>11</c> uniquely completes to <c>115200</c> from the supported rate set.
    /// </summary>
    [Fact]
    public void Complete_ConnectBaud_UsesSupportedRates()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("connect deadbeef baud 11", 24, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        result.CompletionText.Should().Be("115200");
    }

    /// <summary>
    /// Authorized operators see live connector session ids when completing <c>connect</c>.
    /// </summary>
    [Fact]
    public void Complete_SessionIds_FromRegistry_WhenAuthorized()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);
        var result = discovery.Complete("connect ", 8, Admin(), 80);
        result.Tokens.Should().Contain(session.SessionId);
    }

    /// <summary>
    /// A unique partial session-id prefix completes to the full id.
    /// </summary>
    [Fact]
    public void Complete_SessionIds_UniquePartial_Completes()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        registry.TryRegister("ID2", "c2", null, "host2", null, "COM2", null, T0);
        var discovery = new CliCommandDiscovery(registry);
        var partial = session.SessionId[..^1];
        var line = "connect " + partial;
        var result = discovery.Complete(line, line.Length, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.UniqueCompletion);
        result.CompletionText.Should().Be(session.SessionId);
    }

    /// <summary>
    /// Non-matching session-id prefixes return None.
    /// </summary>
    [Fact]
    public void Complete_SessionIds_NoMatch_ReturnsNone()
    {
        var registry = new ConnectorSessionRegistry();
        registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0);
        var discovery = new CliCommandDiscovery(registry);
        discovery.Complete("connect zzzz", 12, Admin(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// <c>?</c> after <c>connect</c> shows <c>&lt;session-id&gt;</c> placeholder, not live registry ids.
    /// </summary>
    [Fact]
    public void Help_Connect_ShowsSessionPlaceholder_NotLiveSessions()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);
        var result = discovery.Help("connect ", 8, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.ContextHelp);
        result.DisplayText.Should().Contain("<session-id>");
        result.DisplayText.Should().Contain("Connector session identifier");
        result.DisplayText.Should().NotContain(session.SessionId);
    }

    /// <summary>
    /// Help after a session id lists serial keywords with baud-rate description text.
    /// </summary>
    [Fact]
    public void Help_ConnectAfterSession_ShowsSerialKeywords()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("connect deadbeef ", 17, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.ContextHelp);
        result.Tokens.Should().Equal("baud", "databits", "parity", "stopbits");
        result.DisplayText.Should().Contain("Serial baud rate");
    }

    /// <summary>
    /// Help after <c>parity</c> lists even/mark/none/odd/space.
    /// </summary>
    [Fact]
    public void Help_ConnectParity_ShowsSupportedValues()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("connect deadbeef parity ", 24, Admin(), 80);
        result.Tokens.Should().Equal("even", "mark", "none", "odd", "space");
    }

    /// <summary>
    /// Partial top-level token help for <c>con</c> offers only <c>connect</c>.
    /// </summary>
    [Fact]
    public void Help_PartialCommandToken()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("conn", 4, Admin(), 80);
        result.Tokens.Should().ContainSingle().Which.Should().Be("connect");
        discovery.Help("con", 3, Admin(), 80).Tokens.Should().Equal("configure", "connect");
        discovery.Help("con", 3, ReadOnly(), 80).Tokens.Should().ContainSingle().Which.Should().Be("connect");
    }

    /// <summary>
    /// Top-level help lists authorized verbs and excludes removed set/detach/terminal for admin and level 1.
    /// </summary>
    [Fact]
    public void Help_TopLevel_AuthorizedOnly()
    {
        var discovery = new CliCommandDiscovery();
        var admin = discovery.Help("", 0, Admin(), 80);
        admin.Kind.Should().Be(CliDiscoveryKind.ContextHelp);
        admin.Tokens.Should().Equal(
            "attach", "clear", "configure", "connect", "copy", "disable", "disconnect", "enable", "exit", "help", "nslookup", "ping", "show", "traceroute", "write");
        admin.Tokens.Should().NotContain(["set", "detach", "terminal"]);

        var level1 = discovery.Help("", 0, ReadOnly(), 80);
        level1.Tokens.Should().Equal(
            "attach", "clear", "connect", "copy", "disable", "disconnect", "enable", "exit", "help", "nslookup", "ping", "show", "traceroute");
        level1.Tokens.Should().NotContain(["set", "detach", "terminal"]);
    }

    /// <summary>
    /// Ping help shows <c>&lt;destination&gt;</c> and session, not a legacy ipv6 keyword.
    /// </summary>
    [Fact]
    public void Help_Ping_ShowsTargets()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new AlwaysIpv6());
        var result = discovery.Help("ping ", 5, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.ContextHelp);
        result.DisplayText.Should().Contain("<destination>");
        result.Tokens.Should().NotContain("ipv6");
        result.Tokens.Should().Contain("session");
    }

    /// <summary>
    /// Traceroute help lists destination then alphabetical options for IPv4 and IPv6 literals.
    /// </summary>
    [Fact]
    public void Help_Traceroute_ShowsTargets_AlphabeticalOptions()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new AlwaysIpv6());
        var targets = discovery.Help("traceroute ", 11, Admin(), 80);
        targets.Tokens.Should().Contain("<destination>");
        targets.Tokens.Should().NotContain("ipv6");

        var options = discovery.Help("traceroute 8.8.8.8 ", 19, Admin(), 80);
        options.Tokens.Should().Equal("port", "probe", "size", "source", "timeout", "ttl");

        var ipv6Line = "traceroute 2001:db8::1 ";
        var ipv6Options = discovery.Help(ipv6Line, ipv6Line.Length, Admin(), 80);
        ipv6Options.Tokens.Should().Equal("port", "probe", "size", "source", "timeout", "ttl");
    }

    /// <summary>
    /// When IPv6 is unavailable, traceroute help still offers destination and never an ipv6 keyword.
    /// </summary>
    [Fact]
    public void Help_Traceroute_DestinationHelp_WhenUnavailable()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new NeverIpv6());
        var result = discovery.Help("traceroute ", 11, Admin(), 80);
        result.Tokens.Should().NotContain("ipv6");
        result.Tokens.Should().Contain("<destination>");
    }

    /// <summary>
    /// Traceroute source address completion is alphabetical independent of registration order.
    /// </summary>
    [Fact]
    public void Complete_TracerouteSource_IsAlphabetical()
    {
        var discovery = new CliCommandDiscovery(
            ipv6Availability: new FixedAddresses(
                ipv4:
                [
                    System.Net.IPAddress.Parse("192.168.1.10"),
                    System.Net.IPAddress.Parse("10.0.0.1"),
                    System.Net.IPAddress.Parse("172.16.0.5")
                ]));
        var result = discovery.Complete("traceroute 8.8.8.8 source ", 26, Admin(), 80);
        result.Tokens.Should().Equal("10.0.0.1", "172.16.0.5", "192.168.1.10");
    }

    /// <summary>
    /// With IPv6 unavailable, ping help hides ipv6 and still offers session.
    /// </summary>
    [Fact]
    public void Help_Ping_HidesIpv6WhenUnavailable()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new NeverIpv6());
        var result = discovery.Help("ping ", 5, Admin(), 80);
        result.Tokens.Should().NotContain("ipv6");
        result.Tokens.Should().Contain("session");
    }

    /// <summary>
    /// <c>ping session</c> completion uses the connector registry for unique session-id prefixes.
    /// </summary>
    [Fact]
    public void Complete_PingSession_UsesRegistry()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry, new NeverIpv6());
        var line = "ping session " + session.SessionId[..2];
        var result = discovery.Complete(line, line.Length, Admin(), 80);
        result.CompletionText.Should().Be(session.SessionId);
    }

    /// <summary>
    /// Empty-line top-level completion lists authorized verbs alphabetically.
    /// </summary>
    [Fact]
    public void Complete_TopLevel_IsAlphabetical()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("", 0, Admin(), 80);
        result.Tokens.Should().Equal(
            "attach", "clear", "configure", "connect", "copy", "disable", "disconnect", "enable", "exit", "help", "nslookup", "ping", "show", "traceroute", "write");
    }

    /// <summary>
    /// Session-id completion sorts alphabetically regardless of registration order.
    /// </summary>
    [Fact]
    public void Complete_SessionIds_AreAlphabetical_IndependentOfRegistrationOrder()
    {
        var registry = new ConnectorSessionRegistry();
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var session = registry.TryRegister($"ID{i}", $"c{i}", null, $"host{i}", null, $"COM{i}", null, T0)
                .Session!;
            ids.Add(session.SessionId);
        }

        var expected = ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();

        var discovery = new CliCommandDiscovery(registry);
        var result = discovery.Complete("connect ", 8, Admin(), 80);
        result.Tokens.Should().Equal(expected);
    }

    /// <summary>
    /// After an IPv4 destination, ping options are listed alphabetically.
    /// </summary>
    [Fact]
    public void Help_PingIpv4Options_AreAlphabetical()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new NeverIpv6());
        var result = discovery.Help("ping 8.8.8.8 ", 13, Admin(), 80);
        result.Tokens.Should().Equal("df", "repeat", "size", "source", "sweep", "timeout");
    }

    /// <summary>
    /// After <c>sweep</c>, interval/max/min appear alphabetically.
    /// </summary>
    [Fact]
    public void Help_PingSweepOptions_AreAlphabetical()
    {
        var discovery = new CliCommandDiscovery(ipv6Availability: new NeverIpv6());
        var result = discovery.Help("ping 8.8.8.8 sweep ", 19, Admin(), 80);
        result.Tokens.Should().Equal("interval", "max", "min");
    }

    /// <summary>
    /// Ping source address completion sorts local IPv4 addresses alphabetically.
    /// </summary>
    [Fact]
    public void Complete_SourceAddresses_AreAlphabetical()
    {
        var discovery = new CliCommandDiscovery(
            ipv6Availability: new FixedAddresses(
                ipv4:
                [
                    System.Net.IPAddress.Parse("192.168.1.10"),
                    System.Net.IPAddress.Parse("10.0.0.1"),
                    System.Net.IPAddress.Parse("172.16.0.5")
                ]));
        var result = discovery.Complete("ping 8.8.8.8 source ", 20, Admin(), 80);
        result.Tokens.Should().Equal("10.0.0.1", "172.16.0.5", "192.168.1.10");
    }

    /// <summary>
    /// Ambiguous <c>dis</c> lists disable/disconnect and keeps common prefix <c>dis</c> after sorting.
    /// </summary>
    [Fact]
    public void Complete_Ambiguous_LongestCommonPrefix_UnchangedBySorting()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("dis", 3, Admin(), 80);
        result.Kind.Should().Be(CliDiscoveryKind.AmbiguousCompletion);
        result.Tokens.Should().Equal("disable", "disconnect");
        result.CompletionText.Should().Be("dis");
    }

    /// <summary>
    /// Level-1 <c>show</c> help filters unauthorized subcommands (e.g. serial) before alphabetical listing.
    /// </summary>
    [Fact]
    public void Help_AuthorizationFiltersBeforeAlphabeticalPresentation()
    {
        var discovery = new CliCommandDiscovery();
        var level1 = discovery.Help("show ", 5, ReadOnly(), 80);
        level1.Tokens.Should().Equal(
            "clock", "dns", "history", "ip", "privilege", "session", "sessions", "terminal", "users", "version");
        level1.Tokens.Should().NotContain("serial");
        level1.Tokens.Should().Contain("history");
    }
    /// <summary>Fake IPv6 availability enabled with one documentation global address.</summary>
    private sealed class AlwaysIpv6 : TechConnect.Server.Diagnostics.IIPv6NetworkAvailability
    {
        /// <inheritdoc />
        public bool IsIpv6PingAvailable => true;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalIpv4Addresses() => [];
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalGlobalIpv6Addresses() =>
            [System.Net.IPAddress.Parse("2001:db8::1")];
    }
    /// <summary>Fake IPv6 availability disabled with no local addresses.</summary>
    private sealed class NeverIpv6 : TechConnect.Server.Diagnostics.IIPv6NetworkAvailability
    {
        /// <inheritdoc />
        public bool IsIpv6PingAvailable => false;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalIpv4Addresses() => [];
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalGlobalIpv6Addresses() => [];
    }
    /// <summary>Returns a fixed IPv4 source-address list for alphabetical completion tests.</summary>
    private sealed class FixedAddresses : TechConnect.Server.Diagnostics.IIPv6NetworkAvailability
    {
        /// <summary>
        /// Fixed IPv4 addresses returned by <see cref="GetLocalIpv4Addresses"/> for completion tests.
        /// </summary>
        private readonly IReadOnlyList<System.Net.IPAddress> _ipv4;

        /// <summary>Stores the IPv4 addresses returned by <see cref="GetLocalIpv4Addresses"/>.</summary>
        public FixedAddresses(IReadOnlyList<System.Net.IPAddress> ipv4) => _ipv4 = ipv4;

        /// <inheritdoc />
        public bool IsIpv6PingAvailable => false;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalIpv4Addresses() => _ipv4;
        /// <inheritdoc />
        public IReadOnlyList<System.Net.IPAddress> GetLocalGlobalIpv6Addresses() => [];
    }

    /// <summary>
    /// Level 1 and 7 both discover connect/attach and never set/detach/terminal.
    /// </summary>
    [Fact]
    public void Help_Level1_ShowsConnectAndAttach_NotRemovedCommands()
    {
        var discovery = new CliCommandDiscovery();
        var level1 = discovery.Help("", 0, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)), 80);
        level1.Tokens.Should().Contain("connect");
        level1.Tokens.Should().Contain("attach");
        level1.Tokens.Should().NotContain(["set", "detach", "terminal"]);

        var level7 = discovery.Help("", 0, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(7)), 80);
        level7.Tokens.Should().Contain("connect");
        level7.Tokens.Should().Contain("attach");
        level7.Tokens.Should().NotContain(["set", "detach", "terminal"]);
    }

    /// <summary>
    /// Admin <c>show</c> help lists all subcommands with session-list and audit descriptions.
    /// </summary>
    [Fact]
    public void Help_ShowSubcommands()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("show ", 5, Admin(), 80);
        result.Tokens.Should().Equal(
            "clock",
            "dns",
            "history",
            "ip",
            "privilege",
            "running-config",
            "serial",
            "session",
            "sessions",
            "startup-config",
            "terminal",
            "users",
            "version");
        result.Descriptions.Should().Contain(d => d.Contains("List connector sessions", StringComparison.OrdinalIgnoreCase));
        result.Descriptions.Should().Contain(d => d.Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>show</c>/<c>show history</c>/<c>nslookup</c> help tokens change with privilege gates.
    /// </summary>
    [Fact]
    public void Help_ShowSubcommands_RespectPrivilege()
    {
        var discovery = new CliCommandDiscovery();
        var level1Show = new[]
        {
            "clock", "dns", "history", "ip", "privilege", "session", "sessions", "terminal", "users", "version"
        };
        discovery.Help("show ", 5, ReadOnly(), 80).Tokens.Should().Equal(level1Show);
        discovery.Help("show ", 5, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(3)), 80)
            .Tokens.Should().Equal(level1Show);
        discovery.Help("show ", 5, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(5)), 80)
            .Tokens.Should().Equal(level1Show);
        discovery.Help("show ", 5, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(7)), 80)
            .Tokens.Should().Equal(
                "clock",
                "dns",
                "history",
                "ip",
                "privilege",
                "serial",
                "session",
                "sessions",
                "terminal",
                "users",
                "version");
        discovery.Help("show history ", 13, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)), 80)
            .Kind.Should().Be(CliDiscoveryKind.None);
        discovery.Help("show history ", 13, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(5)), 80)
            .Tokens.Should().Equal("session");
        discovery.Help("show history ", 13, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(11)), 80)
            .Tokens.Should().Equal("session", "user");
        discovery.Help("show sessions ", 14, ReadOnly(), 80).Tokens.Should().Equal("detail");
        discovery.Help("show ip ", 8, ReadOnly(), 80).Tokens.Should().Equal("interfaces", "route");
        discovery.Help("nslookup ", 9, ReadOnly(), 80).DisplayText.Should().Contain("<hostname>");
    }

    /// <summary>
    /// Help after <c>show session</c> describes the <c>&lt;session-id&gt;</c> argument.
    /// </summary>
    [Fact]
    public void Help_ShowSession_ShowsArgument()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("show session ", 13, Admin(), 80);
        result.DisplayText.Should().Contain("<session-id>");
        result.DisplayText.Should().Contain("Connector session identifier");
    }

    /// <summary>
    /// Help for removed <c>set baud</c> returns None.
    /// </summary>
    [Fact]
    public void Help_SetBaud_IsRemoved()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Help("set baud ", 9, Admin(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// Partial top-level help for <c>sh</c> offers only <c>show</c>.
    /// </summary>
    [Fact]
    public void Help_PartialTopLevel()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Help("sh", 2, Admin(), 80);
        result.Tokens.Should().ContainSingle().Which.Should().Be("show");
    }

    /// <summary>
    /// Level 1 can discover connect/attach/traceroute via partials but not set/terminal/detach.
    /// </summary>
    [Fact]
    public void Level1_CanDiscoverAttach_CannotDiscoverRemovedCommandsViaPartial()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete("c", 1, ReadOnly(), 80).Tokens.Should().Contain("connect");
        discovery.Complete("set", 3, ReadOnly(), 80).Kind.Should().Be(CliDiscoveryKind.None);
        discovery.Complete("a", 1, ReadOnly(), 80).Tokens.Should().Contain("attach");
        discovery.Help("att", 3, ReadOnly(), 80).Tokens.Should().Contain("attach");
        discovery.Complete("t", 1, ReadOnly(), 80).Tokens.Should().Contain("traceroute");
        discovery.Help("ter", 3, ReadOnly(), 80).Kind.Should().Be(CliDiscoveryKind.None);
        discovery.Complete("det", 3, ReadOnly(), 80).Kind.Should().Be(CliDiscoveryKind.None);
    }

    /// <summary>
    /// Discovery formatter normalizes invalid widths to 80 and preserves valid widths.
    /// </summary>
    [Fact]
    public void Formatter_DefaultWidth_Is80()
    {
        CliDiscoveryFormatter.NormalizeColumns(0).Should().Be(80);
        CliDiscoveryFormatter.NormalizeColumns(-1).Should().Be(80);
        CliDiscoveryFormatter.NormalizeColumns(120).Should().Be(120);
    }
    /// <summary>Administrator-boundary operator for unrestricted discovery.</summary>
    private static AuthenticatedOperator Admin() =>
        TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.AdministratorBoundary);

    /// <summary>Read-only boundary (privilege 1) operator for filtered discovery.</summary>
    private static AuthenticatedOperator ReadOnly() =>
        TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
}
