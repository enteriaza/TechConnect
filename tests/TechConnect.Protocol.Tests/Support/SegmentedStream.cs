// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol.Tests
// File:    SegmentedStream.cs
//
// Purpose:
// Provides test-only stream doubles that fragment reads and writes to exercise
// FrameReader and FrameWriter against partial I/O.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol.Tests.Support;

/// <summary>
/// Test stream that stores writes contiguously and returns reads in configurable chunk sizes.
/// </summary>
/// <remarks>
/// Simulates fragmented TCP delivery so frame readers must reassemble headers and
/// payloads across multiple <see cref="Stream.ReadAsync(System.Memory{byte}, System.Threading.CancellationToken)"/>
/// calls. Seeking is not supported. Not thread-safe for concurrent readers/writers.
/// </remarks>
internal sealed class SegmentedMemoryStream : Stream
{
    /// <summary>
    /// Contiguous backing store for all bytes written through this stream.
    /// </summary>
    private readonly MemoryStream _buffer = new();

    /// <summary>
    /// Maximum number of bytes returned by a single read operation.
    /// </summary>
    private readonly int _readChunkSize;

    /// <summary>
    /// Logical read cursor into <see cref="_buffer"/>, independent of the backing stream's Position.
    /// </summary>
    private long _readPosition;

    /// <summary>
    /// Initializes a segmented memory stream.
    /// </summary>
    /// <param name="readChunkSize">
    /// Maximum bytes per read; values less than 1 are coerced to 1.
    /// Defaults to <see cref="int.MaxValue"/> (effectively unfragmented reads).
    /// </param>
    public SegmentedMemoryStream(int readChunkSize = int.MaxValue)
    {
        _readChunkSize = Math.Max(1, readChunkSize);
    }

    /// <summary>
    /// Gets a value indicating that the stream supports reading.
    /// </summary>
    public override bool CanRead => true;

    /// <summary>
    /// Gets a value indicating that seeking is not supported.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets a value indicating that the stream supports writing.
    /// </summary>
    public override bool CanWrite => true;

    /// <summary>
    /// Gets the number of bytes currently stored in the backing buffer.
    /// </summary>
    public override long Length => _buffer.Length;

    /// <summary>
    /// Getting or setting position is not supported; use the internal read cursor instead.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Returns a copy of all bytes written so far.
    /// </summary>
    /// <returns>A newly allocated array of the backing buffer contents.</returns>
    public byte[] ToArray() => _buffer.ToArray();

    /// <summary>
    /// Flushes the backing <see cref="MemoryStream"/>.
    /// </summary>
    public override void Flush() => _buffer.Flush();

    /// <summary>
    /// Reads up to <see cref="_readChunkSize"/> available bytes into <paramref name="buffer"/>.
    /// </summary>
    /// <param name="buffer">Destination array.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Maximum bytes requested by the caller.</param>
    /// <returns>Bytes copied, or 0 at EOF relative to the read cursor.</returns>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var available = (int)(_buffer.Length - _readPosition);
        if (available <= 0)
        {
            return 0;
        }

        var toRead = Math.Min(count, Math.Min(available, _readChunkSize));
        var previous = _buffer.Position;
        _buffer.Position = _readPosition;
        var read = _buffer.Read(buffer, offset, toRead);
        _readPosition += read;
        _buffer.Position = previous;
        return read;
    }

    /// <summary>
    /// Asynchronously reads by delegating to the synchronous segmented <see cref="Read"/> path.
    /// </summary>
    /// <param name="buffer">Destination array.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Maximum bytes requested.</param>
    /// <param name="cancellationToken">Token checked before reading.</param>
    /// <returns>A task producing the number of bytes read.</returns>
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Read(buffer, offset, count);
    }

    /// <summary>
    /// Asynchronously reads into memory by copying through a temporary array.
    /// </summary>
    /// <param name="buffer">Destination memory.</param>
    /// <param name="cancellationToken">Token checked before reading.</param>
    /// <returns>A value task producing the number of bytes read.</returns>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temp = new byte[buffer.Length];
        var read = Read(temp, 0, temp.Length);
        temp.AsSpan(0, read).CopyTo(buffer.Span);
        return ValueTask.FromResult(read);
    }

    /// <summary>
    /// Appends bytes to the contiguous backing buffer.
    /// </summary>
    /// <param name="buffer">Source array.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes to append.</param>
    public override void Write(byte[] buffer, int offset, int count) =>
        _buffer.Write(buffer, offset, count);

    /// <summary>
    /// Asynchronously appends bytes by delegating to <see cref="Write"/>.
    /// </summary>
    /// <param name="buffer">Source array.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes to append.</param>
    /// <param name="cancellationToken">Token checked before writing.</param>
    /// <returns>A completed task after the write.</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously appends memory bytes to the backing buffer.
    /// </summary>
    /// <param name="buffer">Source memory.</param>
    /// <param name="cancellationToken">Token checked before writing.</param>
    /// <returns>A completed value task after the write.</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _buffer.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Seeking is not supported.
    /// </summary>
    /// <param name="offset">Ignored.</param>
    /// <param name="origin">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Changing length is not supported.
    /// </summary>
    /// <param name="value">Ignored.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Disposes the backing buffer when <paramref name="disposing"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _buffer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Write-only test stream that completes underlying writes in small chunks.
/// </summary>
/// <remarks>
/// Exercises callers that must tolerate partial <c>WriteAsync</c> progress on the
/// underlying stream. Read and seek are not supported.
/// </remarks>
internal sealed class ChunkedWriteStream : Stream
{
    /// <summary>
    /// Contiguous store receiving chunked write segments.
    /// </summary>
    private readonly MemoryStream _inner = new();

    /// <summary>
    /// Maximum number of bytes committed per iteration of <see cref="WriteAsync(System.ReadOnlyMemory{byte}, System.Threading.CancellationToken)"/>.
    /// </summary>
    private readonly int _writeChunkSize;

    /// <summary>
    /// Initializes a chunked write stream.
    /// </summary>
    /// <param name="writeChunkSize">
    /// Bytes written per inner iteration; values less than 1 are coerced to 1.
    /// </param>
    public ChunkedWriteStream(int writeChunkSize = 1) =>
        _writeChunkSize = Math.Max(1, writeChunkSize);

    /// <summary>
    /// Returns a copy of all bytes accepted so far.
    /// </summary>
    /// <returns>A newly allocated array of the inner buffer contents.</returns>
    public byte[] ToArray() => _inner.ToArray();

    /// <summary>
    /// Gets a value indicating that reading is not supported.
    /// </summary>
    public override bool CanRead => false;

    /// <summary>
    /// Gets a value indicating that seeking is not supported.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets a value indicating that writing is supported.
    /// </summary>
    public override bool CanWrite => true;

    /// <summary>
    /// Gets the number of bytes stored in the inner buffer.
    /// </summary>
    public override long Length => _inner.Length;

    /// <summary>
    /// Getting or setting position is not supported.
    /// </summary>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Flushes the inner <see cref="MemoryStream"/>.
    /// </summary>
    public override void Flush() => _inner.Flush();

    /// <summary>
    /// Reading is not supported.
    /// </summary>
    /// <param name="buffer">Ignored.</param>
    /// <param name="offset">Ignored.</param>
    /// <param name="count">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Appends the full buffer to the inner stream in one call (synchronous path).
    /// </summary>
    /// <param name="buffer">Source array.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes to append.</param>
    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    /// <summary>
    /// Writes <paramref name="buffer"/> to the inner stream in successive chunks of
    /// at most <see cref="_writeChunkSize"/> bytes.
    /// </summary>
    /// <param name="buffer">Source memory.</param>
    /// <param name="cancellationToken">Token checked between chunks.</param>
    /// <returns>A value task that completes when all bytes have been accepted.</returns>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = Math.Min(_writeChunkSize, buffer.Length - offset);
            await _inner.WriteAsync(buffer.Slice(offset, size), cancellationToken).ConfigureAwait(false);
            offset += size;
        }
    }

    /// <summary>
    /// Seeking is not supported.
    /// </summary>
    /// <param name="offset">Ignored.</param>
    /// <param name="origin">Ignored.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <summary>
    /// Changing length is not supported.
    /// </summary>
    /// <param name="value">Ignored.</param>
    /// <exception cref="NotSupportedException">Always thrown.</exception>
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>
    /// Disposes the inner buffer when <paramref name="disposing"/> is <see langword="true"/>.
    /// </summary>
    /// <param name="disposing">Whether managed resources should be released.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
