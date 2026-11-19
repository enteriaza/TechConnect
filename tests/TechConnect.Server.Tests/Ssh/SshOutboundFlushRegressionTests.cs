// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshOutboundFlushRegressionTests.cs
//
// Purpose:
// PERF-SRV-03 regression: TechConnectCliSession must not Flush after every
// gated SSH write (SshStream.Flush is a no-op; FlushAsync was dead overhead).
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Ensures operator outbound writes do not invoke stream Flush/FlushAsync.
/// </summary>
public sealed class SshOutboundFlushRegressionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-19T16:00:00Z");

    /// <summary>
    /// Scenario: privilege-1 terminal RX pump delivers device bytes to the operator stream.
    /// Expected: bytes arrive; FlushCountingStream observes zero flush calls (PERF-SRV-03).
    /// </summary>
    [Fact]
    public async Task TerminalRxPump_DoesNotFlushAfterEachWrite()
    {
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1), operatorSessionId: "op-flush");
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-flush", null, "host", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            await using var pipe = new DuplexMemoryPipe();
            var counted = new FlushCountingStream(pipe.Server);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var session = new TechConnectCliSession(
                counted,
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
                enable);

            var output = new StringBuilder();
            var reader = Task.Run(async () =>
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
            await WaitForAsync(() => Snapshot(output).Contains("TechConnect>", StringComparison.Ordinal));

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"connect {registered.SessionId}\r"),
                cts.Token);
            await pendingBaud.CompletePendingSerialConfigAsync(registered.SessionId, identity.OperatorSessionId);
            await WaitForAsync(() => Snapshot(output).Contains("Connected to", StringComparison.Ordinal));

            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out var attachment)
                .Should().BeTrue();
            await WaitForAsync(() => attachment!.IsTerminalActive);

            // Baseline flush count after connect/banner (must remain unchanged through RX pump).
            var flushesBeforeRx = counted.FlushCalls;

            var rx = Encoding.UTF8.GetBytes("flush-regression-payload\r\n");
            for (var i = 0; i < 8; i++)
            {
                attachments.TryEnqueueDeviceRx(registered.SessionId, rx)
                    .Should().Be(DeviceRxEnqueueResult.Enqueued);
            }

            await WaitForAsync(() => attachment!.BytesToOperator >= rx.Length * 8);
            await WaitForAsync(() => Snapshot(output).Contains("flush-regression-payload", StringComparison.Ordinal));

            counted.FlushCalls.Should().Be(
                flushesBeforeRx,
                "WriteRawAsync must not Flush/FlushAsync after writes (PERF-SRV-03)");
            counted.FlushCalls.Should().Be(0);

            // Leave terminal then exit so RunAsync can complete cleanly.
            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() => !attachment!.IsTerminalActive);
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            try
            {
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Reader may already be cancelled.
            }
        }
    }

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
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
