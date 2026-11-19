// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    NocPrivilegeSmokeTests.cs
//
// Purpose:
// Smoke coverage for NOC privilege display formatting.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Inspection;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Inspection;

/// <summary>
/// End-to-end interactive smoke for privilege-1 NOC commands plus higher-privilege checks.
/// </summary>
public sealed class NocPrivilegeSmokeTests
{
    /// <summary>
    /// Interactive privilege-1 session runs the full NOC command set, asserts expected
    /// output fragments, permission-denied for restricted history/admin disconnect,
    /// ANSI clear, and a clean <c>exit</c>/Goodbye.
    /// </summary>
    [Fact]
    public async Task Privilege1_InteractiveSmoke_AllNewCommands()
    {
        var output = await RunAsync(
            TestAuthenticatedOperator.Create("noc1", PrivilegeLevel.FromInt(1), operatorSessionId: "smoke-1"),
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
                "nslookup localhost",
                "show history session deadbeef",
                "show history user alice",
                "disconnect deadbeef",
                "exit"
            ]);

        output.Should().Contain("Application");
        output.Should().Contain("Active privilege");
        output.Should().Contain("UTC time");
        output.Should().Contain("Operator session");
        output.Should().Contain("SSH operator");
        output.Should().ContainAny("No audit events", "audit events");
        output.Should().ContainAny("No active connector", "active session");
        output.Should().ContainAny("DNS", "resolvers");
        output.Should().ContainAny("Interface", "No network interfaces");
        output.Should().ContainAny("route", "Routing table", "FAMILY");
        output.Should().Contain("\u001b[2J\u001b[H");
        output.Should().ContainAny("localhost", "Lookup failed", "Address:");
        output.Should().Contain(OperatorAuthorizationService.PermissionDeniedMessage);
        // Permission denied appears for restricted history/admin disconnect — at least once.
        output.Split(OperatorAuthorizationService.PermissionDeniedMessage, StringSplitOptions.None)
            .Length.Should().BeGreaterThan(3);
        output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Privilege 7 keeps <c>show serial</c>, session history, and admin disconnect;
    /// privilege 15 adds user history; privilege 1 is still denied admin disconnect.
    /// </summary>
    [Fact]
    public void Privilege7_And_15_RetainHigherCommands()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher(
            applicationInfo: new FixedAppInfo(),
            dnsConfiguration: new EmptyDns(),
            interfaces: new EmptyNics(),
            routes: new EmptyRoutes());

        var p7 = TestAuthenticatedOperator.Create("ops7", PrivilegeLevel.FromInt(7));
        dispatcher.Execute("show serial", p7).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("show history session deadbeef", p7).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("show history user alice", p7).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        // Session may be absent; privilege 7 must not receive Permission denied for the admin form.
        dispatcher.Execute("disconnect deadbeef", p7).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);

        var p15 = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.FromInt(15));
        dispatcher.Execute("show history user alice", p15).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("show version", p15).Output.Should().Contain("Application");
        dispatcher.Execute("nslookup example.com", p15).Action.Should().Be(CliCommandAction.Nslookup);
        dispatcher.Execute("disconnect deadbeef", TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1)))
            .Output.Should().Be(OperatorAuthorizationService.PermissionDeniedMessage);
    }
    /// <summary>
    /// Drives a <see cref="TechConnectCliSession"/> over a duplex memory pipe, writing
    /// each command after the prompt appears and capturing UTF-8 client output.
    /// </summary>
    private static async Task<string> RunAsync(AuthenticatedOperator identity, string[] commands)
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher(
                applicationInfo: new FixedAppInfo(),
                dnsConfiguration: new EmptyDns(),
                interfaces: new EmptyNics(),
                routes: new EmptyRoutes());
        var dns = new LocalhostDnsLookup();
        var operators = new TechConnect.Server.Ssh.Sessions.SshOperatorSessionTracker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var session = new TechConnectCliSession(
            pipe.Server,
            dispatcher,
            attachments,
            registry,
            tracker,
            serialCache,
            pendingBaud,
            authorization,
            disconnect,
            NullLogger.Instance,
            identity,
            enable,
            dnsLookup: dns,
            operatorSessions: operators);

        var output = new StringBuilder();
        var readOutput = Task.Run(async () =>
        {
            var buf = new byte[2048];
            while (!cts.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await pipe.Client.ReadAsync(buf, cts.Token);
                }
                catch
                {
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                lock (output)
                {
                    output.Append(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
        });

        var run = session.RunAsync(cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("TechConnect>", StringComparison.Ordinal);
            }
        });

        foreach (var command in commands)
        {
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"), cts.Token);
            await Task.Delay(40, cts.Token);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(20));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        lock (output)
        {
            return output.ToString();
        }
    }
    /// <summary>Polls <paramref name="condition"/> until true or 5s elapses.</summary>
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("Timed out waiting for CLI prompt.");
            }

            await Task.Delay(20);
        }
    }
    /// <summary>Fixed application info so interactive smoke does not depend on the host process.</summary>
    private sealed class FixedAppInfo : IApplicationInfoProvider
    {
        /// <inheritdoc />
        public ApplicationInfo GetInfo() => new()
        {
            ApplicationName = "TechConnect.Server",
            Version = "9.9.9.9",
            RuntimeVersion = "10.0.0",
            ProtocolVersion = 1,
            StartedAtUtc = DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            Uptime = TimeSpan.FromMinutes(3)
        };
    }

    /// <summary>Empty DNS config snapshot (disabled / no servers).</summary>
    private sealed class EmptyDns : IDnsConfigurationProvider
    {
        /// <inheritdoc />
        public DnsConfigurationSnapshot GetConfiguration() => new();
    }

    /// <summary>Empty NIC list so interface output stays deterministic offline.</summary>
    private sealed class EmptyNics : INetworkInterfaceInspector
    {
        /// <inheritdoc />
        public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces() => [];
    }

    /// <summary>Empty routing table (available, zero routes).</summary>
    private sealed class EmptyRoutes : IRoutingTableInspector
    {
        /// <inheritdoc />
        public RoutingTableSnapshot GetRoutes() => new() { Routes = [] };
    }

    /// <summary>Always succeeds with loopback so <c>nslookup</c> does not hit real DNS.</summary>
    private sealed class LocalhostDnsLookup : IDnsLookupService
    {
        /// <inheritdoc />
        public Task<DnsLookupResult> LookupAsync(DnsLookupRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new DnsLookupResult
            {
                Hostname = request.Hostname,
                Success = true,
                Addresses = [System.Net.IPAddress.Loopback],
                Duration = TimeSpan.FromMilliseconds(1)
            });
    }
}
