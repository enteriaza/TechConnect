// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteOutboundBuffer.cs
//
// Purpose:
// Bounded serial→remote queue with drop-oldest overflow and no outage replay.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Models;

namespace TechConnect.Client.Services;

/// <summary>
/// Bounded serial→remote queue (device RX awaiting TCP/mTLS send).
/// Overflow: drop oldest queued chunks until the new chunk fits (or drop the new chunk).
/// Disconnect/outage: queued and in-flight remote-bound bytes are discarded — never replayed
/// after reconnect. The physical serial reader is never blocked by this buffer.
/// Accounting: BytesAccepted = BytesSent + BytesDropped + BytesQueued + BytesInFlight.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery guarantees:</b> <see cref="MarkSent"/> increments <c>BytesSent</c> after the
/// transport send API accepts the payload for transmission. That is not an acknowledgment
/// that a remote NOC/operator client received or displayed the bytes.
/// </para>
/// <para>
/// Thread-safe for concurrent enqueue/dequeue/stats via an internal lock and a counting
/// semaphore. <see cref="Dispose"/> completes the queue and releases waiters; further
/// mutating calls throw <see cref="ObjectDisposedException"/>.
/// </para>
/// </remarks>
internal sealed class RemoteOutboundBuffer
{
    /// <summary>FIFO of RX chunks waiting for remote send.</summary>
    private readonly Queue<SerialDataChunk> _queue = new();

    /// <summary>Counts available queued items for <see cref="DequeueAsync"/> waiters.</summary>
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Serializes accounting and queue mutations.</summary>
    private readonly object _gate = new();

    /// <summary>Hard cap on sum of queued chunk sizes (bytes).</summary>
    private readonly int _maximumQueuedBytes;

    /// <summary>When true, new enqueues are dropped and dequeue may return null.</summary>
    private bool _completed;

    /// <summary>When true, the instance is unusable.</summary>
    private bool _disposed;

    /// <summary>Bytes currently sitting in <see cref="_queue"/>.</summary>
    private long _bytesQueued;

    /// <summary>Bytes presented via <see cref="Enqueue"/> or <see cref="Discard"/>.</summary>
    private long _bytesAccepted;

    /// <summary>Bytes dropped (overflow, discard, complete, outage, dispose).</summary>
    private long _bytesDropped;

    /// <summary>
    /// Bytes for which <see cref="MarkSent"/> was called after transport send acceptance.
    /// </summary>
    private long _bytesSent;

    /// <summary>Bytes dequeued but not yet marked sent, requeued, or dropped in-flight.</summary>
    private long _bytesInFlight;

    /// <summary>
    /// Creates a buffer with the specified maximum queued payload size.
    /// </summary>
    /// <param name="maximumQueuedBytes">Must be at least 4096.</param>
    /// <exception cref="ArgumentOutOfRangeException">Cap is below 4096.</exception>
    public RemoteOutboundBuffer(int maximumQueuedBytes)
    {
        if (maximumQueuedBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedBytes));
        }

        _maximumQueuedBytes = maximumQueuedBytes;
    }

    /// <summary>
    /// Accepts an RX chunk for remote delivery, dropping oldest queued data if needed.
    /// </summary>
    /// <param name="chunk">Serial RX chunk; empty payloads are ignored.</param>
    /// <remarks>
    /// Always increments Accepted. If completed, oversized for the entire cap, or unable
    /// to fit after dropping oldest, increments Dropped instead of queuing.
    /// </remarks>
    public void Enqueue(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var size = chunk.Data.Length;
        if (size <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // All bytes presented to the buffer enter Accepted accounting.
            // Rejected/dropped bytes also increment Dropped so that:
            // Accepted = Sent + Dropped + Queued + InFlight
            _bytesAccepted += size;

            if (_completed)
            {
                _bytesDropped += size;
                return;
            }

            if (size > _maximumQueuedBytes)
            {
                _bytesDropped += size;
                return;
            }

            while (_bytesQueued + size > _maximumQueuedBytes && _queue.Count > 0)
            {
                DropOldest_NoLock();
            }

            if (_bytesQueued + size > _maximumQueuedBytes)
            {
                _bytesDropped += size;
                return;
            }

            _queue.Enqueue(chunk);
            _bytesQueued += size;
        }

        _available.Release();
    }

    /// <summary>
    /// Counts RX toward Accepted/Dropped without queuing. Used while the remote transport
    /// is down so local serial keeps flowing but outage bytes are never replayed.
    /// </summary>
    /// <param name="chunk">Serial RX chunk observed during an outage.</param>
    public void Discard(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var size = chunk.Data.Length;
        if (size <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _bytesAccepted += size;
            _bytesDropped += size;
        }
    }

    /// <summary>
    /// Waits for the next queued chunk, moving it to in-flight accounting.
    /// </summary>
    /// <param name="cancellationToken">Cancels the semaphore wait.</param>
    /// <returns>
    /// The next chunk, or <see langword="null"/> when completed and the queue is empty.
    /// </returns>
    public async Task<SerialDataChunk?> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_queue.Count > 0)
                {
                    var chunk = _queue.Dequeue();
                    var size = chunk.Data.Length;
                    _bytesQueued = Math.Max(0, _bytesQueued - size);
                    _bytesInFlight += size;
                    return chunk;
                }

                if (_completed)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Returns a failed in-flight chunk to the front of the queue (or drops it if completed).
    /// </summary>
    /// <param name="chunk">Chunk previously returned by <see cref="DequeueAsync"/>.</param>
    /// <remarks>
    /// Used for cancellation or non-outage send failures where retry on the same connection
    /// is still appropriate. Outage paths should use <see cref="DropInFlight"/> instead.
    /// </remarks>
    public void Requeue(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var size = chunk.Data.Length;
        if (size <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Leaving in-flight: either re-enter the queue or become dropped.
            if (_bytesInFlight >= size)
            {
                _bytesInFlight -= size;
            }
            else
            {
                _bytesInFlight = 0;
            }

            if (_completed)
            {
                _bytesDropped += size;
                return;
            }

            while (_bytesQueued + size > _maximumQueuedBytes && _queue.Count > 0)
            {
                DropOldest_NoLock();
            }

            if (_bytesQueued + size > _maximumQueuedBytes)
            {
                _bytesDropped += size;
                return;
            }

            // Prefer keeping the failed-to-send chunk at the front.
            var existing = _queue.ToArray();
            _queue.Clear();
            _queue.Enqueue(chunk);
            foreach (var item in existing)
            {
                _queue.Enqueue(item);
            }

            _bytesQueued += size;
        }

        _available.Release();
    }

    /// <summary>
    /// Records that <paramref name="byteCount"/> left in-flight after transport send acceptance.
    /// </summary>
    /// <param name="byteCount">Payload length accepted by the transport send API.</param>
    /// <remarks>
    /// Does not mean the remote device or NOC client acknowledged the data.
    /// </remarks>
    public void MarkSent(int byteCount)
    {
        if (byteCount <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_bytesInFlight >= byteCount)
            {
                _bytesInFlight -= byteCount;
            }
            else
            {
                _bytesInFlight = 0;
            }

            _bytesSent += byteCount;
        }
    }

    /// <summary>
    /// Snapshot of delivery accounting plus caller-supplied connect/downtime fields.
    /// </summary>
    /// <param name="connectAttempts">Session connect-attempt counter from the owner.</param>
    /// <param name="downtime">Cumulative remote downtime from the owner.</param>
    /// <returns>Consistent stats under the buffer lock.</returns>
    public RemoteDeliveryStats GetStats(long connectAttempts, TimeSpan downtime)
    {
        lock (_gate)
        {
            return new RemoteDeliveryStats
            {
                BytesQueued = _bytesQueued,
                BytesAccepted = _bytesAccepted,
                BytesSent = _bytesSent,
                BytesDropped = _bytesDropped,
                BytesInFlight = _bytesInFlight,
                ConnectAttempts = connectAttempts,
                CumulativeRemoteDowntime = downtime
            };
        }
    }

    /// <summary>
    /// Marks the buffer complete so dequeue can exit once empty; further enqueues are dropped.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _completed = true;
        }

        try
        {
            _available.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Drops all queued (not in-flight) bytes. Used on transport disconnect so reconnect
    /// does not replay stale serial data from the previous connection.
    /// </summary>
    public void DropQueued()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_queue.Count > 0)
            {
                DropOldest_NoLock();
            }
        }
    }

    /// <summary>
    /// Drops an in-flight chunk that will not be retried (e.g. after transport drop).
    /// </summary>
    /// <param name="chunk">In-flight chunk to abandon.</param>
    public void DropInFlight(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var size = chunk.Data.Length;
        if (size <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bytesInFlight >= size)
            {
                _bytesInFlight -= size;
            }
            else
            {
                _bytesInFlight = 0;
            }

            _bytesDropped += size;
        }
    }

    /// <summary>
    /// Disposes the semaphore and abandons any remaining queued/in-flight bytes as dropped.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _completed = true;
            _queue.Clear();
            _bytesDropped += _bytesQueued + _bytesInFlight;
            _bytesQueued = 0;
            _bytesInFlight = 0;
        }

        _available.Dispose();
    }

    /// <summary>
    /// Removes the oldest queued chunk, updating Dropped/Queued and consuming one semaphore count.
    /// </summary>
    /// <remarks>Caller must hold <see cref="_gate"/>.</remarks>
    private void DropOldest_NoLock()
    {
        var dropped = _queue.Dequeue();
        _bytesQueued -= dropped.Data.Length;
        _bytesDropped += dropped.Data.Length;
        // Consumed a semaphore slot that belonged to the dropped item.
        _ = _available.Wait(0);
    }
}
