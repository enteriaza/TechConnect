// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRxOwnershipPerfTests.cs
//
// Purpose:
// Release allocation probe for PERF-CLI-01 (RX ownership local vs remote).
// Writes results to %TEMP%\TechConnect-PERF-CLI-01.txt (not a pass/fail gate).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Serial;

/// <summary>
/// Measurement harness mirroring SerialConnectionService RX publish ownership rules.
/// </summary>
public sealed class SerialRxOwnershipPerfTests
{
    /// <summary>
    /// Scenario: compare local-ephemeral vs remote-durable enqueue allocation.
    /// Expected: local path allocates far less than remote-durable copy path.
    /// </summary>
    [Fact]
    public void MeasureLocalVsRemoteOwnership_RecordsMetrics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
        sb.AppendLine($"runtime={Environment.Version}");
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine("readBuffer=4096");
        sb.AppendLine();

        int[] sizes = [16, 64, 256, 1024, 4096];
        foreach (var size in sizes)
        {
            var ops = size >= 4096 ? 10_000 : 30_000;
            var local = Measure(size, ops, remoteEnabled: false);
            var remote = Measure(size, ops, remoteEnabled: true);
            sb.AppendLine(
                $"size={size} ops={ops} " +
                $"local_alloc/op={local.AllocPerOp} local_BperB={local.AllocPerOp / (double)size:F2} " +
                $"local_ops/s={local.OpsPerSec:F0} local_gen0={local.Gen0} " +
                $"remote_alloc/op={remote.AllocPerOp} remote_BperB={remote.AllocPerOp / (double)size:F2} " +
                $"remote_ops/s={remote.OpsPerSec:F0} remote_gen0={remote.Gen0}");
        }

        var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-CLI-01.txt");
        File.WriteAllText(path, sb.ToString());

        var midLocal = Measure(1024, 10_000, remoteEnabled: false);
        var midRemote = Measure(1024, 10_000, remoteEnabled: true);
        midLocal.AllocPerOp.Should().BeLessThan(midRemote.AllocPerOp);
        midRemote.AllocPerOp.Should().BeGreaterThan(1000);
    }

    private static (long AllocPerOp, double OpsPerSec, int Gen0) Measure(int readSize, int ops, bool remoteEnabled)
    {
        var streamBuffer = new byte[4096];
        Random.Shared.NextBytes(streamBuffer);
        var console = new SerialConsoleBuffer(maxPendingBytes: 65_536);
        var remoteQueue = new Queue<SerialDataChunk>();

        for (var i = 0; i < 32; i++)
        {
            PublishOnce(streamBuffer, readSize, remoteEnabled, console, remoteQueue);
            if ((i & 7) == 7)
            {
                _ = console.TryDrain(out _, out _);
                while (remoteQueue.Count > 4)
                {
                    remoteQueue.Dequeue();
                }
            }
        }

        console.ClearPending();
        remoteQueue.Clear();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            PublishOnce(streamBuffer, readSize, remoteEnabled, console, remoteQueue);
            if ((i & 7) == 7)
            {
                _ = console.TryDrain(out _, out _);
                while (remoteQueue.Count > 4)
                {
                    remoteQueue.Dequeue();
                }
            }
        }

        sw.Stop();
        var alloc = (GC.GetTotalAllocatedBytes(precise: true) - a0) / ops;
        return (alloc, ops / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), GC.CollectionCount(0) - g0);
    }

    /// <summary>
    /// Mirrors <c>SerialConnectionService.ReadLoopAsync</c> ownership decision + console enqueue.
    /// </summary>
    private static void PublishOnce(
        byte[] streamBuffer,
        int readSize,
        bool remoteEnabled,
        SerialConsoleBuffer console,
        Queue<SerialDataChunk> remoteQueue)
    {
        readSize = Math.Min(readSize, streamBuffer.Length);
        ReadOnlyMemory<byte> payloadMemory;
        if (remoteEnabled)
        {
            var payload = GC.AllocateUninitializedArray<byte>(readSize);
            System.Buffer.BlockCopy(streamBuffer, 0, payload, 0, readSize);
            payloadMemory = payload;
        }
        else
        {
            payloadMemory = streamBuffer.AsMemory(0, readSize);
        }

        var chunk = new SerialDataChunk
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = SerialDataDirection.Rx,
            Data = payloadMemory
        };

        if (remoteEnabled)
        {
            remoteQueue.Enqueue(chunk);
        }

        // Synchronous console copy — production MainForm DataTransferred behaviour.
        console.Enqueue(chunk);
    }
}
