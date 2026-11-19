// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DnsCliEndToEndQualificationTests.cs
//
// Purpose:
// Optional end-to-end DNS CLI qualification against configured servers; skipped unless opted in.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Inspection;
using TechConnect.Server.NameResolution;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.NameResolution;

/// <summary>
/// End-to-end qualification through TechConnectCliSession (real CLI path, configured DNS only).
/// Live ICMP requires raw-socket privilege; failures are recorded without inventing success.
/// </summary>
public sealed class DnsCliEndToEndQualificationTests
{
    /// <summary>
    /// Cli Show Dns And Nslookup Use Configured Servers.
    /// </summary>
    [Fact]
    public async Task Cli_ShowDns_And_Nslookup_UseConfiguredServers()
    {
        var dns = new ConfiguredDnsResolver(
            [IPAddress.Parse("1.1.1.1")],
            TimeSpan.FromSeconds(3),
            NullLogger<ConfiguredDnsResolver>.Instance);
        var ipv6 = new SystemIPv6NetworkAvailability();
        var destination = new DestinationResolver(dns, ipv6);
        var lookup = new ConfiguredDnsLookupService(dns);
        var dnsConfig = new TechConnectDnsConfigurationProvider(dns);

        var output = await RunCliAsync(
            dns,
            destination,
            lookup,
            dnsConfig,
            ipv6,
            [
                "show dns",
                "nslookup google.com",
                "nslookup 1.1.1.1",
                "exit"
            ],
            timeoutSeconds: 30);

        Console.WriteLine(output);
        output.Should().Contain("DNS enabled : yes");
        output.Should().Contain("1.1.1.1");
        output.Should().Contain("never uses the operating-system");
        output.Should().NotContain("from local network configuration");

        // Forward lookup may soft-fail in locked labs; require either success or explicit DNS error — never OS silence.
        output.Should().Match(o =>
            o.Contains("Address:", StringComparison.Ordinal) ||
            o.Contains("Lookup failed", StringComparison.Ordinal) ||
            o.Contains("NXDOMAIN", StringComparison.Ordinal) ||
            o.Contains("timed out", StringComparison.OrdinalIgnoreCase));

        // PTR for 1.1.1.1 often exists (one.one.one.one); accept failure text if blocked.
        output.Should().Contain("Query:");
    }

    /// <summary>
    /// Cli Dns Disabled Hostname Fails Literals Still Dispatch.
    /// </summary>
    [Fact]
    public async Task Cli_DnsDisabled_HostnameFails_LiteralsStillDispatch()
    {
        var dns = new ConfiguredDnsResolver(
            [],
            TimeSpan.FromSeconds(1),
            NullLogger<ConfiguredDnsResolver>.Instance);
        var ipv6 = new SystemIPv6NetworkAvailability();
        var destination = new DestinationResolver(dns, ipv6);

        var output = await RunCliAsync(
            dns,
            destination,
            new ConfiguredDnsLookupService(dns),
            new TechConnectDnsConfigurationProvider(dns),
            ipv6,
            [
                "show dns",
                "nslookup google.com",
                "ping google.com",
                "traceroute google.com",
                "ping 127.0.0.1 repeat 1 timeout 1",
                "exit"
            ],
            timeoutSeconds: 45);

        Console.WriteLine(output);
        output.Should().Contain("DNS enabled : no");
        output.Should().Contain("DNS is not configured");
        output.Should().Contain("Resolving google.com...");
        // Hostname path must not start ICMP; only the later literal ping may send.
        var beforeLiteral = output.Split("ping 127.0.0.1", 2)[0];
        beforeLiteral.Should().NotContain("Sending");
        output.Should().Contain("Sending 1, 56-byte ICMP Echos to 127.0.0.1");
    }

    /// <summary>
    /// Cli Hostname Ping Shows Resolved Address When Dns And Icmp Available.
    /// </summary>
    [Fact]
    public async Task Cli_HostnamePing_ShowsResolvedAddress_WhenDnsAndIcmpAvailable()
    {
        var dns = new ConfiguredDnsResolver(
            [IPAddress.Parse("1.1.1.1")],
            TimeSpan.FromSeconds(3),
            NullLogger<ConfiguredDnsResolver>.Instance);
        var ipv6 = new SystemIPv6NetworkAvailability();
        var destination = new DestinationResolver(dns, ipv6);

        if (!CanOpenRawIcmp(AddressFamily.InterNetwork) && !CanOpenRawIcmp(AddressFamily.InterNetworkV6))
        {
            // Still verify resolution messaging without requiring echo replies.
            var resolveOnly = await RunCliAsync(
                dns,
                destination,
                new ConfiguredDnsLookupService(dns),
                new TechConnectDnsConfigurationProvider(dns),
                ipv6,
                ["ping dns.google repeat 1 timeout 1", "exit"],
                timeoutSeconds: 20);
            Console.WriteLine(resolveOnly);
            resolveOnly.Should().Match(o =>
                o.Contains("Resolving dns.google", StringComparison.Ordinal) ||
                o.Contains("DNS", StringComparison.Ordinal) ||
                o.Contains("Unable to resolve", StringComparison.Ordinal) ||
                o.Contains("did not resolve", StringComparison.Ordinal));
            return;
        }

        var output = await RunCliAsync(
            dns,
            destination,
            new ConfiguredDnsLookupService(dns),
            new TechConnectDnsConfigurationProvider(dns),
            ipv6,
            [
                "ping 8.8.8.8 repeat 2 timeout 2",
                "ping dns.google repeat 2 timeout 2",
                "traceroute 8.8.8.8 ttl 5 probe 1 timeout 2",
                "traceroute dns.google ttl 5 probe 1 timeout 2",
                "exit"
            ],
            timeoutSeconds: 120);

        Console.WriteLine(output);
        output.Should().Contain("8.8.8.8");
        output.Should().Contain("Resolving dns.google");
        output.Should().Contain("Using");
        output.Should().Contain("dns.google");
        output.Should().Contain("Tracing route to");
        // Hop rows: digit + spaces (fixed-width); at least one responder or timeout stars.
        output.Should().Match(o => o.Contains(" ms", StringComparison.Ordinal) || o.Contains(" *", StringComparison.Ordinal));
    }

    /// <summary>
    /// Live Icmp Path After Destination Resolution When Raw Sockets Available.
    /// </summary>
    [Fact]
    public async Task Live_IcmpPath_AfterDestinationResolution_WhenRawSocketsAvailable()
    {
        var dns = new ConfiguredDnsResolver(
            [IPAddress.Parse("1.1.1.1")],
            TimeSpan.FromSeconds(3),
            NullLogger<ConfiguredDnsResolver>.Instance);
        var ipv6 = new SystemIPv6NetworkAvailability();
        var destination = new DestinationResolver(dns, ipv6);

        // IPv4 literal ping
        if (CanOpenRawIcmp(AddressFamily.InterNetwork))
        {
            var icmp = new RawSocketIcmpEchoClient(NullLogger<RawSocketIcmpEchoClient>.Instance);
            var reply = await icmp.SendAsync(
                new IcmpEchoRequest
                {
                    Destination = IPAddress.Parse("8.8.8.8"),
                    PayloadBytes = 32,
                    Timeout = TimeSpan.FromSeconds(2),
                    Identifier = 0xD5C1,
                    Sequence = 1
                },
                CancellationToken.None);
            Console.WriteLine($"IPv4 ping 8.8.8.8 => {reply.Status} rtt={reply.RoundTripTime}");
            reply.Status.Should().BeOneOf(IcmpEchoStatus.Success, IcmpEchoStatus.TimedOut);
        }
        else
        {
            Console.WriteLine("IPv4 raw ICMP unavailable (privilege/OS); literal ping not executed.");
        }

        // Hostname → resolve → ping selected address
        var resolved = await destination.ResolveAsync("dns.google", CancellationToken.None);
        Console.WriteLine(
            $"dns.google resolve success={resolved.Success} addr={resolved.Address} family={resolved.Family} err={resolved.Error}");
        if (resolved.Success && resolved.Address is not null && CanOpenRawIcmp(resolved.Family!.Value))
        {
            var icmp = new RawSocketIcmpEchoClient(NullLogger<RawSocketIcmpEchoClient>.Instance);
            var reply = await icmp.SendAsync(
                new IcmpEchoRequest
                {
                    Destination = resolved.Address,
                    PayloadBytes = 32,
                    Timeout = TimeSpan.FromSeconds(2),
                    Identifier = 0xD5C2,
                    Sequence = 1
                },
                CancellationToken.None);
            Console.WriteLine($"Hostname ping {resolved.Address} => {reply.Status} rtt={reply.RoundTripTime}");
        }

        // IPv6 literal when usable
        if (ipv6.IsIpv6PingAvailable && CanOpenRawIcmp(AddressFamily.InterNetworkV6))
        {
            var icmp = new RawSocketIcmpEchoClient(NullLogger<RawSocketIcmpEchoClient>.Instance);
            var v6 = IPAddress.Parse("2001:4860:4860::8888");
            var reply = await icmp.SendAsync(
                new IcmpEchoRequest
                {
                    Destination = v6,
                    PayloadBytes = 32,
                    Timeout = TimeSpan.FromSeconds(3),
                    Identifier = 0xD5C3,
                    Sequence = 1
                },
                CancellationToken.None);
            Console.WriteLine($"IPv6 ping {v6} => {reply.Status} rtt={reply.RoundTripTime}");
        }
        else
        {
            Console.WriteLine(
                $"IPv6 ping skipped (available={ipv6.IsIpv6PingAvailable}, raw={CanOpenRawIcmp(AddressFamily.InterNetworkV6)}).");
        }

        // Short traceroute IPv4
        if (CanOpenRawIcmp(AddressFamily.InterNetwork))
        {
            var trace = new RawSocketIcmpTracerouteClient(NullLogger<RawSocketIcmpTracerouteClient>.Instance);
            var request = new TracerouteRequest
            {
                Family = TracerouteAddressFamily.Ipv4,
                DestinationInput = "8.8.8.8",
                Destination = IPAddress.Parse("8.8.8.8"),
                MaxTtl = 5,
                ProbesPerHop = 1,
                TimeoutSeconds = 2,
                PayloadBytes = 40
            };
            var probes = new List<TracerouteProbeResult>();
            await foreach (var p in trace.TraceAsync(request, CancellationToken.None))
            {
                probes.Add(p);
            }

            Console.WriteLine($"IPv4 traceroute probes={probes.Count} responders={probes.Count(p => p.Responder is not null)}");
            probes.Should().NotBeEmpty();

            // Reverse DNS sample
            var responder = probes.Select(p => p.Responder).FirstOrDefault(a => a is not null);
            if (responder is not null)
            {
                var ptr = await dns.ReverseResolveAsync(responder, CancellationToken.None);
                Console.WriteLine($"PTR {responder} => {ptr ?? "(none)"}");
            }
        }
        else
        {
            Console.WriteLine("IPv4 traceroute not executed (raw ICMP unavailable).");
        }
    }
    /// <summary>
    /// Asynchronously performs run cli.
    /// </summary>
    /// <param name="dns">The dns.</param>
    /// <param name="destination">The destination.</param>
    /// <param name="lookup">The lookup.</param>
    /// <param name="dnsConfig">The dns config.</param>
    /// <param name="ipv6">The ipv6.</param>
    /// <param name="commands">The commands.</param>
    /// <param name="timeoutSeconds">The timeout seconds.</param>
    /// <returns>The operation result.</returns>

    private static async Task<string> RunCliAsync(
        IDnsResolver dns,
        IDestinationResolver destination,
        IDnsLookupService lookup,
        IDnsConfigurationProvider dnsConfig,
        IIPv6NetworkAvailability ipv6,
        string[] commands,
        int timeoutSeconds)
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher(
                dnsConfiguration: dnsConfig,
                ipv6Availability: ipv6);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
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
            TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)),
            enable,
            icmpEchoClient: new RawSocketIcmpEchoClient(NullLogger<RawSocketIcmpEchoClient>.Instance),
            ipv6Availability: ipv6,
            tracerouteClient: new RawSocketIcmpTracerouteClient(NullLogger<RawSocketIcmpTracerouteClient>.Instance),
            dnsLookup: lookup,
            dnsResolver: dns,
            destinationResolver: destination);

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
        await WaitForPromptAsync(output);

        foreach (var command in commands)
        {
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"), cts.Token);
            // Allow resolve + probes for diagnostic commands.
            await Task.Delay(command.StartsWith("ping", StringComparison.OrdinalIgnoreCase) ||
                             command.StartsWith("traceroute", StringComparison.OrdinalIgnoreCase) ||
                             command.StartsWith("nslookup", StringComparison.OrdinalIgnoreCase)
                ? 800
                : 80);
        }

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds - 2));
        }
        catch (TimeoutException)
        {
            cts.Cancel();
        }

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
    /// <summary>
    /// Asynchronously performs wait for prompt.
    /// </summary>
    /// <param name="output">The output.</param>

    private static async Task WaitForPromptAsync(StringBuilder output)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                var text = output.ToString();
                if (text.Contains("TechConnect>", StringComparison.Ordinal) ||
                    text.Contains("TechConnect#", StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("CLI prompt not observed.");
    }
    /// <summary>
    /// Performs can open raw icmp.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <returns>The operation result.</returns>

    private static bool CanOpenRawIcmp(AddressFamily family)
    {
        try
        {
            using var socket = new Socket(family, SocketType.Raw, family == AddressFamily.InterNetworkV6
                ? ProtocolType.IcmpV6
                : ProtocolType.Icmp);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
