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
/// <para>
/// Does not affect the underlying serial byte stream used for remote forwarding.
/// Only <see cref="SerialDataDirection.Rx"/> chunks are enqueued for display.
/// When the pending display buffer exceeds its soft cap, the oldest pending bytes
/// are dropped and counted in <see cref="PendingBytesDropped"/> — transport RX is
/// never discarded by this component. Arbitrary drops can split UTF-8 or VT sequences;
/// the UI flush path must resynchronize the terminal parser when drops are reported.
/// </para>
/// <para>
/// Storage is a fixed-capacity ring (PERF-CLI-01/02): bulk <see cref="Span{T}"/> copies
/// on enqueue and O(1) oldest-byte discard on overflow (no per-byte <c>Add</c> and no
/// <c>RemoveRange</c> element shift).
/// </para>
/// </remarks>
internal sealed class SerialConsoleBuffer
{
    /// <summary>Default soft cap for undrained display bytes waiting for the UI flush.</summary>
    public const int DefaultMaxPendingBytes = 1_048_576;

    /// <summary>Lock protecting ring indices, count, and drop accounting.</summary>
    private readonly object _gate = new();

    /// <summary>Soft cap on undrained pending display bytes (also the ring length).</summary>
    private readonly int _maxPendingBytes;

    /// <summary>Fixed ring storage; length equals <see cref="_maxPendingBytes"/>.</summary>
    private readonly byte[] _ring;

    /// <summary>Index of the oldest pending byte within <see cref="_ring"/>.</summary>
    private int _head;

    /// <summary>Number of pending bytes currently stored in the ring.</summary>
    private int _count;

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
        _ring = new byte[maxPendingBytes];
    }

    /// <summary>
    /// Soft maximum undrained pending display bytes.
    /// </summary>
    public int MaxPendingBytes => _maxPendingBytes;

    /// <summary>
    /// Current undrained pending display byte count (for tests and diagnostics).
    /// </summary>
    public int PendingByteCount
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
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
            Append(chunk.Data.Span);
        }
    }

    /// <summary>
    /// Clears pending display bytes without resetting drop accounting.
    /// </summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _head = 0;
            _count = 0;
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

            if (_count == 0)
            {
                data = [];
                return false;
            }

            data = new byte[_count];
            CopyOut(data);
            _head = 0;
            _count = 0;
            return true;
        }
    }

    /// <summary>
    /// Appends <paramref name="src"/> into the ring, dropping oldest bytes when the soft cap is exceeded.
    /// </summary>
    /// <param name="src">RX octets to append (may wrap; may exceed capacity alone).</param>
    /// <remarks>Must be called while holding <see cref="_gate"/>.</remarks>
    private void Append(ReadOnlySpan<byte> src)
    {
        if (src.IsEmpty)
        {
            return;
        }

        // Incoming chunk alone fills or exceeds the soft cap: keep only its newest tail.
        if (src.Length >= _maxPendingBytes)
        {
            _pendingBytesDropped += _count + (src.Length - _maxPendingBytes);
            src = src[^_maxPendingBytes..];
            src.CopyTo(_ring);
            _head = 0;
            _count = _maxPendingBytes;
            return;
        }

        var overflow = _count + src.Length - _maxPendingBytes;
        if (overflow > 0)
        {
            _head = (_head + overflow) % _maxPendingBytes;
            _count -= overflow;
            _pendingBytesDropped += overflow;
        }

        var tail = (_head + _count) % _maxPendingBytes;
        var first = Math.Min(src.Length, _maxPendingBytes - tail);
        src[..first].CopyTo(_ring.AsSpan(tail));
        if (first < src.Length)
        {
            src[first..].CopyTo(_ring.AsSpan(0));
        }

        _count += src.Length;
    }

    /// <summary>
    /// Copies the current pending region into <paramref name="destination"/> in FIFO order.
    /// </summary>
    /// <param name="destination">Destination of length <see cref="_count"/>.</param>
    /// <remarks>Must be called while holding <see cref="_gate"/>.</remarks>
    private void CopyOut(Span<byte> destination)
    {
        var first = Math.Min(_count, _maxPendingBytes - _head);
        _ring.AsSpan(_head, first).CopyTo(destination);
        if (first < _count)
        {
            _ring.AsSpan(0, _count - first).CopyTo(destination[first..]);
        }
    }
}
