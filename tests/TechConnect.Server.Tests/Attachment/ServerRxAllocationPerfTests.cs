// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ServerRxAllocationPerfTests.cs
//
// Purpose:
// Release allocation probe for PERF-SRV-01 (FrameReader-style ownership + queue).
// Writes results to %TEMP%\TechConnect-PERF-SRV-01.txt (not a pass/fail gate).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Server.Attachment;

namespace TechConnect.Server.Tests.Attachment;

/// <summary>
/// Measurement harness for Server RX enqueue allocation (copy vs ownership transfer).
/// </summary>
public sealed class ServerRxAllocationPerfTests
{
    /// <summary>
    /// Scenario: compare defensive copy enqueue vs owned enqueue after a FrameReader read.
    /// Expected: completes and records metrics; does not assert absolute budgets.
    /// </summary>
    [Fact]
    public async Task MeasureServerRxEnqueue_RecordsMetrics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
        sb.AppendLine($"runtime={Environment.Version}");
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine();

        int[] sizes = [64, 256, 1024, 4096, 16_384, ProtocolLimits.DefaultMaxSerialDataPayloadBytes];
        sb.AppendLine("=== isolate: FrameReader-style alloc + queue enqueue (no FrameWriter/MemoryStream) ===");
        foreach (var size in sizes)
        {
            var ops = size >= 16_384 ? 10_000 : 50_000;
            var copy = MeasureEnqueueIsolate(size, ops, owned: false);
            var owned = MeasureEnqueueIsolate(size, ops, owned: true);
            sb.AppendLine(
                $"isolate size={size} ops={ops} " +
                $"copy_alloc/op={copy.AllocPerOp} copy_ops/s={copy.OpsPerSec:F0} copy_gen0={copy.Gen0} " +
                $"owned_alloc/op={owned.AllocPerOp} owned_ops/s={owned.OpsPerSec:F0} owned_gen0={owned.Gen0}");
        }

        sb.AppendLine();
        sb.AppendLine("=== integration: FrameReader read + queue enqueue (includes stream/writer alloc) ===");
        foreach (var size in sizes)
        {
            var ops = size >= 16_384 ? 5_000 : 20_000;
            var copy = await MeasurePathAsync(size, ops, owned: false);
            var owned = await MeasurePathAsync(size, ops, owned: true);
            sb.AppendLine(
                $"framed size={size} ops={ops} " +
                $"copy_alloc/op={copy.AllocPerOp} copy_ops/s={copy.OpsPerSec:F0} copy_gen0={copy.Gen0} " +
                $"owned_alloc/op={owned.AllocPerOp} owned_ops/s={owned.OpsPerSec:F0} owned_gen0={owned.Gen0}");
        }

        // Sustained owned path with real queue drain (slow consumer simulation: drain every N).
        var sustained = await MeasureSustainedOwnedAsync(payloadSize: 1024, frames: 20_000, drainEvery: 8);
        sb.AppendLine(
            $"sustained_owned size=1024 frames={sustained.Frames} " +
            $"alloc/op={sustained.AllocPerOp} ops/s={sustained.OpsPerSec:F0} gen0={sustained.Gen0} " +
            $"max_queued={sustained.MaxQueued}");

        var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-SRV-01.txt");
        await File.WriteAllTextAsync(path, sb.ToString());

        // Sanity: owned isolate must allocate ~1× payload vs ~2× for defensive copy.
        var midCopy = MeasureEnqueueIsolate(1024, 20_000, owned: false);
        var midOwned = MeasureEnqueueIsolate(1024, 20_000, owned: true);
        midOwned.AllocPerOp.Should().BeLessThan(midCopy.AllocPerOp);
        midOwned.AllocPerOp.Should().BeLessThan(1200);
        midCopy.AllocPerOp.Should().BeGreaterThan(1800);
    }

    private static (long AllocPerOp, double OpsPerSec, int Gen0) MeasureEnqueueIsolate(
        int payloadSize,
        int ops,
        bool owned)
    {
        var template = new byte[payloadSize];
        Random.Shared.NextBytes(template);
        var q = new BoundedByteQueue(Math.Max(256 * 1024, payloadSize * 16));

        for (var i = 0; i < 64; i++)
        {
            EnqueueOnce(q, template, owned);
            _ = q.DequeueAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            EnqueueOnce(q, template, owned);
            if ((i & 7) == 7)
            {
                for (var d = 0; d < 8; d++)
                {
                    _ = q.DequeueAsync(CancellationToken.None).GetAwaiter().GetResult();
                }
            }
        }

        sw.Stop();
        q.Dispose();
        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - a0) / ops;
        return (alloc, ops / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), GC.CollectionCount(0) - g0);
    }

    private static void EnqueueOnce(BoundedByteQueue q, byte[] template, bool owned)
    {
        // Simulate FrameReader transferring a dedicated payload array.
        var payload = new byte[template.Length];
        Buffer.BlockCopy(template, 0, payload, 0, template.Length);
        if (owned)
        {
            if (!q.TryEnqueueOwned(payload))
            {
                _ = q.DequeueAsync(CancellationToken.None).GetAwaiter().GetResult();
                q.TryEnqueueOwned(payload).Should().BeTrue();
            }
        }
        else if (!q.TryEnqueue(payload))
        {
            _ = q.DequeueAsync(CancellationToken.None).GetAwaiter().GetResult();
            q.TryEnqueue(payload).Should().BeTrue();
        }
    }

    private static async Task<(long AllocPerOp, double OpsPerSec, int Gen0)> MeasurePathAsync(
        int payloadSize,
        int ops,
        bool owned)
    {
        var payload = new byte[payloadSize];
        Random.Shared.NextBytes(payload);

        // Warmup
        for (var i = 0; i < 32; i++)
        {
            await RunOnceAsync(payload, owned, drain: true);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            await RunOnceAsync(payload, owned, drain: true);
        }

        sw.Stop();
        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - a0) / ops;
        return (alloc, ops / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), GC.CollectionCount(0) - g0);
    }

    private static async Task RunOnceAsync(byte[] payloadTemplate, bool owned, bool drain)
    {
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.SerialData, payloadTemplate);
        stream.Position = 0;
        var frame = await new FrameReader(stream).ReadAsync();
        frame.Should().NotBeNull();

        var q = new BoundedByteQueue(256 * 1024);
        if (owned)
        {
            q.TryEnqueueOwned(frame!.Value.Payload).Should().BeTrue();
        }
        else
        {
            q.TryEnqueue(frame!.Value.Payload).Should().BeTrue();
        }

        if (drain)
        {
            var chunk = await q.DequeueAsync(CancellationToken.None);
            chunk.Should().HaveCount(payloadTemplate.Length);
        }

        q.Dispose();
    }

    private static async Task<(int Frames, long AllocPerOp, double OpsPerSec, int Gen0, int MaxQueued)>
        MeasureSustainedOwnedAsync(int payloadSize, int frames, int drainEvery)
    {
        var payload = new byte[payloadSize];
        Random.Shared.NextBytes(payload);
        var q = new BoundedByteQueue(256 * 1024);
        var maxQueued = 0;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < frames; i++)
        {
            await using var stream = new MemoryStream();
            await using var writer = new FrameWriter(stream);
            await writer.WriteAsync(MessageType.SerialData, payload);
            stream.Position = 0;
            var frame = await new FrameReader(stream).ReadAsync();
            q.TryEnqueueOwned(frame!.Value.Payload).Should().BeTrue();
            maxQueued = Math.Max(maxQueued, q.QueuedBytes);
            if ((i + 1) % drainEvery == 0)
            {
                for (var d = 0; d < drainEvery; d++)
                {
                    _ = await q.DequeueAsync(CancellationToken.None);
                }
            }
        }

        sw.Stop();
        q.Dispose();
        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - a0) / frames;
        return (frames, alloc, frames / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), GC.CollectionCount(0) - g0, maxQueued);
    }
}
