// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialConsoleBuffer.cs
//
// Purpose:
// Batches local serial RX octets for the VT100 terminal display without
// affecting the remote-forwarded serial byte stream.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Models;

namespace TechConnect.Client.Forms;

/// <summary>
/// Thread-safe pending-byte buffer between serial RX and the UI-thread terminal host.
/// </summary>
/// <remarks>
/// Does not affect the underlying serial byte stream used for remote forwarding.
/// Only <see cref="SerialDataDirection.Rx"/> chunks are enqueued for display.
/// When the pending display buffer exceeds its soft cap, the oldest pending bytes
/// are dropped and counted in <see cref="PendingBytesDropped"/> — transport RX is
/// never discarded by this component.
/// </remarks>
internal sealed class SerialConsoleBuffer
{
    /// <summary>Default soft cap for undrained display bytes waiting for the UI flush.</summary>
    public const int DefaultMaxPendingBytes = 1_048_576;

    /// <summary>Lock protecting pending bytes and drop accounting.</summary>
    private readonly object _gate = new();

    /// <summary>Soft cap on undrained pending display bytes.</summary>
    private readonly int _maxPendingBytes;

    /// <summary>Pending RX octets awaiting UI-thread drain into the terminal host.</summary>
    private readonly List<byte> _pending = new();

    /// <summary>Cumulative pending display bytes dropped because the soft cap was exceeded.</summary>
    private long _pendingBytesDropped;

    /// <summary>
    /// Initializes a buffer with the specified pending-byte budget.
    /// </summary>
    /// <param name="maxPendingBytes">
    /// Soft maximum undrained bytes; must be at least 4096. Excess drops oldest pending display bytes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxPendingBytes"/> is below 4096.</exception>
    public SerialConsoleBuffer(int maxPendingBytes = DefaultMaxPendingBytes)
    {
        if (maxPendingBytes < 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingBytes));
        }

        _maxPendingBytes = maxPendingBytes;
    }

    /// <summary>
    /// Cumulative count of pending display bytes dropped after the soft cap was exceeded.
    /// </summary>
    public long PendingBytesDropped
    {
        get
        {
            lock (_gate)
            {
                return _pendingBytesDropped;
            }
        }
    }

    /// <summary>
    /// Enqueues a serial chunk for display when it is RX data.
    /// </summary>
    /// <param name="chunk">Transferred serial chunk; TX is ignored for console display.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is <see langword="null"/>.</exception>
    public void Enqueue(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        // Local console shows device RX only. TX remains available via stats / Serilog.
        if (chunk.Direction != SerialDataDirection.Rx || chunk.Data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            var span = chunk.Data.Span;
            EnsureCapacity(span.Length);
            for (var i = 0; i < span.Length; i++)
            {
                _pending.Add(span[i]);
            }

            TrimOverflow();
        }
    }

    /// <summary>
    /// Clears pending display bytes without resetting drop accounting.
    /// </summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    /// <summary>
    /// Drains pending bytes for writing into the terminal host on the UI thread.
    /// </summary>
    /// <param name="data">Copied pending octets, or empty when nothing is pending.</param>
    /// <param name="droppedSincePreviousDrain">
    /// Display bytes dropped due to the soft cap since the previous successful drain (or construction).
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="data"/> contains drained content.</returns>
    public bool TryDrain(out byte[] data, out long droppedSincePreviousDrain)
    {
        lock (_gate)
        {
            droppedSincePreviousDrain = _pendingBytesDropped;
            _pendingBytesDropped = 0;

            if (_pending.Count == 0)
            {
                data = Array.Empty<byte>();
                return false;
            }

            data = _pending.ToArray();
            _pending.Clear();
            return true;
        }
    }

    /// <summary>
    /// Ensures the pending list can accept <paramref name="additional"/> bytes after overflow trim.
    /// </summary>
    /// <param name="additional">Bytes about to be appended.</param>
    private void EnsureCapacity(int additional)
    {
        var required = _pending.Count + additional;
        if (required <= _pending.Capacity)
        {
            return;
        }

        _pending.Capacity = Math.Max(required, _pending.Capacity * 2);
    }

    /// <summary>
    /// Drops oldest pending bytes when the soft cap is exceeded.
    /// </summary>
    private void TrimOverflow()
    {
        var overflow = _pending.Count - _maxPendingBytes;
        if (overflow <= 0)
        {
            return;
        }

        _pending.RemoveRange(0, overflow);
        _pendingBytesDropped += overflow;
    }
}
