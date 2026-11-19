// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteLiveNetworkTests.cs
//
// Purpose:
// Optional live-network traceroute qualification; skipped unless environment opts in.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Optional live checks. Set TECHCONNECT_LIVE_TRACEROUTE=1 to enable IPv4.
/// Set TECHCONNECT_LIVE_TRACEROUTE_IPV6=1 to enable IPv6 (requires usable IPv6).
/// </summary>
public sealed class TracerouteLiveNetworkTests
{
    /// <summary>
    /// Opt-in live IPv4 traceroute (<c>TECHCONNECT_LIVE_TRACEROUTE=1</c>) must receive TimeExceeded or EchoReply hops.
    /// </summary>
    [Fact]
    public async Task Live_IcmpEchoTraceroute_ReceivesHops_WhenEnabled()
    {
        if (!IsEnabled("TECHCONNECT_LIVE_TRACEROUTE") || !CanOpenRawIcmp(AddressFamily.InterNetwork))
        {
            return;
        }

        var destination = IPAddress.Parse(
            Environment.GetEnvironmentVariable("TECHCONNECT_LIVE_TRACEROUTE_DEST") ?? "198.18.255.255");

        var probes = await TraceAsync(
            new TracerouteRequest
            {
                Family = TracerouteAddressFamily.Ipv4,
                DestinationInput = destination.ToString(),
                Destination = destination,
                MaxTtl = 8,
                ProbesPerHop = 1,
                TimeoutSeconds = 2,
                PayloadBytes = 40
            });

        probes.Should().NotBeEmpty();
        probes.Should().Contain(p =>
            p.Status == TracerouteProbeStatus.TimeExceeded ||
            p.Status == TracerouteProbeStatus.EchoReply);
        probes.Should().Contain(p => p.Responder != null);
        probes.Should().NotContain(p => p.Status == TracerouteProbeStatus.Error);
    }

    /// <summary>
    /// Live IPv4 output hop rows share identical width and RTT column origin.
    /// </summary>
    [Fact]
    public async Task Live_IcmpEchoTraceroute_FormatsAlignedHopRows_WhenEnabled()
    {
        if (!IsEnabled("TECHCONNECT_LIVE_TRACEROUTE") || !CanOpenRawIcmp(AddressFamily.InterNetwork))
        {
            return;
        }

        var destination = IPAddress.Parse(
            Environment.GetEnvironmentVariable("TECHCONNECT_LIVE_TRACEROUTE_DEST") ?? "198.18.255.255");

        const int probesPerHop = 3;
        var request = new TracerouteRequest
        {
            Family = TracerouteAddressFamily.Ipv4,
            DestinationInput = destination.ToString(),
            Destination = destination,
            MaxTtl = 8,
            ProbesPerHop = probesPerHop,
            TimeoutSeconds = 2,
            PayloadBytes = 40
        };

        var probes = await TraceAsync(request);
        probes.Should().NotBeEmpty();

        var output = Render(request, probes, probesPerHop);
        Console.WriteLine(output);

        var hopLines = output.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Length > 0 && char.IsDigit(l.TrimStart()[0]))
            .ToList();

        hopLines.Should().NotBeEmpty();
        var rttStart = TracerouteOutputFormatter.HopWidth + 2 + TracerouteOutputFormatter.AddressWidth;
        foreach (var line in hopLines)
        {
            line.Length.Should().BeGreaterThanOrEqualTo(rttStart + TracerouteOutputFormatter.RttColumnWidth * probesPerHop);
            // Every hop line shares the same RTT column origin.
            line[rttStart - 1].Should().Be(' ');
        }

        hopLines.Select(l => l.Length).Distinct().Should().ContainSingle(
            "all hop rows must share identical width for column alignment");
    }

    /// <summary>
    /// Opt-in multi-destination run (<c>TECHCONNECT_LIVE_TRACEROUTE_MULTI=1</c>) exercises several targets without Error status.
    /// </summary>
    [Fact]
    public async Task Live_IcmpEchoTraceroute_MultiDestination_WhenEnabled()
    {
        if (!IsEnabled("TECHCONNECT_LIVE_TRACEROUTE_MULTI") || !CanOpenRawIcmp(AddressFamily.InterNetwork))
        {
            return;
        }

        var destinations = (Environment.GetEnvironmentVariable("TECHCONNECT_LIVE_TRACEROUTE_DESTS") ??
                            "198.18.255.255,www.saix.net,203.0.113.1")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var input in destinations)
        {
            IPAddress destination;
            try
            {
                destination = System.Net.Dns.GetHostAddresses(input)
                    .First(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SKIP {input}: {ex.Message}");
                continue;
            }

            var request = new TracerouteRequest
            {
                Family = TracerouteAddressFamily.Ipv4,
                DestinationInput = input,
                Destination = destination,
                MaxTtl = input.Equals("198.18.255.255", StringComparison.Ordinal) ? 8 : 16,
                ProbesPerHop = 3,
                TimeoutSeconds = 2,
                PayloadBytes = 40
            };

            var probes = await TraceAsync(request);
            Console.WriteLine(new string('=', 72));
            Console.WriteLine(Render(request, probes, request.ProbesPerHop));

            probes.Should().NotBeEmpty($"expected probes for {input}");
            probes.Should().NotContain(p => p.Status == TracerouteProbeStatus.Error);
        }
    }

    /// <summary>
    /// Opt-in live IPv6 traceroute; hop 1 must not falsely complete unless replies truly come from the destination.
    /// </summary>
    [Fact]
    public async Task Live_IcmpEchoTracerouteIpv6_ReceivesHops_WhenEnabled()
    {
        if (!IsEnabled("TECHCONNECT_LIVE_TRACEROUTE_IPV6") || !CanOpenRawIcmp(AddressFamily.InterNetworkV6))
        {
            return;
        }

        var destinationText =
            Environment.GetEnvironmentVariable("TECHCONNECT_LIVE_TRACEROUTE_IPV6_DEST") ?? "2001:4860:4860::8888";
        if (!IPAddress.TryParse(destinationText, out var destination) ||
            destination.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return;
        }

        var request = new TracerouteRequest
        {
            Family = TracerouteAddressFamily.Ipv6,
            DestinationInput = destination.ToString(),
            Destination = destination,
            MaxTtl = 16,
            ProbesPerHop = 3,
            TimeoutSeconds = 2,
            PayloadBytes = 40
        };

        var probes = await TraceAsync(request);
        Console.WriteLine(Render(request, probes, request.ProbesPerHop));

        probes.Should().NotBeEmpty();
        probes.Should().NotContain(p => p.Status == TracerouteProbeStatus.Error);

        var hop1 = probes.Where(p => p.Ttl == 1).ToList();
        hop1.Should().NotBeEmpty();

        // Must not declare destination reached solely because hop 1 got an Echo Reply from the target
        // when Hop Limit was not applied (the previous false-positive pattern).
        if (hop1.Any(p => p.Status == TracerouteProbeStatus.EchoReply && p.DestinationReached))
        {
            hop1.Should().OnlyContain(p =>
                p.Responder != null &&
                IcmpTracerouteParser.Ipv6AddressesEqual(p.Responder, destination));
            // Still require evidence of a genuine one-hop path: all hop-1 probes Echo Reply from dest.
            // Prefer Time Exceeded from an intermediate when the path has routers (typical).
        }
        else
        {
            hop1.Should().Contain(p =>
                p.Status == TracerouteProbeStatus.TimeExceeded ||
                p.Status == TracerouteProbeStatus.TimedOut);
            if (hop1.Any(p => p.Status == TracerouteProbeStatus.TimeExceeded))
            {
                hop1.Where(p => p.Responder != null)
                    .Should()
                    .OnlyContain(p => !IcmpTracerouteParser.Ipv6AddressesEqual(p.Responder!, destination));
            }
        }

        probes.Should().Contain(p =>
            p.Status == TracerouteProbeStatus.TimeExceeded ||
            p.Status == TracerouteProbeStatus.EchoReply ||
            p.Status == TracerouteProbeStatus.TimedOut);
    }

    /// <summary>
    /// Regression: hop 1 must not be three Echo Replies from the destination (wrong Hop Limit option).
    /// </summary>
    [Fact]
    public async Task Live_IcmpEchoTracerouteIpv6_DoesNotInventHop1Destination_WhenEnabled()
    {
        if (!IsEnabled("TECHCONNECT_LIVE_TRACEROUTE_IPV6") || !CanOpenRawIcmp(AddressFamily.InterNetworkV6))
        {
            return;
        }

        // Previously reported destination-as-hop-1 with Trace complete due to wrong Hop Limit option.
        var destination = IPAddress.Parse(
            Environment.GetEnvironmentVariable("TECHCONNECT_LIVE_TRACEROUTE_IPV6_FALSE_DEST")
            ?? "2001:4860:4826:7700::");

        var request = new TracerouteRequest
        {
            Family = TracerouteAddressFamily.Ipv6,
            DestinationInput = destination.ToString(),
            Destination = destination,
            MaxTtl = 8,
            ProbesPerHop = 3,
            TimeoutSeconds = 2,
            PayloadBytes = 40
        };

        var probes = await TraceAsync(request);
        Console.WriteLine(Render(request, probes, request.ProbesPerHop));

        var hop1 = probes.Where(p => p.Ttl == 1).ToList();
        hop1.Should().HaveCount(3);

        var falseComplete = hop1.All(p =>
            p.Status == TracerouteProbeStatus.EchoReply &&
            p.DestinationReached &&
            p.Responder != null &&
            IcmpTracerouteParser.Ipv6AddressesEqual(p.Responder, destination));

        falseComplete.Should().BeFalse(
            "hop 1 must not be three Echo Replies from the destination (Hop Limit not applied)");
    }
    /// <summary>Collects all probes from <see cref="RawSocketIcmpTracerouteClient.TraceAsync"/>.</summary>
    private static async Task<List<TracerouteProbeResult>> TraceAsync(TracerouteRequest request)
    {
        var client = new RawSocketIcmpTracerouteClient(NullLogger<RawSocketIcmpTracerouteClient>.Instance);
        var probes = new List<TracerouteProbeResult>();
        await foreach (var probe in client.TraceAsync(request, CancellationToken.None))
        {
            probes.Add(probe);
            if (probe.DestinationReached || probe.StopTracing)
            {
                // Finish remaining probes of this hop already yielded; stop collecting further hops
                // when destination/error terminates the async stream.
            }
        }

        return probes;
    }
    /// <summary>Renders header, hop lines, and footer for console/assert inspection.</summary>
    private static string Render(
        TracerouteRequest request,
        IReadOnlyList<TracerouteProbeResult> probes,
        int probesPerHop)
    {
        var sb = new StringBuilder();
        sb.Append(TracerouteOutputFormatter.FormatHeader(request));
        foreach (var group in probes.GroupBy(p => p.Ttl).OrderBy(g => g.Key))
        {
            sb.Append(TracerouteOutputFormatter.FormatHopLine(group.Key, group.ToList(), probesPerHop));
        }

        var reached = probes.Any(p => p.DestinationReached);
        var failed = probes.Any(p => p.StopTracing && !p.DestinationReached);
        sb.Append(TracerouteOutputFormatter.FormatFooter(reached, cancelled: false, failed));
        return sb.ToString();
    }
    /// <summary>True when the named environment variable equals <c>1</c>.</summary>
    private static bool IsEnabled(string name) =>
        string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
    /// <summary>Probes whether a raw ICMP socket can be bound for the address family.</summary>
    private static bool CanOpenRawIcmp(AddressFamily family)
    {
        try
        {
            var protocol = family == AddressFamily.InterNetworkV6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp;
            using var s = new Socket(family, SocketType.Raw, protocol);
            var any = family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            s.Bind(new IPEndPoint(any, 0));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
