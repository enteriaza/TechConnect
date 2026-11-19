// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteCliResilienceTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (TracerouteCliResilienceTests).
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Diagnostics;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for traceroute CLI resilience and abort behaviour.
/// </summary>
public sealed class TracerouteCliResilienceTests
{
    /// <summary>
    /// Verifies: Socket Init Failure Reports Error And Session Continues.
    /// </summary>
    [Fact]
    public async Task SocketInitFailure_ReportsError_AndSessionContinues()
    {
        var traceroute = new FakeIcmpTracerouteClient
        {
            ExceptionToThrow = new InvalidOperationException(
                "You must call the Bind method before performing this operation.")
        };

        var output = await RunAsync(
            traceroute,
            ["traceroute 8.8.8.8", "help", "exit"]);

        output.Should().Contain("Bind method");
        output.Should().Contain("Trace aborted");
        output.Should().Contain("Available commands");
        output.Should().Contain("Goodbye");
        traceroute.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies: Unexpected Diagnostic Exception Is Logged Boundary And Session Continues.
    /// </summary>
    [Fact]
    public async Task UnexpectedDiagnosticException_IsLoggedBoundary_AndSessionContinues()
    {
        var traceroute = new FakeIcmpTracerouteClient
        {
            ExceptionToThrow = new InvalidDataException("synthetic unexpected failure")
        };

        var output = await RunAsync(
            traceroute,
            ["traceroute 1.1.1.1", "show sessions", "exit"]);

        output.Should().Contain("Traceroute failed unexpectedly");
        output.Should().Contain("Trace aborted");
        output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Verifies: Network Style Error Result Does Not Kill Session.
    /// </summary>
    [Fact]
    public async Task NetworkStyleErrorResult_DoesNotKillSession()
    {
        var traceroute = new FakeIcmpTracerouteClient();
        traceroute.Enqueue(new TracerouteProbeResult
        {
            Ttl = 1,
            ProbeIndex = 1,
            Status = TracerouteProbeStatus.Error,
            Detail = "Traceroute socket initialization failed: access denied",
            StopTracing = true
        });

        var output = await RunAsync(
            traceroute,
            ["traceroute 8.8.8.8", "exit"]);

        output.Should().Contain("socket initialization failed");
        output.Should().Contain("Trace aborted");
        output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Verifies: Hop Is Rendered As Single Aligned Row Not Partial Probes.
    /// </summary>
    [Fact]
    public async Task HopIsRenderedAsSingleAlignedRow_NotPartialProbes()
    {
        var traceroute = new FakeIcmpTracerouteClient();
        traceroute.Enqueue(
            new TracerouteProbeResult
            {
                Ttl = 1,
                ProbeIndex = 1,
                Status = TracerouteProbeStatus.TimeExceeded,
                Responder = IPAddress.Parse("198.18.0.65"),
                RoundTripTime = TimeSpan.FromMilliseconds(14)
            },
            new TracerouteProbeResult
            {
                Ttl = 1,
                ProbeIndex = 2,
                Status = TracerouteProbeStatus.TimedOut
            },
            new TracerouteProbeResult
            {
                Ttl = 1,
                ProbeIndex = 3,
                Status = TracerouteProbeStatus.TimeExceeded,
                Responder = IPAddress.Parse("198.18.0.65"),
                RoundTripTime = TimeSpan.FromMilliseconds(13)
            },
            new TracerouteProbeResult
            {
                Ttl = 2,
                ProbeIndex = 1,
                Status = TracerouteProbeStatus.EchoReply,
                Responder = IPAddress.Parse("198.18.255.255"),
                RoundTripTime = TimeSpan.FromMilliseconds(2),
                DestinationReached = true
            },
            new TracerouteProbeResult
            {
                Ttl = 2,
                ProbeIndex = 2,
                Status = TracerouteProbeStatus.EchoReply,
                Responder = IPAddress.Parse("198.18.255.255"),
                RoundTripTime = TimeSpan.FromMilliseconds(3),
                DestinationReached = true
            },
            new TracerouteProbeResult
            {
                Ttl = 2,
                ProbeIndex = 3,
                Status = TracerouteProbeStatus.EchoReply,
                Responder = IPAddress.Parse("198.18.255.255"),
                RoundTripTime = TimeSpan.FromMilliseconds(4),
                DestinationReached = true
            });

        var output = await RunAsync(traceroute, ["traceroute 198.18.255.255", "exit"]);

        var hop1 = TracerouteOutputFormatter.FormatHopLine(
            1,
            [
                new TracerouteProbeResult
                {
                    Ttl = 1,
                    ProbeIndex = 1,
                    Status = TracerouteProbeStatus.TimeExceeded,
                    Responder = IPAddress.Parse("198.18.0.65"),
                    RoundTripTime = TimeSpan.FromMilliseconds(14)
                },
                new TracerouteProbeResult
                {
                    Ttl = 1,
                    ProbeIndex = 2,
                    Status = TracerouteProbeStatus.TimedOut
                },
                new TracerouteProbeResult
                {
                    Ttl = 1,
                    ProbeIndex = 3,
                    Status = TracerouteProbeStatus.TimeExceeded,
                    Responder = IPAddress.Parse("198.18.0.65"),
                    RoundTripTime = TimeSpan.FromMilliseconds(13)
                }
            ],
            probesPerHop: 3);

        output.Should().Contain(hop1.TrimEnd('\r', '\n'));
        output.Should().Contain("Trace complete.");
        output.Should().Contain("Goodbye");

        // Destination reached after hop 1 should still allow hop 2 row; no hop 3.
        output.Should().Contain("198.18.255.255");
        output.Should().NotContain("\r\n  3  ");
    }

    /// <summary>
    /// Verifies: Cancellation Completes Current Hop Row And Returns To Prompt.
    /// </summary>
    [Fact]
    public async Task Cancellation_CompletesCurrentHopRow_AndReturnsToPrompt()
    {
        var traceroute = new FakeIcmpTracerouteClient(delayPerProbe: TimeSpan.FromSeconds(5));
        traceroute.Enqueue(
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
            });

        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            TestAuthenticatedOperator.Create(),
            enable,
            tracerouteClient: traceroute);

        var output = new StringBuilder();
        var readOutput = Task.Run(async () =>
        {
            var buf = new byte[1024];
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
        await WaitForAsync(() => Snapshot(output).Contains("TechConnect#", StringComparison.Ordinal));

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("traceroute 8.8.8.8\r"), cts.Token);
        await WaitForAsync(() => Snapshot(output).Contains("Tracing route", StringComparison.Ordinal));

        await pipe.Client.WriteAsync(new byte[] { 0x03 }, cts.Token);
        await WaitForAsync(() => Snapshot(output).Contains("Trace cancelled", StringComparison.Ordinal));

        var text = Snapshot(output);
        // Cancel may land before the first delayed probe yields; unfinished slots are '*'.
        text.Should().MatchRegex(@"  1  .*\*.*\*.*\*");
        text.Should().Contain("Trace cancelled.");
        text.Should().NotContain("Trace complete.");

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("help\r"), cts.Token);
        await WaitForAsync(() => Snapshot(output).Contains("Available commands", StringComparison.Ordinal));
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        Snapshot(output).Should().Contain("Goodbye");
    }
    /// <summary>
    /// Asynchronously performs run.
    /// </summary>
    /// <param name="traceroute">The traceroute.</param>
    /// <param name="commands">The commands.</param>
    /// <returns>The operation result.</returns>

    private static async Task<string> RunAsync(IIcmpTracerouteClient traceroute, string[] commands)
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            TestAuthenticatedOperator.Create(),
            enable,
            tracerouteClient: traceroute);

        var output = new StringBuilder();
        var readOutput = Task.Run(async () =>
        {
            var buf = new byte[1024];
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
        await WaitForAsync(() => Snapshot(output).Contains("TechConnect#", StringComparison.Ordinal));

        foreach (var command in commands)
        {
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"), cts.Token);
            await Task.Delay(40);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        return Snapshot(output);
    }
    /// <summary>
    /// Performs snapshot.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <returns>The operation result.</returns>

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
    /// <summary>
    /// Asynchronously performs wait for.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeoutMs">The timeout ms.</param>

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met in time.");
    }
}
