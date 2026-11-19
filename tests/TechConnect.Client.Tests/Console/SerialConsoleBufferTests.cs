// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialConsoleBufferTests.cs
//
// Purpose:
// Verifies SerialConsoleBuffer RX filtering, ring bulk enqueue/drain, wraparound,
// overflow drop policy, and concurrent producer/consumer behaviour.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Unit tests for <see cref="SerialConsoleBuffer"/> pending-byte batching and TX filtering.
/// </summary>
public sealed class SerialConsoleBufferTests
{
    /// <summary>
    /// Scenario: enqueue fragmented RX chunks then drain.
    /// Expected: contiguous raw octets without TX or artificial separators.
    /// </summary>
    [Fact]
    public void MultipleReads_FormContinuousBytes()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("Start"u8.ToArray()));
        buffer.Enqueue(TestHelpers.Chunk("ing"u8.ToArray()));
        buffer.Enqueue(TestHelpers.Chunk(" device..."u8.ToArray()));

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(0);
        data.Should().Equal("Starting device..."u8.ToArray());
    }

    /// <summary>
    /// Scenario: enqueue one byte at a time.
    /// Expected: drain yields the full sequence.
    /// </summary>
    [Fact]
    public void SingleByteReads_FormContinuousBytes()
    {
        var buffer = new SerialConsoleBuffer();
        foreach (var b in "Starting"u8.ToArray())
        {
            buffer.Enqueue(TestHelpers.Chunk(new[] { b }));
        }

        buffer.TryDrain(out var data, out _).Should().BeTrue();
        data.Should().Equal("Starting"u8.ToArray());
    }

    /// <summary>
    /// Scenario: enqueue TX then RX chunks.
    /// Expected: drain shows only RX bytes.
    /// </summary>
    [Fact]
    public void TxChunks_DoNotAppearInConsole()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("secret-tx", SerialDataDirection.Tx));
        buffer.Enqueue(TestHelpers.Chunk("visible-rx", SerialDataDirection.Rx));

        buffer.TryDrain(out var data, out _).Should().BeTrue();
        data.Should().Equal("visible-rx"u8.ToArray());
    }

    /// <summary>
    /// Scenario: drain an empty buffer.
    /// Expected: false with empty data and zero drops.
    /// </summary>
    [Fact]
    public void TryDrain_Empty_ReturnsFalse()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.TryDrain(out var data, out var dropped).Should().BeFalse();
        data.Should().BeEmpty();
        dropped.Should().Be(0);
    }

    /// <summary>
    /// Scenario: drain twice with new enqueues between.
    /// Expected: first drain concatenates prior chunks; second drain returns only the new chunk.
    /// </summary>
    [Fact]
    public void TryDrain_PreservesOrderAcrossBatches()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("one"));
        buffer.Enqueue(TestHelpers.Chunk("two"));
        buffer.TryDrain(out var first, out _).Should().BeTrue();
        first.Should().Equal("onetwo"u8.ToArray());

        buffer.Enqueue(TestHelpers.Chunk("three"));
        buffer.TryDrain(out var second, out _).Should().BeTrue();
        second.Should().Equal("three"u8.ToArray());
    }

    /// <summary>
    /// Scenario: enqueue more than maxPendingBytes.
    /// Expected: oldest pending display bytes are dropped and reported on drain.
    /// </summary>
    [Fact]
    public void Overflow_DropsOldestPending_AndReportsOnDrain()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        buffer.Enqueue(TestHelpers.Chunk(new string('a', 3000)));
        buffer.Enqueue(TestHelpers.Chunk(new string('b', 3000)));

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().BeGreaterThan(0);
        data.Length.Should().Be(4096);
        data.Should().Match(bytes => bytes.All(b => b == (byte)'a' || b == (byte)'b'));
        data[^1].Should().Be((byte)'b');
        buffer.PendingBytesDropped.Should().Be(0);
    }

    /// <summary>
    /// Scenario: incomplete pending then ClearPending, then enqueue ASCII.
    /// Expected: drain yields only the post-clear ASCII.
    /// </summary>
    [Fact]
    public void ClearPending_DiscardsUndrained()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0x1B, (byte)'[' }));
        buffer.ClearPending();
        buffer.Enqueue(TestHelpers.Chunk("ok"u8.ToArray()));
        buffer.TryDrain(out var data, out _).Should().BeTrue();
        data.Should().Equal("ok"u8.ToArray());
    }

    /// <summary>
    /// Scenario: enqueue a large continuous stream and drain once.
    /// Expected: exact length, payload intact.
    /// </summary>
    [Fact]
    public void LargeContinuousStream_SingleDrain()
    {
        var buffer = new SerialConsoleBuffer();
        var large = new byte[16_384];
        Array.Fill(large, (byte)'z');
        buffer.Enqueue(TestHelpers.Chunk(large));
        buffer.TryDrain(out var data, out _).Should().BeTrue();
        data.Should().HaveCount(16_384);
        data.Should().OnlyContain(b => b == (byte)'z');
    }

    /// <summary>
    /// Scenario: fill near capacity then append so the write wraps past the ring end.
    /// Expected: FIFO order preserved across the wrap boundary.
    /// </summary>
    [Fact]
    public void Wraparound_PreservesFifoOrder()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        buffer.Enqueue(TestHelpers.Chunk(new string('a', 4000)));
        buffer.TryDrain(out _, out _).Should().BeTrue();

        // After drain, head is reset; fill then overflow so oldest 'b' bytes are discarded.
        buffer.Enqueue(TestHelpers.Chunk(new string('b', 3000)));
        buffer.Enqueue(TestHelpers.Chunk(new string('c', 2000))); // overflow = 3000+2000-4096 = 904
        buffer.PendingByteCount.Should().Be(4096);

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(904);
        data.Should().HaveCount(4096);
        data[0].Should().Be((byte)'b');
        data[^1].Should().Be((byte)'c');
        data.Count(b => b == (byte)'b').Should().Be(3000 - 904);
        data.Count(b => b == (byte)'c').Should().Be(2000);
    }

    /// <summary>
    /// Scenario: single chunk larger than the soft cap.
    /// Expected: only the newest maxPendingBytes are retained; drops accounted.
    /// </summary>
    [Fact]
    public void Enqueue_LargerThanCapacity_KeepsNewestTail()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        var payload = new byte[5000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        buffer.Enqueue(TestHelpers.Chunk(payload));
        buffer.PendingByteCount.Should().Be(4096);
        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(5000 - 4096);
        data.Should().Equal(payload.AsSpan(5000 - 4096).ToArray());
    }

    /// <summary>
    /// Scenario: exact capacity fill without overflow.
    /// Expected: no drops; drain returns all bytes.
    /// </summary>
    [Fact]
    public void ExactCapacity_NoOverflow()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        buffer.Enqueue(TestHelpers.Chunk(new string('e', 4096)));
        buffer.PendingByteCount.Should().Be(4096);
        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(0);
        data.Should().HaveCount(4096);
    }

    /// <summary>
    /// Scenario: concurrent producers and a draining consumer with overflow pressure.
    /// Expected: pending stays bounded; drained bytes are a subsequence of enqueued pattern.
    /// </summary>
    [Fact]
    public async Task ConcurrentProducersAndConsumer_RemainBounded()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 8192);
        var writers = Enumerable.Range(0, 4).Select(id => Task.Run(() =>
        {
            var chunk = new byte[128];
            Array.Fill(chunk, (byte)('A' + id));
            for (var i = 0; i < 500; i++)
            {
                buffer.Enqueue(TestHelpers.Chunk(chunk));
            }
        })).ToArray();

        long drained = 0;
        long dropped = 0;
        var reader = Task.Run(async () =>
        {
            for (var i = 0; i < 200; i++)
            {
                if (buffer.TryDrain(out var data, out var d))
                {
                    drained += data.Length;
                    dropped += d;
                }

                await Task.Delay(1).ConfigureAwait(false);
            }
        });

        await Task.WhenAll(writers);
        await reader;
        if (buffer.TryDrain(out var tail, out var tailDropped))
        {
            drained += tail.Length;
            dropped += tailDropped;
        }

        buffer.PendingByteCount.Should().Be(0);
        buffer.PendingByteCount.Should().BeLessThanOrEqualTo(buffer.MaxPendingBytes);
        (drained + dropped).Should().Be(4L * 500 * 128);
    }

    /// <summary>
    /// Scenario: overflow while a consumer is actively draining.
    /// Expected: no exception; pending never exceeds soft cap.
    /// </summary>
    [Fact]
    public async Task OverflowDuringConsumption_StaysBounded()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var producer = Task.Run(() =>
        {
            var chunk = new byte[512];
            Array.Fill(chunk, (byte)'p');
            while (!cts.IsCancellationRequested)
            {
                buffer.Enqueue(TestHelpers.Chunk(chunk));
                buffer.PendingByteCount.Should().BeLessThanOrEqualTo(4096);
            }
        }, cts.Token);

        var consumer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                _ = buffer.TryDrain(out _, out _);
            }
        }, cts.Token);

        await Task.Delay(150);
        await cts.CancelAsync();
        try
        {
            await Task.WhenAll(producer, consumer);
        }
        catch (OperationCanceledException)
        {
        }

        buffer.PendingByteCount.Should().BeLessThanOrEqualTo(4096);
    }
}
