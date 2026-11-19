// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteClientBehaviorTests.cs
//
// Purpose:
// Verifies RawSocketIcmpTracerouteClient sequencing, stop conditions, and error mapping with fakes where applicable.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Behaviour tests for ICMP Echo traceroute client contracts.
/// </summary>
public sealed class TracerouteClientBehaviorTests
{
    /// <summary>
    /// Fake Client Yields Probes In Order And Supports Cancellation.
    /// </summary>
    [Fact]
    public async Task FakeClient_YieldsProbesInOrder_AndSupportsCancellation()
    {
        var client = new FakeIcmpTracerouteClient();
        client.Enqueue(
            new TracerouteProbeResult
            {
                Ttl = 1,
                ProbeIndex = 1,
                Status = TracerouteProbeStatus.TimeExceeded,
                Responder = IPAddress.Parse("192.168.1.1"),
                RoundTripTime = TimeSpan.FromMilliseconds(1)
            },
            new TracerouteProbeResult
            {
                Ttl = 1,
                ProbeIndex = 2,
                Status = TracerouteProbeStatus.TimedOut
            },
            new TracerouteProbeResult
            {
                Ttl = 2,
                ProbeIndex = 1,
                Status = TracerouteProbeStatus.EchoReply,
                Responder = IPAddress.Parse("8.8.8.8"),
                RoundTripTime = TimeSpan.FromMilliseconds(20),
                DestinationReached = true
            });

        var request = new TracerouteRequest
        {
            Family = TracerouteAddressFamily.Ipv4,
            DestinationInput = "8.8.8.8",
            Destination = IPAddress.Parse("8.8.8.8"),
            MaxTtl = 5,
            ProbesPerHop = 3
        };

        var results = new List<TracerouteProbeResult>();
        await foreach (var probe in client.TraceAsync(request, CancellationToken.None))
        {
            results.Add(probe);
        }

        results.Should().HaveCount(3);
        results[0].Status.Should().Be(TracerouteProbeStatus.TimeExceeded);
        results[1].Status.Should().Be(TracerouteProbeStatus.TimedOut);
        results[2].DestinationReached.Should().BeTrue();
        client.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// Output Formatter Footer Distinguishes Outcomes.
    /// </summary>
    [Fact]
    public void OutputFormatter_FooterDistinguishesOutcomes()
    {
        TracerouteOutputFormatter.FormatFooter(destinationReached: true, cancelled: false, terminalFailure: false)
            .Should().Contain("Trace complete.");
        TracerouteOutputFormatter.FormatFooter(destinationReached: false, cancelled: true, terminalFailure: false)
            .Should().Contain("cancelled");
        TracerouteOutputFormatter.FormatFooter(destinationReached: false, cancelled: false, terminalFailure: false)
            .Should().Contain("not reached");
        TracerouteOutputFormatter.FormatFooter(destinationReached: false, cancelled: false, terminalFailure: true)
            .Should().Contain("aborted");
    }

    /// <summary>
    /// Port Progression Is Deterministic Without Overflow At Defaults.
    /// </summary>
    [Fact]
    public void PortProgression_IsDeterministicWithoutOverflowAtDefaults()
    {
        var last = TracerouteLimits.LastDestinationPort(
            TracerouteLimits.DefaultInitialPort,
            TracerouteLimits.DefaultMaxTtl,
            TracerouteLimits.DefaultProbesPerHop);
        last.Should().Be(33434 + 90 - 1);
        last.Should().BeLessThanOrEqualTo(TracerouteLimits.MaxPort);
    }

    /// <summary>
    /// Fake Client Propagates Socket Init Style Exception To Caller.
    /// </summary>
    [Fact]
    public async Task FakeClient_PropagatesSocketInitStyleException_ToCaller()
    {
        var client = new FakeIcmpTracerouteClient
        {
            ExceptionToThrow = new InvalidOperationException("You must call the Bind method before performing this operation.")
        };

        var request = new TracerouteRequest
        {
            Family = TracerouteAddressFamily.Ipv4,
            DestinationInput = "8.8.8.8",
            Destination = IPAddress.Parse("8.8.8.8")
        };

        var act = async () =>
        {
            await foreach (var _ in client.TraceAsync(request, CancellationToken.None))
            {
            }
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Bind method*");
    }

    /// <summary>
    /// Encode Sequence Unique Per Hop And Probe.
    /// </summary>
    [Fact]
    public void EncodeSequence_UniquePerHopAndProbe()
    {
        var seen = new HashSet<ushort>();
        for (var ttl = 1; ttl <= 30; ttl++)
        {
            for (var probe = 1; probe <= 3; probe++)
            {
                seen.Add(RawSocketIcmpTracerouteClient.EncodeSequence(ttl, probe)).Should().BeTrue();
            }
        }
    }
}
