// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialConsoleBufferPerfTests.cs
//
// Purpose:
// Release allocation/throughput probe for PERF-CLI-01/02 (SerialConsoleBuffer).
// Writes results to %TEMP%\TechConnect-PERF-CLI-01-02.txt (not a pass/fail gate).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Measurement harness for <see cref="SerialConsoleBuffer"/> enqueue/drain/overflow paths.
/// </summary>
public sealed class SerialConsoleBufferPerfTests
{
    /// <summary>
    /// Scenario: measure enqueue/drain allocation and throughput for representative workloads.
    /// Expected: completes and records metrics; asserts overflow path stays bounded.
    /// </summary>
    [Fact]
    public void MeasureEnqueueDrainOverflow_RecordsMetrics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
        sb.AppendLine($"runtime={Environment.Version}");
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine($"maxPendingDefault={SerialConsoleBuffer.DefaultMaxPendingBytes}");
        sb.AppendLine();

        Measure(sb, "small_1B_drained", chunkSize: 1, ops: 100_000, drainEvery: 8, cap: null);
        Measure(sb, "small_16B_drained", chunkSize: 16, ops: 50_000, drainEvery: 8, cap: null);
        Measure(sb, "med_64B_drained", chunkSize: 64, ops: 50_000, drainEvery: 8, cap: null);
        Measure(sb, "med_256B_drained", chunkSize: 256, ops: 20_000, drainEvery: 8, cap: null);
        Measure(sb, "large_1KiB_drained", chunkSize: 1024, ops: 10_000, drainEvery: 8, cap: null);
        Measure(sb, "large_4KiB_drained", chunkSize: 4096, ops: 5_000, drainEvery: 8, cap: null);
        Measure(sb, "sustained_256B_drained", chunkSize: 256, ops: 50_000, drainEvery: 8, cap: null);
        Measure(sb, "overflow_1KiB_64KiBcap", chunkSize: 1024, ops: 20_000, drainEvery: 0, cap: 65_536);
        Measure(sb, "overflow_4KiB_1MiBcap", chunkSize: 4096, ops: 4_000, drainEvery: 0, cap: SerialConsoleBuffer.DefaultMaxPendingBytes);
        Measure(sb, "slow_consumer_256B", chunkSize: 256, ops: 20_000, drainEvery: 16, cap: null);

        var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-CLI-01-02.txt");
        File.WriteAllText(path, sb.ToString());

        // Sanity: overflow workload must leave pending at soft cap and report drops.
        var overflow = new SerialConsoleBuffer(maxPendingBytes: 4096);
        for (var i = 0; i < 20; i++)
        {
            overflow.Enqueue(TestHelpers.Chunk(new byte[1024]));
        }

        overflow.PendingByteCount.Should().Be(4096);
        overflow.TryDrain(out var data, out var dropped).Should().BeTrue();
        data.Length.Should().Be(4096);
        dropped.Should().BeGreaterThan(0);
    }

    private static void Measure(
        System.Text.StringBuilder sb,
        string name,
        int chunkSize,
        int ops,
        int drainEvery,
        int? cap)
    {
        var buffer = new SerialConsoleBuffer(cap ?? SerialConsoleBuffer.DefaultMaxPendingBytes);
        var payload = new byte[chunkSize];
        Random.Shared.NextBytes(payload);
        var chunk = TestHelpers.Chunk(payload);

        for (var i = 0; i < 32; i++)
        {
            buffer.Enqueue(chunk);
            if (drainEvery > 0 && (i % drainEvery) == drainEvery - 1)
            {
                _ = buffer.TryDrain(out _, out _);
            }
        }

        buffer.ClearPending();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        long dropped = 0;
        var maxPending = 0;
        for (var i = 0; i < ops; i++)
        {
            buffer.Enqueue(chunk);
            maxPending = Math.Max(maxPending, buffer.PendingByteCount);
            if (drainEvery > 0 && (i % drainEvery) == drainEvery - 1)
            {
                if (buffer.TryDrain(out _, out var d))
                {
                    dropped += d;
                }
            }
        }

        if (drainEvery == 0)
        {
            dropped = buffer.PendingBytesDropped;
        }

        sw.Stop();
        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - a0) / Math.Max(ops, 1);
        var seconds = Math.Max(sw.Elapsed.TotalSeconds, 1e-9);
        var bytes = (long)chunkSize * ops;
        sb.AppendLine(
            $"CASE {name} chunk={chunkSize} ops={ops} " +
            $"alloc/op={alloc} ops/s={ops / seconds:F0} MiB/s={bytes / seconds / (1024 * 1024):F1} " +
            $"gen0={GC.CollectionCount(0) - g0} maxPending={maxPending} dropped={dropped}");
    }
}
