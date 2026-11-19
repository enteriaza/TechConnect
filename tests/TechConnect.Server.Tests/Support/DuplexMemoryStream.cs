// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DuplexMemoryStream.cs
//
// Purpose:
// Provides linked duplex memory streams for in-process SSH CLI
// session unit tests without a real TCP/SSH stack.
//
// -----------------------------------------------------------------------------
namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Two linked memory streams: writing to <see cref="Client"/> is readable from <see cref="Server"/>, and vice versa.
/// </summary>
/// <remarks>
/// Limitations: in-memory only; not thread-safe for concurrent writers on the same half;
/// seeking unsupported. Used to drive <see cref="TechConnect.Server.Ssh.Cli.TechConnectCliSession"/>
/// without TCP/SSH.
/// </remarks>
internal sealed class DuplexMemoryPipe : IAsyncDisposable
{
    /// <summary>Pipe carrying client→server bytes.</summary>
    private readonly LinkedPipe _clientToServer = new();

    /// <summary>Pipe carrying server→client bytes.</summary>
    private readonly LinkedPipe _serverToClient = new();

    /// <summary>Client-side half-duplex stream.</summary>
    public Stream Client => new HalfDuplexStream(_clientToServer, _serverToClient);

    /// <summary>Server-side half-duplex stream.</summary>
    public Stream Server => new HalfDuplexStream(_serverToClient, _clientToServer);
    /// <summary>
    /// Completes both linked pipes so pending reads observe EOF.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        _clientToServer.Dispose();
        _serverToClient.Dispose();
        return ValueTask.CompletedTask;
    }
    /// <summary>
    /// Unidirectional in-memory byte queue with async wait for readers.
    /// </summary>
    private sealed class LinkedPipe : IDisposable
    {
        /// <summary>Signaled when bytes are enqueued or the pipe is completed.</summary>
        private readonly SemaphoreSlim _data = new(0);

        /// <summary>Protects <c>_queue</c> and <c>_completed</c>.</summary>
        private readonly object _gate = new();

        /// <summary>Pending bytes written by the peer half.</summary>
        private readonly Queue<byte> _queue = new();

        /// <summary>When true, further writes throw and empty reads return 0 (EOF).</summary>
        private bool _completed;

        /// <summary>
        /// Enqueues <paramref name="buffer"/> and wakes a waiting reader when non-empty.
        /// </summary>
        /// <param name="buffer">Bytes to append.</param>
        /// <exception cref="ObjectDisposedException">The pipe was completed.</exception>
        public void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_gate)
            {
                if (_completed)
                {
                    throw new ObjectDisposedException(nameof(LinkedPipe));
                }

                foreach (var b in buffer)
                {
                    _queue.Enqueue(b);
                }
            }

            if (buffer.Length > 0)
            {
                _data.Release();
            }
        }
        /// <summary>
        /// Dequeues into <paramref name="buffer"/>, waiting when empty until data or completion.
        /// </summary>
        /// <param name="buffer">Destination memory.</param>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>Bytes copied, or 0 on EOF after completion.</returns>
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_queue.Count > 0)
                    {
                        var n = Math.Min(buffer.Length, _queue.Count);
                        for (var i = 0; i < n; i++)
                        {
                            buffer.Span[i] = _queue.Dequeue();
                        }

                        return n;
                    }

                    if (_completed)
                    {
                        return 0;
                    }
                }

                await _data.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        /// <summary>
        /// Marks the pipe complete (EOF for readers) and wakes waiters.
        /// </summary>
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
            }

            try
            {
                _data.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
        /// <summary>
        /// Completes the pipe and disposes the wait semaphore.
        /// </summary>
        public void Dispose()
        {
            Complete();
            _data.Dispose();
        }
    }
    /// <summary>
    /// Stream facade: writes go to one <see cref="LinkedPipe"/>, reads come from the other.
    /// </summary>
    private sealed class HalfDuplexStream : Stream
    {
        /// <summary>Outbound pipe (peer reads from this).</summary>
        private readonly LinkedPipe _write;

        /// <summary>Inbound pipe (peer writes to this).</summary>
        private readonly LinkedPipe _read;

        /// <summary>
        /// Binds this half to an outbound write pipe and inbound read pipe.
        /// </summary>
        /// <param name="write">Pipe that receives bytes written by this stream.</param>
        /// <param name="read">Pipe that supplies bytes for reads.</param>
        public HalfDuplexStream(LinkedPipe write, LinkedPipe read)
        {
            _write = write;
            _read = read;
        }
        /// <summary>Always readable (may block until peer writes or completes).</summary>
        public override bool CanRead => true;

        /// <summary>Seeking is unsupported.</summary>
        public override bool CanSeek => false;

        /// <summary>Always writable until disposed/completed.</summary>
        public override bool CanWrite => true;

        /// <summary>Not supported; throws <see cref="NotSupportedException"/>.</summary>
        public override long Length => throw new NotSupportedException();

        /// <summary>Not supported; get/set throw <see cref="NotSupportedException"/>.</summary>
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        /// <summary>No-op; data is immediately available to the peer pipe.</summary>
        public override void Flush()
        {
        }
        /// <summary>No-op async flush.</summary>
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        /// <summary>Synchronous read via <see cref="ReadAsync(Memory{byte}, CancellationToken)"/>.</summary>
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        /// <summary>Reads from the inbound linked pipe.</summary>
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _read.ReadAsync(buffer, cancellationToken);
        /// <summary>Synchronous write via <see cref="WriteAsync(ReadOnlyMemory{byte}, CancellationToken)"/>.</summary>
        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        /// <summary>Writes to the outbound linked pipe (peer can read immediately).</summary>
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _write.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        /// <summary>Not supported.</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>Not supported.</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Completes both pipes so the peer observes EOF on the opposite half.
        /// </summary>
        /// <param name="disposing">Whether called from Dispose.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _write.Complete();
                _read.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
