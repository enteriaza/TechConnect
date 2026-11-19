// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshOutboundWritePerfTests.cs
//
// Purpose:
// Release isolate for PERF-SRV-03 (gated Write vs Write+Flush on an SshStream-like
// stream). Writes results to %TEMP%\TechConnect-PERF-SRV-03.txt (not a pass/fail gate
// beyond Write-only being no slower than Write+Flush).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using FluentAssertions;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Measurement harness mirroring <c>TechConnectCliSession.WriteRawAsync</c> gate behaviour.
/// </summary>
public sealed class SshOutboundWritePerfTests
{
    /// <summary>
    /// Scenario: SshStream-like stream (WriteAsync completes immediately; Flush is dispose-check only).
    /// Expected: Write-only gated path sustains higher ops/s than Write+FlushAsync; records metrics.
    /// </summary>
    [Fact]
    public async Task MeasureGatedWriteFlushOverhead_RecordsMetrics()
    {
        const int ops = 40_000;
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        var gate = new SemaphoreSlim(1, 1);

        var withFlush = new SshLikeStream();
        var writeOnly = new SshLikeStream();

        var with = await MeasureAsync(
            () => WriteRawWithFlushAsync(withFlush, gate, payload, CancellationToken.None),
            ops);
        var without = await MeasureAsync(
            () => WriteRawNoFlushAsync(writeOnly, gate, payload, CancellationToken.None),
            ops);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
        sb.AppendLine($"runtime={Environment.Version}");
        sb.AppendLine($"os={Environment.OSVersion}");
        sb.AppendLine($"procs={Environment.ProcessorCount}");
        sb.AppendLine($"payloadBytes={payload.Length} ops={ops}");
        sb.AppendLine(
            $"write+flush ops/s={with.OpsPerSec:F0} ns/op={with.NsPerOp:F1} allocBytes={with.AllocBytes}");
        sb.AppendLine(
            $"write-only ops/s={without.OpsPerSec:F0} ns/op={without.NsPerOp:F1} allocBytes={without.AllocBytes}");
        sb.AppendLine($"withFlush.flushCalls={withFlush.FlushCalls} writeOnly.flushCalls={writeOnly.FlushCalls}");

        var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-SRV-03.txt");
        File.WriteAllText(path, sb.ToString());

        without.OpsPerSec.Should().BeGreaterThan(with.OpsPerSec);
        writeOnly.FlushCalls.Should().Be(0);
        withFlush.FlushCalls.Should().BeGreaterThan(0);
    }

    private static async Task WriteRawWithFlushAsync(
        Stream stream,
        SemaphoreSlim gate,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteRawNoFlushAsync(
        Stream stream,
        SemaphoreSlim gate,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<(double OpsPerSec, double NsPerOp, long AllocBytes)> MeasureAsync(
        Func<Task> oneOp,
        int ops)
    {
        for (var i = 0; i < 1_000; i++)
        {
            await oneOp().ConfigureAwait(false);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < ops; i++)
        {
            await oneOp().ConfigureAwait(false);
        }

        sw.Stop();
        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        return (ops / Math.Max(sw.Elapsed.TotalSeconds, 1e-9), sw.Elapsed.TotalNanoseconds / ops, alloc);
    }

    /// <summary>
    /// Mirrors DevTunnels <c>SshStream</c>: WriteAsync sends immediately; Flush only checks disposed.
    /// </summary>
    private sealed class SshLikeStream : Stream
    {
        public long WriteCalls;
        public long FlushCalls;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => FlushCalls++;

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => WriteCalls++;

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
