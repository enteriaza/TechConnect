// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialConsoleBufferTests.cs
//
// Purpose:
// Verifies SerialConsoleBuffer RX filtering, byte drain/batching, and overflow drop policy.
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
}
