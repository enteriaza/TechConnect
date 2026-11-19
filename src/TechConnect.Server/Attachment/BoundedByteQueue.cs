// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    BoundedByteQueue.cs
//
// Purpose:
// Provides a bounded, non-blocking byte-budget queue that buffers device RX for
// delivery to an attached operator SSH session without stalling the connector reader.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Attachment;

/// <summary>
/// Bounded byte-budget queue for operator delivery (device RX → SSH).
/// </summary>
/// <remarks>
/// <para>
/// Overflow policy: refuse the new chunk (caller detaches the slow operator).
/// Never blocks the producer. Empty enqueues succeed without allocating.
/// </para>
/// <para>
/// Thread-safety: enqueue, dequeue, complete, drop, and dispose synchronize on an
/// internal gate; <see cref="TryEnqueue"/> releases a semaphore outside the lock.
/// Concurrent producers and a single consumer are supported; disposed use throws
/// <see cref="ObjectDisposedException"/>.
/// </para>
/// <para>
/// The type exposes <see cref="Dispose"/> but does not implement <see cref="IDisposable"/>.
/// Callers that own an instance must call <see cref="Dispose"/> explicitly (as
/// <see cref="OperatorAttachment"/> does).
/// </para>
/// </remarks>
internal sealed class BoundedByteQueue
{
    /// <summary>FIFO of defensive payload copies awaiting SSH write.</summary>
    private readonly Queue<byte[]> _queue = new();

    /// <summary>Signals that a chunk is available or that the queue completed.</summary>
    private readonly SemaphoreSlim _available = new(0);

    /// <summary>Serializes mutations of queue contents and completion/dispose flags.</summary>
    private readonly object _gate = new();

    /// <summary>Maximum sum of queued chunk lengths accepted by <see cref="TryEnqueue"/>.</summary>
    private readonly int _maximumQueuedBytes;

    /// <summary>Current sum of queued chunk lengths.</summary>
    private int _queuedBytes;

    /// <summary>When true, further enqueues fail and dequeue returns null once drained.</summary>
    private bool _completed;

    /// <summary>When true, further operations throw <see cref="ObjectDisposedException"/>.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a queue with the specified byte budget.
    /// </summary>
    /// <param name="maximumQueuedBytes">
    /// Maximum total queued bytes. Must be at least 4096.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumQueuedBytes"/> is less than 4096.
    /// </exception>
    public BoundedByteQueue(int maximumQueuedBytes)
    {
        if (maximumQueuedBytes < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumQueuedBytes));
        }

        _maximumQueuedBytes = maximumQueuedBytes;
    }

    /// <summary>Configured maximum total queued bytes.</summary>
    public int MaximumQueuedBytes => _maximumQueuedBytes;

    /// <summary>Current number of bytes held in the queue (under the internal gate).</summary>
    public int QueuedBytes
    {
        get
        {
            lock (_gate)
            {
                return _queuedBytes;
            }
        }
    }

    /// <summary>
    /// Attempts to enqueue a defensive copy of <paramref name="data"/>.
    /// </summary>
    /// <param name="data">Payload to copy and enqueue; empty payloads succeed without work.</param>
    /// <returns>
    /// <see langword="true"/> when accepted (including empty data);
    /// <see langword="false"/> when the queue is full or completed (overflow / closed).
    /// </returns>
    /// <exception cref="ObjectDisposedException">The queue has been disposed.</exception>
    public bool TryEnqueue(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return true;
        }

        var copy = data.ToArray();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_completed)
            {
                return false;
            }

            if (_queuedBytes + copy.Length > _maximumQueuedBytes)
            {
                return false;
            }

            _queue.Enqueue(copy);
            _queuedBytes += copy.Length;
        }

        _available.Release();
        return true;
    }

    /// <summary>
    /// Waits for the next chunk, or <see langword="null"/> when the queue is completed and empty.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait on the availability semaphore.</param>
    /// <returns>
    /// The next queued chunk (caller owns the array), or <see langword="null"/> after
    /// <see cref="Complete"/> when no chunks remain.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The queue has been disposed.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled while waiting.
    /// </exception>
    /// <remarks>
    /// Spurious wake-ups (complete with empty queue, or races with <see cref="DropAllQueued"/>)
    /// loop until a chunk is available or completion is observed.
    /// </remarks>
    public async Task<byte[]?> DequeueAsync(CancellationToken cancellationToken)
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
                    _queuedBytes -= chunk.Length;
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
    /// Marks the queue completed so further enqueues fail and waiting consumers unblock.
    /// </summary>
    /// <remarks>
    /// Idempotent when already completed or disposed. Releases the availability semaphore
    /// once so a blocked <see cref="DequeueAsync"/> can observe completion; a
    /// <see cref="SemaphoreFullException"/> from that release is ignored.
    /// </remarks>
    public void Complete()
    {
        lock (_gate)
        {
            if (_disposed || _completed)
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
    /// Drops all queued chunks without completing the queue.
    /// </summary>
    /// <remarks>
    /// Used when leaving terminal mode so stale RX is not dumped on re-entry.
    /// Consumes matching semaphore counts with non-blocking waits. Does not signal completion.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The queue has been disposed.</exception>
    public void DropAllQueued()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (_queue.Count > 0)
            {
                var dropped = _queue.Dequeue();
                _queuedBytes -= dropped.Length;
                _ = _available.Wait(0);
            }
        }
    }

    /// <summary>
    /// Completes the queue, clears buffered chunks, and disposes the availability semaphore.
    /// </summary>
    /// <remarks>Idempotent. After dispose, further enqueue/dequeue/drop throw.</remarks>
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
            _queuedBytes = 0;
        }

        _available.Dispose();
    }
}
