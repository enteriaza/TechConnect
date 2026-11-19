// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteOutboundBufferTests.cs
//
// Purpose:
// Verifies RemoteOutboundBuffer enqueue/dequeue accounting, capacity, drop-oldest, requeue, and completion semantics.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Buffer;

/// <summary>
/// Accounting semantics:
/// - Accepted = Sent + Dropped + Queued + InFlight
/// - Every byte presented to Enqueue increments Accepted
/// - Requeue does not increase Accepted (bytes were already accepted when first enqueued)
/// - Drop-oldest never counts as Sent; dropped bytes move from Queued to Dropped
/// - Oversized / post-Complete rejects increment Accepted and Dropped together (never Queued/Sent)
/// </summary>
public sealed class RemoteOutboundBufferTests
{
    /// <summary>
    /// Default buffer capacity used by accounting scenarios (4096 bytes).
    /// </summary>
    private const int Capacity = 4096;

    /// <summary>
    /// Scenario: enqueue one chunk, dequeue, then MarkSent.
    /// Expected: same chunk instance; in-flight then sent accounting keeps the invariant.
    /// </summary>
    [Fact]
    public async Task Enqueue_Dequeue_PreservesPayloadAndAccounting()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            var chunk = TestHelpers.Chunk("hello");

            buffer.Enqueue(chunk);
            TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));

            var dequeued = await buffer.DequeueAsync(CancellationToken.None);
            dequeued.Should().BeSameAs(chunk);
            var mid = buffer.GetStats(0, TimeSpan.Zero);
            mid.BytesInFlight.Should().Be(5);
            mid.BytesQueued.Should().Be(0);
            TestHelpers.AssertAccountingInvariant(mid);

            buffer.MarkSent(5);
            var after = buffer.GetStats(0, TimeSpan.Zero);
            after.BytesSent.Should().Be(5);
            after.BytesInFlight.Should().Be(0);
            after.BytesAccepted.Should().Be(5);
            TestHelpers.AssertAccountingInvariant(after);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: enqueue a payload exactly equal to capacity.
    /// Expected: accepted and queued with zero drops; dequeue and MarkSent preserve invariant.
    /// </summary>
    [Fact]
    public async Task ExactCapacity_AcceptsWithoutDrop()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            var payload = new byte[Capacity];
            Random.Shared.NextBytes(payload);

            buffer.Enqueue(TestHelpers.Chunk(payload));
            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesAccepted.Should().Be(Capacity);
            stats.BytesQueued.Should().Be(Capacity);
            stats.BytesDropped.Should().Be(0);
            TestHelpers.AssertAccountingInvariant(stats);

            var dequeued = await buffer.DequeueAsync(CancellationToken.None);
            dequeued!.Data.Length.Should().Be(Capacity);
            buffer.MarkSent(Capacity);
            TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: overflow the queue so drop-oldest runs.
    /// Expected: dropped bytes come from queued (not sent) and invariant holds.
    /// </summary>
    [Fact]
    public void DropOldest_Overflow_DropsQueuedNotSent()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Enqueue(TestHelpers.Chunk(new byte[3000]));
            buffer.Enqueue(TestHelpers.Chunk(new byte[2000]));

            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesDropped.Should().Be(3000);
            stats.BytesQueued.Should().Be(2000);
            stats.BytesAccepted.Should().Be(5000);
            stats.BytesSent.Should().Be(0, because: "drop-oldest never counts as Sent");
            TestHelpers.AssertAccountingInvariant(stats);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: enqueue a chunk larger than capacity.
    /// Expected: accepted+dropped together; never queued or sent.
    /// </summary>
    [Fact]
    public async Task OversizedChunk_IsDropped_NeverQueuedOrSent()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Enqueue(TestHelpers.Chunk(new byte[Capacity + 1]));

            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesDropped.Should().Be(Capacity + 1);
            stats.BytesAccepted.Should().Be(Capacity + 1);
            stats.BytesQueued.Should().Be(0);
            stats.BytesSent.Should().Be(0);
            TestHelpers.AssertAccountingInvariant(stats);

            buffer.Enqueue(TestHelpers.Chunk("ok"));
            var dequeued = await buffer.DequeueAsync(CancellationToken.None);
            dequeued!.Data.ToArray().Should().Equal(System.Text.Encoding.UTF8.GetBytes("ok"));
            buffer.MarkSent(2);
            var after = buffer.GetStats(0, TimeSpan.Zero);
            after.BytesSent.Should().Be(2);
            after.BytesAccepted.Should().Be(Capacity + 1 + 2);
            after.BytesDropped.Should().Be(Capacity + 1);
            TestHelpers.AssertAccountingInvariant(after);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: many concurrent producers/consumers stress the buffer.
    /// Expected: accounting invariant holds throughout and the buffer drains.
    /// </summary>
    [Fact]
    public async Task ConcurrentProducersAndConsumers_PreserveInvariantAndDrain()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            const int producers = 4;
            const int perProducer = 50;
            const int chunkSize = 128;
            var expectedBytes = producers * perProducer * chunkSize; // 25_600

            // Burst-produce without a consumer so overflow drops are guaranteed.
            await Task.WhenAll(Enumerable.Range(0, producers).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perProducer; i++)
                {
                    buffer.Enqueue(TestHelpers.Chunk(new byte[chunkSize]));
                }
            })));

            var afterProduce = buffer.GetStats(0, TimeSpan.Zero);
            TestHelpers.AssertAccountingInvariant(afterProduce);
            afterProduce.BytesAccepted.Should().Be(expectedBytes);
            afterProduce.BytesDropped.Should().BeGreaterThan(0);

            long sent = 0;
            while (true)
            {
                var snapshot = buffer.GetStats(0, TimeSpan.Zero);
                if (snapshot.BytesQueued == 0 && snapshot.BytesInFlight == 0)
                {
                    break;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var chunk = await buffer.DequeueAsync(timeout.Token);
                if (chunk is null)
                {
                    break;
                }

                sent += chunk.Data.Length;
                buffer.MarkSent(chunk.Data.Length);
                TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));
            }

            buffer.Complete();
            var final = buffer.GetStats(0, TimeSpan.Zero);
            TestHelpers.AssertAccountingInvariant(final);
            final.BytesQueued.Should().Be(0);
            final.BytesInFlight.Should().Be(0);
            final.BytesAccepted.Should().Be(final.BytesSent + final.BytesDropped);
            final.BytesSent.Should().Be(sent);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: requeue at front after partial send displacement.
    /// Expected: Accepted does not increase; bytes remain accounted.
    /// </summary>
    [Fact]
    public async Task Requeue_Front_PartialDisplacement_DoesNotIncreaseAccepted()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Enqueue(TestHelpers.Chunk(new byte[3000]));
            buffer.Enqueue(TestHelpers.Chunk(new byte[1096]));
            var before = buffer.GetStats(0, TimeSpan.Zero);
            before.BytesAccepted.Should().Be(Capacity);
            TestHelpers.AssertAccountingInvariant(before);

            var first = await buffer.DequeueAsync(CancellationToken.None);
            first!.Data.Length.Should().Be(3000);
            var inFlight = buffer.GetStats(0, TimeSpan.Zero);
            inFlight.BytesInFlight.Should().Be(3000);
            inFlight.BytesQueued.Should().Be(1096);
            TestHelpers.AssertAccountingInvariant(inFlight);

            buffer.Enqueue(TestHelpers.Chunk(new byte[1096]));
            var mid = buffer.GetStats(0, TimeSpan.Zero);
            TestHelpers.AssertAccountingInvariant(mid);

            var acceptedBeforeRequeue = mid.BytesAccepted;
            buffer.Requeue(first);

            var after = buffer.GetStats(0, TimeSpan.Zero);
            after.BytesAccepted.Should().Be(acceptedBeforeRequeue, because: "Requeue does not increase Accepted");
            after.BytesInFlight.Should().Be(0);
            after.BytesSent.Should().Be(0, because: "drop-oldest never counts as Sent");
            after.BytesDropped.Should().BeGreaterThan(0, because: "displacement drops oldest queued bytes");
            TestHelpers.AssertAccountingInvariant(after);

            var front = await buffer.DequeueAsync(CancellationToken.None);
            front.Should().BeSameAs(first);
            buffer.MarkSent(3000);
            TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: Complete then DequeueAsync.
    /// Expected: dequeue returns null.
    /// </summary>
    [Fact]
    public async Task Complete_MakesDequeueReturnNull()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Complete();
            var result = await buffer.DequeueAsync(CancellationToken.None);
            result.Should().BeNull();
            TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: cancel a waiting DequeueAsync.
    /// Expected: operation cancels/throws rather than hanging.
    /// </summary>
    [Fact]
    public async Task Dequeue_Cancellation_Throws()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = async () => await buffer.DequeueAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
            TestHelpers.AssertAccountingInvariant(buffer.GetStats(0, TimeSpan.Zero));
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: dispose with queued and in-flight bytes.
    /// Expected: those bytes move to Dropped and invariant holds.
    /// </summary>
    [Fact]
    public async Task Dispose_MovesQueuedAndInFlightToDropped()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        buffer.Enqueue(TestHelpers.Chunk(new byte[100]));
        buffer.Enqueue(TestHelpers.Chunk(new byte[50]));
        _ = await buffer.DequeueAsync(CancellationToken.None);

        var before = buffer.GetStats(0, TimeSpan.Zero);
        before.BytesInFlight.Should().Be(100);
        before.BytesQueued.Should().Be(50);
        TestHelpers.AssertAccountingInvariant(before);

        buffer.Dispose();

        var after = buffer.GetStats(0, TimeSpan.Zero);
        after.BytesQueued.Should().Be(0);
        after.BytesInFlight.Should().Be(0);
        after.BytesDropped.Should().Be(150);
        after.BytesAccepted.Should().Be(150);
        TestHelpers.AssertAccountingInvariant(after);
    }

    /// <summary>
    /// Scenario: enqueue after Complete.
    /// Expected: bytes accepted and dropped together without queuing.
    /// </summary>
    [Fact]
    public void EnqueueAfterComplete_AcceptsAndDrops()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Complete();
            buffer.Enqueue(TestHelpers.Chunk(new byte[10]));

            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesDropped.Should().Be(10);
            stats.BytesAccepted.Should().Be(10);
            stats.BytesQueued.Should().Be(0);
            stats.BytesSent.Should().Be(0);
            TestHelpers.AssertAccountingInvariant(stats);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: DropQueued while bytes are in-flight.
    /// Expected: queue cleared to Dropped; in-flight unchanged.
    /// </summary>
    [Fact]
    public async Task DropQueued_ClearsQueue_WithoutTouchingInFlight()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Enqueue(TestHelpers.Chunk(new byte[100]));
            buffer.Enqueue(TestHelpers.Chunk(new byte[50]));
            var inFlight = await buffer.DequeueAsync(CancellationToken.None);
            inFlight!.Data.Length.Should().Be(100);

            buffer.DropQueued();
            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesQueued.Should().Be(0);
            stats.BytesInFlight.Should().Be(100);
            stats.BytesDropped.Should().Be(50);
            TestHelpers.AssertAccountingInvariant(stats);

            buffer.DropInFlight(inFlight);
            var after = buffer.GetStats(0, TimeSpan.Zero);
            after.BytesInFlight.Should().Be(0);
            after.BytesDropped.Should().Be(150);
            TestHelpers.AssertAccountingInvariant(after);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Scenario: Discard a chunk without enqueue.
    /// Expected: Accepted and Dropped increase; never queued.
    /// </summary>
    [Fact]
    public void Discard_CountsAcceptedAndDropped_WithoutQueuing()
    {
        var buffer = new RemoteOutboundBuffer(Capacity);
        try
        {
            buffer.Discard(TestHelpers.Chunk(new byte[40]));
            var stats = buffer.GetStats(0, TimeSpan.Zero);
            stats.BytesAccepted.Should().Be(40);
            stats.BytesDropped.Should().Be(40);
            stats.BytesQueued.Should().Be(0);
            stats.BytesInFlight.Should().Be(0);
            TestHelpers.AssertAccountingInvariant(stats);
        }
        finally
        {
            buffer.Dispose();
        }
    }
}

