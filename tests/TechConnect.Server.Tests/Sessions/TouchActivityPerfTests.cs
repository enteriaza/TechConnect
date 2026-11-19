// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TouchActivityPerfTests.cs
//
// Purpose:
// Release contention probe for PERF-SRV-02 (registry TouchActivity vs cached session).
// Writes results to %TEMP%\TechConnect-PERF-SRV-02.txt (not a pass/fail gate).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Tests.Sessions;

/// <summary>
/// Measurement harness for connector activity-touch lock contention.
/// </summary>
public sealed class TouchActivityPerfTests
{
    /// <summary>
    /// Scenario: compare registry TouchActivity under fan-out vs cached session Volatile write.
    /// Expected: cached path sustains higher multi-thread ops/s; records metrics.
    /// </summary>
    [Fact]
    public void MeasureTouchActivityContention_RecordsMetrics()
    {
        const int sessionCount = 100;
        const int opsPerThread = 40_000;
        var registry = new ConnectorSessionRegistry();
        var sessions = new ConnectorSession[sessionCount];
        var t0 = DateTimeOffset.Parse("2026-11-19T12:00:00Z");
        for (var i = 0; i < sessionCount; i++)
        {
            sessions[i] = registry.TryRegister($"ID-{i}", $"conn-{i}", null, null, null, "COM1", null, t0).Session!;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
        sb.AppendLine($"runtime={Environment.Version}");
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine($"sessions={sessionCount} opsPerThread={opsPerThread}");
        sb.AppendLine();

        foreach (var threads in new[] { 1, 4, 8, 16 })
        {
            var locked = Measure(() =>
            {
                Parallel.For(0, threads, t =>
                {
                    var id = sessions[t % sessionCount].ConnectionId;
                    for (var i = 0; i < opsPerThread; i++)
                    {
                        registry.TouchActivity(id, t0.AddTicks(i + 1));
                    }
                });
            }, threads * opsPerThread);

            var cached = Measure(() =>
            {
                Parallel.For(0, threads, t =>
                {
                    var session = sessions[t % sessionCount];
                    for (var i = 0; i < opsPerThread; i++)
                    {
                        // Mirrors ConnectorListenerHostedService RX hot path (ObserveActivity).
                        session.ObserveActivity(t0.AddTicks(i + 1));
                    }
                });
            }, threads * opsPerThread);

            sb.AppendLine(
                $"threads={threads} registry_ops/s={locked.OpsPerSec:F0} registry_ns={locked.NsPerOp:F1} " +
                $"cached_ops/s={cached.OpsPerSec:F0} cached_ns={cached.NsPerOp:F1}");
        }

        var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-SRV-02.txt");
        File.WriteAllText(path, sb.ToString());

        // Sanity: at 8 threads cached should not be slower than registry touch.
        var reg8 = Measure(() =>
        {
            Parallel.For(0, 8, t =>
            {
                var id = sessions[t % sessionCount].ConnectionId;
                for (var i = 0; i < 10_000; i++)
                {
                    registry.TouchActivity(id, t0.AddTicks(i + 1));
                }
            });
        }, 80_000);
        var cache8 = Measure(() =>
        {
            Parallel.For(0, 8, t =>
            {
                var session = sessions[t % sessionCount];
                for (var i = 0; i < 10_000; i++)
                {
                    session.ObserveActivity(t0.AddTicks(i + 1));
                }
            });
        }, 80_000);
        cache8.OpsPerSec.Should().BeGreaterThan(reg8.OpsPerSec);

        // Allocation: ObserveActivity must not allocate per update (stack DateTimeOffset only).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var session = sessions[0];
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50_000; i++)
        {
            session.ObserveActivity(t0.AddTicks(i + 1));
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        sb.AppendLine($"ObserveActivity_alloc_bytes_per_50k={allocated}");
        File.WriteAllText(path, sb.ToString());
        allocated.Should().Be(0);
    }

    private static (double OpsPerSec, double NsPerOp) Measure(Action work, long totalOps)
    {
        work(); // warmup
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        return (totalOps / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), sw.Elapsed.TotalNanoseconds / totalOps);
    }
}
