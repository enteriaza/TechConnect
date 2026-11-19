// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    BoundedByteQueueTests.cs
//
// Purpose:
// Verifies bounded operator outbound queue enqueue/dequeue fidelity, overflow
// refusal without blocking, ownership transfer, completion, and drop behaviour.
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
    /// Verifies defensive <see cref="BoundedByteQueue.TryEnqueue"/> copies so caller mutation
    /// after enqueue does not affect the queued chunk.
    /// </summary>
    [Fact]
    public async Task TryEnqueue_DefensiveCopy_CallerMutationDoesNotAffectQueue()
    {
        var q = new BoundedByteQueue(8192);
        var payload = new byte[] { 1, 2, 3, 4 };
        q.TryEnqueue(payload).Should().BeTrue();
        payload[0] = 0xFF;
        var got = await q.DequeueAsync(CancellationToken.None);
        got.Should().Equal(new byte[] { 1, 2, 3, 4 });
    }

    /// <summary>
    /// Verifies <see cref="BoundedByteQueue.TryEnqueueOwned"/> transfers the same array instance.
    /// </summary>
    [Fact]
    public async Task TryEnqueueOwned_TransfersSameArrayInstance()
    {
        var q = new BoundedByteQueue(8192);
        var payload = new byte[] { 9, 8, 7 };
        q.TryEnqueueOwned(payload).Should().BeTrue();
        var got = await q.DequeueAsync(CancellationToken.None);
        ReferenceEquals(got, payload).Should().BeTrue();
        got.Should().Equal(new byte[] { 9, 8, 7 });
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
    /// Verifies owned overflow refuses without retaining the refused array and without copying.
    /// </summary>
    [Fact]
    public async Task TryEnqueueOwned_Overflow_CallerRetainsOwnership()
    {
        var q = new BoundedByteQueue(4096);
        var first = new byte[4000];
        var refused = new byte[200];
        q.TryEnqueueOwned(first).Should().BeTrue();
        q.TryEnqueueOwned(refused).Should().BeFalse();
        q.QueuedBytes.Should().Be(4000);

        // Caller still owns the refused buffer and may mutate it freely.
        refused[0] = 0xAB;
        var got = await q.DequeueAsync(CancellationToken.None);
        ReferenceEquals(got, first).Should().BeTrue();
        got![0].Should().Be(0);
    }

    /// <summary>
    /// Verifies overflow on the copy path does not allocate a retained discarded chunk
    /// (capacity check precedes <c>ToArray</c>); queued bytes stay unchanged.
    /// </summary>
    [Fact]
    public void TryEnqueue_Overflow_DoesNotIncreaseQueuedBytes()
    {
        var q = new BoundedByteQueue(4096);
        q.TryEnqueue(new byte[4000]).Should().BeTrue();
        var before = q.QueuedBytes;
        q.TryEnqueue(new byte[200]).Should().BeFalse();
        q.QueuedBytes.Should().Be(before);
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

    /// <summary>
    /// Verifies dispose with pending owned data clears the queue and rejects further use.
    /// </summary>
    [Fact]
    public void Dispose_WithPendingData_ClearsAndRejectsFurtherUse()
    {
        var q = new BoundedByteQueue(8192);
        q.TryEnqueueOwned(new byte[100]).Should().BeTrue();
        q.QueuedBytes.Should().Be(100);
        q.Dispose();
        q.QueuedBytes.Should().Be(0);
        var act = () => q.TryEnqueueOwned(new byte[1]);
        act.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// Verifies concurrent producers using owned enqueue preserve total accepted bytes under the cap.
    /// </summary>
    [Fact]
    public async Task ConcurrentOwnedProducers_RespectCapacity()
    {
        var q = new BoundedByteQueue(8192);
        var accepted = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                if (q.TryEnqueueOwned(new byte[64]))
                {
                    Interlocked.Increment(ref accepted);
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);
        q.QueuedBytes.Should().BeLessThanOrEqualTo(8192);
        q.QueuedBytes.Should().Be(accepted * 64);

        var drained = 0;
        q.Complete();
        while (true)
        {
            var chunk = await q.DequeueAsync(CancellationToken.None);
            if (chunk is null)
            {
                break;
            }

            drained += chunk.Length;
        }

        drained.Should().Be(accepted * 64);
    }

    /// <summary>
    /// Verifies empty owned enqueue succeeds without changing queued bytes.
    /// </summary>
    [Fact]
    public void TryEnqueueOwned_Empty_SucceedsWithoutQueueing()
    {
        var q = new BoundedByteQueue(4096);
        q.TryEnqueueOwned([]).Should().BeTrue();
        q.QueuedBytes.Should().Be(0);
    }
}
