// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    BoundedByteQueueTests.cs
//
// Purpose:
// Verifies bounded operator outbound queue enqueue/dequeue fidelity, overflow
// refusal without blocking, completion, and drop-without-complete behaviour.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;

namespace TechConnect.Server.Tests.Attachment;

/// <summary>
/// Unit tests for <see cref="BoundedByteQueue"/> byte-budget and lifecycle semantics.
/// </summary>
public sealed class BoundedByteQueueTests
{
    /// <summary>
    /// Verifies a defensive copy round-trips including NUL, 0xFF, CR/LF, and ESC bytes.
    /// </summary>
    [Fact]
    public async Task EnqueueDequeue_PreservesBytes()
    {
        var q = new BoundedByteQueue(8192);
        var payload = new byte[] { 0x00, 0xFF, 0x0D, 0x0A, 0x1B, 0x5B, 0x41, 0xC3, 0x28 };
        q.TryEnqueue(payload).Should().BeTrue();
        var got = await q.DequeueAsync(CancellationToken.None);
        got.Should().Equal(payload);
    }

    /// <summary>
    /// Verifies overflow refuses the new chunk without mutating queued bytes or blocking.
    /// </summary>
    [Fact]
    public void Overflow_RefusesWithoutBlocking()
    {
        var q = new BoundedByteQueue(4096);
        q.TryEnqueue(new byte[4000]).Should().BeTrue();
        q.TryEnqueue(new byte[200]).Should().BeFalse();
        q.QueuedBytes.Should().Be(4000);
    }

    /// <summary>
    /// Verifies <see cref="BoundedByteQueue.Complete"/> unblocks dequeue with null.
    /// </summary>
    [Fact]
    public async Task Complete_UnblocksDequeueWithNull()
    {
        var q = new BoundedByteQueue(4096);
        q.Complete();
        var got = await q.DequeueAsync(CancellationToken.None);
        got.Should().BeNull();
    }

    /// <summary>
    /// Verifies <see cref="BoundedByteQueue.DropAllQueued"/> clears bytes but still accepts new enqueues.
    /// </summary>
    [Fact]
    public void DropAllQueued_ClearsWithoutCompleting()
    {
        var q = new BoundedByteQueue(8192);
        q.TryEnqueue("abc"u8.ToArray()).Should().BeTrue();
        q.DropAllQueued();
        q.QueuedBytes.Should().Be(0);
        q.TryEnqueue("x"u8.ToArray()).Should().BeTrue();
        q.QueuedBytes.Should().Be(1);
    }
}
