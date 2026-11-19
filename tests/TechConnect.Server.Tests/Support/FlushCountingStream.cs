// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FlushCountingStream.cs
//
// Purpose:
// Stream decorator that counts Flush / FlushAsync while forwarding all I/O —
// used to assert PERF-SRV-03 (CLI WriteRawAsync does not flush after each write).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Forwards to an inner stream while counting flush invocations.
/// </summary>
internal sealed class FlushCountingStream : Stream
{
    private readonly Stream _inner;

    /// <summary>Creates a counting wrapper around <paramref name="inner"/>.</summary>
    public FlushCountingStream(Stream inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    // Plain counter (not Interlocked): Stream is MarshalByRefObject; CLI writes are gated.
    private long _flushCalls;

    /// <summary>Number of <see cref="Flush"/> / <see cref="FlushAsync"/> calls observed.</summary>
    public long FlushCalls => _flushCalls;

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush()
    {
        _flushCalls++;
        _inner.Flush();
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        _flushCalls++;
        return _inner.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => _inner.SetLength(value);

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    /// <inheritdoc />
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // Do not dispose inner — owned by DuplexMemoryPipe.
        base.Dispose(disposing);
    }
}
