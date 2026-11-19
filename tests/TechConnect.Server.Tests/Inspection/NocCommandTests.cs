// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    NocCommandTests.cs
//
// Purpose:
// Verifies NOC inspection command formatting and DNS/interface/route display paths with fakes.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Inspection;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Ssh.Sessions;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Inspection;

/// <summary>
/// Dispatcher-level coverage for privilege-1 NOC <c>show</c>/<c>nslookup</c>/<c>clear</c>
/// formatting against faked application, DNS, NIC, and routing providers.
/// </summary>
public sealed class NocCommandTests
{
    /// <summary>Fixed UTC timestamp for uptime, clock, and session fixtures.</summary>
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// <c>show version</c> renders application, protocol, runtime, and uptime from
    /// <see cref="IApplicationInfoProvider"/> without leaking filesystem paths or secrets.
    /// </summary>
    [Fact]
    public void ShowVersion_UsesApplicationInfoProvider()
    {
        var info = new FakeApplicationInfoProvider(new ApplicationInfo
        {
            ApplicationName = "TechConnect.Server",
            Version = "1.2.3.4",
            InformationalVersion = "1.2.3+abc",
            RuntimeVersion = "10.0.0",
            ProtocolVersion = 7,
            StartedAtUtc = T0,
            Uptime = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(5)
        });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(applicationInfo: info);
        var output = dispatcher.Execute("show version", Level1()).Output!;
        output.Should().Contain("TechConnect.Server");
        output.Should().Contain("1.2.3.4");
        output.Should().Contain("1.2.3+abc");
        output.Should().Contain(".NET 10.0.0");
        output.Should().Contain("Protocol    : 7");
        output.Should().Contain("2h 05m");
        output.Should().NotContain("C:\\");
        output.Should().NotContain("password");
    }

    /// <summary>
    /// <c>show privilege</c> labels login vs active levels and notes when they differ,
    /// without exposing password hashes or tokens.
    /// </summary>
    [Fact]
    public void ShowPrivilege_DistinguishesLoginAndActive()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var login = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(15), operatorSessionId: "op-1");
        var output = dispatcher.Execute(
                "show privilege",
                login,
                activePrivilege: PrivilegeLevel.FromInt(1))
            .Output!;
        output.Should().Contain("noc");
        output.Should().Contain("Login privilege    : 15");
        output.Should().Contain("Active privilege   : 1");
        output.Should().Contain("differs from login privilege");
        output.Should().NotContain("hash");
        output.Should().NotContain("token");
    }

    /// <summary>
    /// <c>show clock</c> formats UTC from the injected <see cref="TimeProvider"/> and
    /// includes local time plus time-zone labels.
    /// </summary>
    [Fact]
    public void ShowClock_UsesTimeProviderAndLocalZone()
    {
        var clock = new FakeTimeProvider(T0);
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(timeProvider: clock);
        var output = dispatcher.Execute("show clock", Level1()).Output!;
        output.Should().Contain("UTC time   : 2026-11-18 12:00:00Z");
        output.Should().Contain("Local time :");
        output.Should().Contain("Time zone  :");
    }

    /// <summary>
    /// <c>show terminal</c> reflects the calling operator's CLI session record
    /// (id, attachment, TERM, columns/rows), not connector registry state.
    /// </summary>
    [Fact]
    public void ShowTerminal_UsesSelfSessionRecord()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var login = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-term");
        var self = new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-term",
            Username = "noc",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            ConnectedAtUtc = T0,
            AttachedConnectorSessionId = "sess-1",
            TerminalModeActive = false,
            TermType = "xterm-256color",
            Columns = 120,
            Rows = 40
        };
        var output = dispatcher.Execute("show terminal", login, selfSession: self).Output!;
        output.Should().Contain("op-term");
        output.Should().Contain("interactive-cli");
        output.Should().Contain("sess-1");
        output.Should().Contain("xterm-256color");
        output.Should().Contain("Columns          : 120");
        output.Should().Contain("Rows             : 40");
    }

    /// <summary>
    /// <c>show users</c> lists SSH operator sessions (endpoints, attachments) and
    /// omits connector hostnames registered only in the connector registry.
    /// </summary>
    [Fact]
    public void ShowUsers_ListsOperatorSessions_NotConnectors()
    {
        var tracker = new SshOperatorSessionTracker();
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-a",
            Username = "alice",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = "10.0.0.5:22",
            ConnectedAtUtc = T0,
            Columns = 80,
            Rows = 24
        });
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-b",
            Username = "bob",
            LoginPrivilege = PrivilegeLevel.FromInt(7),
            ActivePrivilege = PrivilegeLevel.FromInt(7),
            RemoteEndpoint = "10.0.0.6:2222",
            ConnectedAtUtc = T0.AddMinutes(1),
            AttachedConnectorSessionId = "conn-1",
            Columns = 80,
            Rows = 24
        });
        var (dispatcher, registry, _, _, _, _, _, _, _, _) =
            CliTestFactory.CreateDispatcher(operatorSessions: tracker);
        registry.TryRegister("ID", "c1", null, "field-host", null, "COM1", null, T0);

        var output = dispatcher.Execute("show users", Level1()).Output!;
        output.Should().Contain("alice");
        output.Should().Contain("bob");
        output.Should().Contain("op-a");
        output.Should().Contain("10.0.0.5:22");
        output.Should().Contain("conn-1");
        output.Should().Contain("2 SSH operators");
        output.Should().NotContain("field-host");
        output.Should().NotContain("password");
    }

    /// <summary>
    /// <c>clear screen</c> returns <see cref="CliCommandAction.ClearScreen"/> so the
    /// interactive session can emit the ANSI clear sequence.
    /// </summary>
    [Fact]
    public void ClearScreen_ReturnsClearScreenAction()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        dispatcher.Execute("clear screen", Level1()).Action.Should().Be(CliCommandAction.ClearScreen);
    }

    /// <summary>
    /// <c>show sessions detail</c> expands hostname/version fields while preserving
    /// the same registration order as the compact <c>show sessions</c> list.
    /// </summary>
    [Fact]
    public void ShowSessionsDetail_ExpandsConnectorInfo_PreservesOrder()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var first = registry.TryRegister("ID-A", "c1", null, "host-a", "1.0.0", "COM1", null, T0).Session!;
        var second = registry.TryRegister("ID-B", "c2", null, "host-b", "2.0.0", "COM3", null, T0.AddSeconds(1)).Session!;

        var list = dispatcher.Execute("show sessions", Level1()).Output!;
        var detail = dispatcher.Execute("show sessions detail", Level1()).Output!;
        detail.Should().Contain(first.SessionId);
        detail.Should().Contain(second.SessionId);
        detail.Should().Contain("host-a");
        detail.Should().Contain("1.0.0");
        detail.Should().Contain("App version");
        detail.IndexOf(first.SessionId, StringComparison.Ordinal)
            .Should().BeLessThan(detail.IndexOf(second.SessionId, StringComparison.Ordinal));
        list.IndexOf(first.SessionId, StringComparison.Ordinal)
            .Should().BeLessThan(list.IndexOf(second.SessionId, StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>show dns</c> prints configured resolvers and timeout from
    /// <see cref="IDnsConfigurationProvider"/> and states that OS DNS is never used.
    /// </summary>
    [Fact]
    public void ShowDns_UsesConfigurationProvider()
    {
        var dns = new FakeDnsConfigurationProvider(new DnsConfigurationSnapshot
        {
            Enabled = true,
            Servers = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")],
            QueryTimeoutSeconds = 3
        });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(dnsConfiguration: dns);
        var output = dispatcher.Execute("show dns", Level1()).Output!;
        output.Should().Contain("1.1.1.1");
        output.Should().Contain("8.8.8.8");
        output.Should().Contain("DNS enabled : yes");
        output.Should().Contain("Query timeout");
        output.Should().Contain("never uses the operating-system");
        output.Should().NotContain("eth0");
    }

    /// <summary>
    /// When DNS is disabled with an empty server list, <c>show dns</c> reports
    /// <c>DNS enabled : no</c> and <c>none</c> for resolvers.
    /// </summary>
    [Fact]
    public void ShowDns_EmptyConfiguration()
    {
        var dns = new FakeDnsConfigurationProvider(new DnsConfigurationSnapshot
        {
            Enabled = false,
            Servers = [],
            QueryTimeoutSeconds = 3
        });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(dnsConfiguration: dns);
        var output = dispatcher.Execute("show dns", Level1()).Output!;
        output.Should().Contain("DNS enabled : no");
        output.Should().Contain("none");
    }

    /// <summary>
    /// <c>show ip interfaces</c> formats multi-address NICs (IPv4/IPv6 with prefixes)
    /// and omits MAC addresses from the operator-facing view.
    /// </summary>
    [Fact]
    public void ShowIpInterfaces_FormatsMultipleAddresses()
    {
        var nics = new FakeNetworkInterfaceInspector(
        [
            new NetworkInterfaceInfo
            {
                Name = "Ethernet",
                Description = "Contoso NIC",
                Status = OperationalStatus.Up,
                Type = NetworkInterfaceType.Ethernet,
                Ipv4Addresses =
                [
                    new NetworkAddressInfo { Address = IPAddress.Parse("10.0.0.10"), PrefixLength = 24 },
                    new NetworkAddressInfo { Address = IPAddress.Parse("10.0.0.11"), PrefixLength = 24 }
                ],
                Ipv6Addresses =
                [
                    new NetworkAddressInfo { Address = IPAddress.Parse("2001:db8::10"), PrefixLength = 64 }
                ]
            },
            new NetworkInterfaceInfo
            {
                Name = "Loopback",
                Status = OperationalStatus.Up,
                Type = NetworkInterfaceType.Loopback,
                Ipv4Addresses =
                [
                    new NetworkAddressInfo { Address = IPAddress.Loopback, PrefixLength = 8 }
                ]
            }
        ]);
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(interfaces: nics);
        var output = dispatcher.Execute("show ip interfaces", Level1()).Output!;
        output.Should().Contain("Ethernet");
        output.Should().Contain("Contoso NIC");
        output.Should().Contain("10.0.0.10/24");
        output.Should().Contain("10.0.0.11/24");
        output.Should().Contain("2001:db8::10/64");
        output.Should().Contain("Loopback");
        output.Should().NotContain("MAC");
    }

    /// <summary>
    /// <c>show ip route</c> emits separate IPv4/IPv6 sections with default routes
    /// and a route count footer from the fake routing snapshot.
    /// </summary>
    [Fact]
    public void ShowIpRoute_FormatsDefaultAndEntries()
    {
        var routes = new FakeRoutingTableInspector(new RoutingTableSnapshot
        {
            Routes =
            [
                new RouteEntry
                {
                    Family = AddressFamily.InterNetwork,
                    Destination = IPAddress.Any,
                    PrefixLength = 0,
                    NextHop = IPAddress.Parse("10.0.0.1"),
                    InterfaceName = "Ethernet",
                    Metric = 25,
                    IsDefault = true,
                    IsOnLink = false
                },
                new RouteEntry
                {
                    Family = AddressFamily.InterNetworkV6,
                    Destination = IPAddress.IPv6Any,
                    PrefixLength = 0,
                    NextHop = IPAddress.Parse("fe80::1"),
                    InterfaceName = "Ethernet",
                    Metric = 256,
                    IsDefault = true,
                    IsOnLink = false
                }
            ]
        });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(routes: routes);
        var output = dispatcher.Execute("show ip route", Level1()).Output!;
        output.Should().Contain("IPv4 Routing Table");
        output.Should().Contain("IPv6 Routing Table");
        output.Should().Contain("10.0.0.1");
        output.Should().Contain("fe80::1");
        output.Should().Contain("0.0.0.0/0");
        output.Should().Contain("2 routes");
    }

    /// <summary>
    /// When the routing inspector returns <c>UnavailableReason</c>,
    /// <c>show ip route</c> surfaces that platform message to the operator.
    /// </summary>
    [Fact]
    public void ShowIpRoute_UnsupportedPlatform_ReportsReason()
    {
        var routes = new FakeRoutingTableInspector(new RoutingTableSnapshot
        {
            UnavailableReason = "Routing table inspection is not available on this platform."
        });
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(routes: routes);
        dispatcher.Execute("show ip route", Level1()).Output.Should()
            .Contain("not available on this platform");
    }

    /// <summary>
    /// Bare <c>nslookup</c> without a hostname is incomplete at the catalog/parser
    /// layer (<c>% Incomplete command</c>), not a DNS lookup attempt.
    /// </summary>
    [Fact]
    public void Nslookup_MissingArgument_ShowsUsage()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        // Bare verb without hostname is incomplete at the catalog/parser layer.
        dispatcher.Execute("nslookup", Level1()).Output.Should().Be("% Incomplete command");
    }

    /// <summary>
    /// <c>nslookup example.com</c> returns <see cref="CliCommandAction.Nslookup"/>
    /// with the hostname captured for the interactive lookup runner.
    /// </summary>
    [Fact]
    public void Nslookup_ValidHostname_ReturnsAction()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var result = dispatcher.Execute("nslookup example.com", Level1());
        result.Action.Should().Be(CliCommandAction.Nslookup);
        result.NslookupHostname.Should().Be("example.com");
    }

    /// <summary>
    /// Privilege 1 may run the new NOC inspection verbs, but
    /// <c>show history session|user</c> and admin <c>disconnect &lt;id&gt;</c> remain denied.
    /// </summary>
    [Fact]
    public void Privilege1_AllNewCommandsAuthorized()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(
            applicationInfo: new FakeApplicationInfoProvider(new ApplicationInfo
            {
                ApplicationName = "TechConnect.Server",
                Version = "0.0.0",
                RuntimeVersion = "10.0",
                ProtocolVersion = 1,
                StartedAtUtc = T0,
                Uptime = TimeSpan.Zero
            }),
            dnsConfiguration: new FakeDnsConfigurationProvider(new DnsConfigurationSnapshot()),
            interfaces: new FakeNetworkInterfaceInspector([]),
            routes: new FakeRoutingTableInspector(new RoutingTableSnapshot { Routes = [] }));

        string[] commands =
        [
            "show version",
            "show privilege",
            "show clock",
            "show terminal",
            "show users",
            "show history",
            "show sessions",
            "show sessions detail",
            "show dns",
            "show ip interfaces",
            "show ip route",
            "clear screen",
            "nslookup example.com"
        ];

        foreach (var command in commands)
        {
            var result = dispatcher.Execute(command, Level1());
            result.Output.Should().NotBe(
                OperatorAuthorizationService.PermissionDeniedMessage,
                because: "{0} must be allowed at privilege 1",
                command);
        }

        dispatcher.Execute("show history session abc", Level1()).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("show history user alice", Level1()).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("disconnect abcdef12", Level1()).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }

    /// <summary>
    /// <see cref="NocDisplayFormatter.FormatDnsLookup"/> shows addresses and duration
    /// on success, and a clear failure line with the error text on failure.
    /// </summary>
    [Fact]
    public void DnsLookupFormatter_SuccessAndFailure()
    {
        var ok = NocDisplayFormatter.FormatDnsLookup(new DnsLookupResult
        {
            Hostname = "example.com",
            Success = true,
            Addresses = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1::248")],
            Duration = TimeSpan.FromMilliseconds(12)
        });
        ok.Should().Contain("example.com");
        ok.Should().Contain("93.184.216.34");
        ok.Should().Contain("2606:2800:220:1::248");
        ok.Should().Contain("12 ms");

        var fail = NocDisplayFormatter.FormatDnsLookup(new DnsLookupResult
        {
            Hostname = "no.such.host",
            Success = false,
            Error = "No such host is known.",
            Duration = TimeSpan.FromMilliseconds(5)
        });
        fail.Should().Contain("Lookup failed");
        fail.Should().Contain("No such host is known.");
    }

    /// <summary>
    /// The in-test <c>FakeDnsLookupService</c> can return success, failure, or honor
    /// cancellation via an infinite delay — used by interactive smoke paths.
    /// </summary>
    [Fact]
    public async Task FakeDnsLookup_SupportsSuccessFailureAndCancellation()
    {
        var okService = new FakeDnsLookupService(new DnsLookupResult
        {
            Hostname = "ok.example",
            Success = true,
            Addresses = [IPAddress.Loopback],
            Duration = TimeSpan.FromMilliseconds(1)
        });
        var ok = await okService.LookupAsync(new DnsLookupRequest { Hostname = "ok.example" }, CancellationToken.None);
        ok.Success.Should().BeTrue();

        var failService = new FakeDnsLookupService(new DnsLookupResult
        {
            Hostname = "bad.example",
            Success = false,
            Error = "Name resolution failure."
        });
        var fail = await failService.LookupAsync(new DnsLookupRequest { Hostname = "bad.example" }, CancellationToken.None);
        fail.Success.Should().BeFalse();
        fail.Error.Should().Contain("Name resolution failure");

        var cancelService = new FakeDnsLookupService(delay: Timeout.InfiniteTimeSpan);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = async () => await cancelService.LookupAsync(
            new DnsLookupRequest { Hostname = "slow.example" },
            cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
    /// <summary>Privilege-1 NOC operator fixture with a stable session id.</summary>
    private static AuthenticatedOperator Level1() =>
        TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-noc");

    /// <summary>Returns a fixed <see cref="ApplicationInfo"/> snapshot for <c>show version</c>.</summary>
    private sealed class FakeApplicationInfoProvider(ApplicationInfo info) : IApplicationInfoProvider
    {
        /// <inheritdoc />
        public ApplicationInfo GetInfo() => info;
    }

    /// <summary>Returns a fixed DNS configuration snapshot for <c>show dns</c>.</summary>
    private sealed class FakeDnsConfigurationProvider(DnsConfigurationSnapshot snapshot) : IDnsConfigurationProvider
    {
        /// <inheritdoc />
        public DnsConfigurationSnapshot GetConfiguration() => snapshot;
    }

    /// <summary>Returns a fixed NIC list for <c>show ip interfaces</c> (no live OS query).</summary>
    private sealed class FakeNetworkInterfaceInspector(IReadOnlyList<NetworkInterfaceInfo> interfaces)
        : INetworkInterfaceInspector
    {
        /// <inheritdoc />
        public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => interfaces;
    }

    /// <summary>Returns a fixed routing snapshot or unavailable reason for <c>show ip route</c>.</summary>
    private sealed class FakeRoutingTableInspector(RoutingTableSnapshot snapshot) : IRoutingTableInspector
    {
        /// <inheritdoc />
        public RoutingTableSnapshot GetRoutes() => snapshot;
    }

    /// <summary>
    /// In-memory <see cref="IDnsLookupService"/> that returns a configured result or
    /// delays until cancelled (does not talk to real DNS).
    /// </summary>
    private sealed class FakeDnsLookupService(DnsLookupResult? result = null, TimeSpan? delay = null) : IDnsLookupService
    {
        /// <inheritdoc />
        public async Task<DnsLookupResult> LookupAsync(DnsLookupRequest request, CancellationToken cancellationToken)
        {
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return result ?? new DnsLookupResult
            {
                Hostname = request.Hostname,
                Success = false,
                Error = "No result configured."
            };
        }
    }

    /// <summary>Frozen UTC clock; local zone forced to UTC for deterministic <c>show clock</c>.</summary>
    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;

        /// <inheritdoc />
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
