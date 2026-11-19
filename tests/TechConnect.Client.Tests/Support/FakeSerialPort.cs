// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    FakeSerialPort.cs
//
// Purpose:
// In-memory ISerialPort / ISerialPortFactory / Stream fakes for serial connection
// and pipeline tests without real COM hardware.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.IO.Ports;
using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Controllable in-memory <see cref="ISerialPort"/> with injectable RX, write capture,
/// open/config failures, loopback, and output-queue drain simulation.
/// </summary>
/// <remarks>
/// Limitation: does not emulate Windows driver timing, hardware flow control signalling,
/// or OS-level access conflicts beyond the options flags below.
/// </remarks>
internal sealed class FakeSerialPort : ISerialPort
{
    /// <summary>
    /// Synchronizes open/close/dispose state.
    /// </summary>
    private readonly object _gate = new();
    /// <summary>
    /// BaseStream implementation for this port.
    /// </summary>
    private readonly FakeSerialStream _stream;
    /// <summary>
    /// Whether Open succeeded and Close/Dispose have not cleared it.
    /// </summary>
    private bool _isOpen;
    /// <summary>
    /// Whether Dispose has completed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates a fake port for the given name with optional behaviour knobs.
    /// </summary>
    public FakeSerialPort(string portName, FakeSerialPortOptions? options = null)
    {
        PortName = portName;
        Options = options ?? new FakeSerialPortOptions();
        _stream = new FakeSerialStream(this);
    }

    /// <summary>
    /// Mutable behaviour knobs for this port instance.
    /// </summary>
    public FakeSerialPortOptions Options { get; }

    /// <inheritdoc />
    public string PortName { get; }

    /// <inheritdoc />
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _isOpen && !_disposed && !Options.Removed;
            }
        }
    }

    /// <summary>
    /// Simulated baud rate (default 115200).
    /// </summary>
    private int _baudRate = 115200;
    /// <summary>
    /// Simulated data bits (default 8).
    /// </summary>
    private int _dataBits = 8;
    /// <summary>
    /// Simulated parity (default None).
    /// </summary>
    private Parity _parity = Parity.None;
    /// <summary>
    /// Simulated stop bits (default One).
    /// </summary>
    private StopBits _stopBits = StopBits.One;
    /// <summary>
    /// Simulated handshake (default None).
    /// </summary>
    private Handshake _handshake = Handshake.None;

    /// <inheritdoc />
    public int BaudRate
    {
        get => _baudRate;
        set
        {
            ThrowIfConfigurationRejected();
            Options.OnBaudRateSet?.Invoke(value);
            _baudRate = value;
        }
    }

    /// <inheritdoc />
    public int DataBits
    {
        get => _dataBits;
        set
        {
            ThrowIfConfigurationRejected();
            _dataBits = value;
        }
    }

    /// <inheritdoc />
    public Parity Parity
    {
        get => _parity;
        set
        {
            ThrowIfConfigurationRejected();
            _parity = value;
        }
    }

    /// <inheritdoc />
    public StopBits StopBits
    {
        get => _stopBits;
        set
        {
            ThrowIfConfigurationRejected();
            _stopBits = value;
        }
    }

    /// <inheritdoc />
    public Handshake Handshake
    {
        get => _handshake;
        set
        {
            ThrowIfConfigurationRejected();
            _handshake = value;
        }
    }

    /// <inheritdoc />
    public bool DtrEnable { get; set; }

    /// <inheritdoc />
    public bool RtsEnable { get; set; }

    /// <inheritdoc />
    public int ReadTimeout { get; set; }

    /// <inheritdoc />
    public int WriteTimeout { get; set; }

    /// <inheritdoc />
    public int ReadBufferSize { get; set; } = 4096;

    /// <inheritdoc />
    public int WriteBufferSize { get; set; } = 2048;

    /// <summary>Simulated driver output queue depth (drained by the stream after each write).</summary>
    public int BytesToWrite { get; set; }

    /// <inheritdoc />
    public Stream BaseStream => _stream;

    /// <summary>
    /// Number of successful <see cref="Open"/> calls.
    /// </summary>
    public int OpenCount { get; private set; }

    /// <summary>
    /// Number of times <see cref="Dispose"/> completed disposal.
    /// </summary>
    public int DisposeCount { get; private set; }

    /// <summary>
    /// Chunks successfully written through <see cref="BaseStream"/>.
    /// </summary>
    public ConcurrentQueue<byte[]> WrittenChunks => _stream.WrittenChunks;

    /// <inheritdoc />
    public void Open()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Options.OpenException is not null)
            {
                throw Options.OpenException;
            }

            if (Options.Removed)
            {
                throw new UnauthorizedAccessException($"Port {PortName} is unavailable.");
            }

            _stream.PrepareForReopen();
            _isOpen = true;
            OpenCount++;
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        // Wake the stream outside _gate: SignalClosed may synchronously resume ReadAsync,
        // which calls IsOpen and would otherwise deadlock on this lock.
        lock (_gate)
        {
            _isOpen = false;
        }

        _stream.SignalClosed();
    }

    /// <summary>
    /// Marks the port removed and faults the base stream (simulates unplug while open).
    /// </summary>
    public void SimulateDeviceRemoval()
    {
        // Fault outside _gate for the same reason as Close (SemaphoreSlim may resume
        // ReadAsync synchronously → IsOpen → re-enter this lock).
        lock (_gate)
        {
            Options.Removed = true;
            _isOpen = false;
        }

        _stream.Fault(new IOException($"Device {PortName} was removed."));
    }

    /// <summary>Faults the base stream while leaving <see cref="IsOpen"/> true (write-path failures).</summary>
    public void FaultWrites(Exception exception) => _stream.Fault(exception);

    /// <summary>
    /// Injects RX bytes into the base stream for the reader loop to consume.
    /// </summary>
    public void InjectRx(ReadOnlySpan<byte> data) => _stream.InjectRx(data);

    /// <summary>
    /// Injects UTF-8 RX text into the base stream.
    /// </summary>
    public void InjectRx(string text) => InjectRx(System.Text.Encoding.UTF8.GetBytes(text));

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _isOpen = false;
            DisposeCount++;
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Throws and clears a one-shot <see cref="FakeSerialPortOptions.ConfigurationException"/> when set.
    /// </summary>
    private void ThrowIfConfigurationRejected()
    {
        // One-shot: allows restore/reopen paths after a rejected configuration attempt.
        var ex = Options.ConfigurationException;
        if (ex is null)
        {
            return;
        }

        Options.ConfigurationException = null;
        throw ex;
    }
}

/// <summary>
/// Behaviour knobs for <see cref="FakeSerialPort"/> open, config, read, write, and drain simulation.
/// </summary>
internal sealed class FakeSerialPortOptions
{
    /// <summary>
    /// When set, <see cref="FakeSerialPort.Open"/> throws this exception.
    /// </summary>
    public Exception? OpenException { get; set; }

    /// <summary>
    /// When true, the port is treated as unavailable / removed.
    /// </summary>
    public bool Removed { get; set; }

    /// <summary>
    /// One-shot exception thrown on the next line-property assignment, then cleared.
    /// </summary>
    public Exception? ConfigurationException { get; set; }

    /// <summary>Optional hook invoked when BaudRate is assigned (for concurrency tests).</summary>
    public Action<int>? OnBaudRateSet { get; set; }

    /// <summary>
    /// When true, ReadAsync waits without honouring CancellationToken (mirrors real SerialPort behaviour).
    /// Close/Dispose must unblock the read via SignalClosed.
    /// </summary>
    public bool IgnoreReadCancellation { get; set; }

    /// <summary>
    /// When true, writes are echoed into the RX stream (physical TX↔RX loopback simulation).
    /// </summary>
    public bool Loopback { get; set; }

    /// <summary>Optional delay applied inside each BaseStream write (backpressure / slow UART).</summary>
    public TimeSpan? WriteDelay { get; set; }

    /// <summary>
    /// When set, after each write <see cref="FakeSerialPort.BytesToWrite"/> stays at the written
    /// length until this delay elapses (simulates driver output-queue drain).
    /// </summary>
    public TimeSpan? OutputQueueDrainDelay { get; set; }

    /// <summary>When true, <see cref="FakeSerialPort.BytesToWrite"/> never drains (drain timeout tests).</summary>
    public bool HoldOutputQueue { get; set; }
}

/// <summary>
/// <see cref="ISerialPortFactory"/> that creates or tracks <see cref="FakeSerialPort"/> instances by name.
/// </summary>
/// <remarks>
/// Limitation: each <see cref="Create"/> replaces the dictionary entry for that name with a fresh instance
/// (matching production factory semantics); <see cref="GetOrAdd"/> reuses.
/// </remarks>
internal sealed class FakeSerialPortFactory : ISerialPortFactory
{
    /// <summary>
    /// Ports created or replaced by this factory.
    /// </summary>
    private readonly ConcurrentDictionary<string, FakeSerialPort> _ports = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Optional custom port constructor.
    /// </summary>
    private readonly Func<string, FakeSerialPort>? _factory;

    /// <summary>
    /// Creates a factory that optionally uses a custom per-name port constructor.
    /// </summary>
    public FakeSerialPortFactory(Func<string, FakeSerialPort>? factory = null)
    {
        _factory = factory;
    }

    /// <summary>
    /// Ports created or replaced via <see cref="Create"/> / <see cref="GetOrAdd"/>, keyed by port name.
    /// </summary>
    public IReadOnlyDictionary<string, FakeSerialPort> Ports => _ports;

    /// <summary>
    /// Number of times <see cref="Create"/> was invoked.
    /// </summary>
    public int CreateCount { get; private set; }

    /// <summary>
    /// Returns an existing fake port for <paramref name="portName"/> or creates one.
    /// </summary>
    public FakeSerialPort GetOrAdd(string portName)
    {
        return _ports.GetOrAdd(portName, name => _factory?.Invoke(name) ?? new FakeSerialPort(name));
    }

    /// <inheritdoc />
    public ISerialPort Create(string portName)
    {
        CreateCount++;
        // Match production factory semantics: each Create returns a fresh port instance.
        var port = _factory?.Invoke(portName) ?? new FakeSerialPort(portName);
        _ports[portName] = port;
        return port;
    }
}

/// <summary>
/// Non-seekable duplex stream backing <see cref="FakeSerialPort"/>: queued RX, captured writes,
/// optional loopback, fault/close signalling, and simulated output-queue drain.
/// </summary>
/// <remarks>
/// Limitation: synchronous Read/Write block on async counterparts; Length/Position/Seek are unsupported.
/// </remarks>
internal sealed class FakeSerialStream : Stream
{
    /// <summary>
    /// Owning FakeSerialPort for open/option checks.
    /// </summary>
    private readonly FakeSerialPort _owner;
    /// <summary>
    /// Queued RX chunks awaiting ReadAsync.
    /// </summary>
    private readonly ConcurrentQueue<byte[]> _rx = new();
    /// <summary>
    /// Signals that RX data or close/fault woke a waiter.
    /// </summary>
    private readonly SemaphoreSlim _rxAvailable = new(0);

    /// <summary>
    /// Synchronizes fault/close flags for the stream.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Current partially-consumed RX chunk.
    /// </summary>
    private byte[]? _partial;
    /// <summary>
    /// Offset within _partial.
    /// </summary>
    private int _partialOffset;
    /// <summary>
    /// Sticky fault thrown by subsequent I/O.
    /// </summary>
    private Exception? _fault;
    /// <summary>
    /// Whether SignalClosed/Fault marked the stream closed.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Whether Dispose has completed for this stream.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Creates the stream owned by the given fake port.
    /// </summary>
    public FakeSerialStream(FakeSerialPort owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Chunks accepted by successful writes.
    /// </summary>
    public ConcurrentQueue<byte[]> WrittenChunks { get; } = new();

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Enqueues RX bytes and signals a waiting reader.
    /// </summary>
    public void InjectRx(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _rx.Enqueue(data.ToArray());
        _rxAvailable.Release();
    }

    /// <summary>
    /// Marks the stream faulted/closed and wakes waiters so subsequent reads/writes throw.
    /// </summary>
    public void Fault(Exception exception)
    {
        lock (_gate)
        {
            _fault = exception;
            _closed = true;
        }

        try
        {
            _rxAvailable.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>
    /// Marks the stream closed and wakes waiters (EOF / unblock IgnoreReadCancellation readers).
    /// </summary>
    public void SignalClosed()
    {
        lock (_gate)
        {
            _closed = true;
        }

        try
        {
            _rxAvailable.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    /// <summary>Clears close/fault state so a Close→Open cycle can resume ReadAsync.</summary>
    public void PrepareForReopen()
    {
        lock (_gate)
        {
            _closed = false;
            _fault = null;
        }
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            ThrowIfFaulted();

            if (_partial is not null)
            {
                return ConsumePartial(buffer);
            }

            if (_rx.TryDequeue(out var chunk))
            {
                _partial = chunk;
                _partialOffset = 0;
                return ConsumePartial(buffer);
            }

            lock (_gate)
            {
                if (_closed || _disposed || !_owner.IsOpen)
                {
                    return 0;
                }
            }

            var waitToken = _owner.Options.IgnoreReadCancellation
                ? CancellationToken.None
                : cancellationToken;
            await _rxAvailable.WaitAsync(waitToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted();

        if (!_owner.IsOpen)
        {
            throw new InvalidOperationException("The port is closed.");
        }

        if (_owner.Options.WriteDelay is { } writeDelay)
        {
            await Task.Delay(writeDelay, cancellationToken).ConfigureAwait(false);
        }

        var copy = buffer.ToArray();
        WrittenChunks.Enqueue(copy);
        if (_owner.Options.Loopback)
        {
            InjectRx(copy);
        }

        if (_owner.Options.HoldOutputQueue || _owner.Options.OutputQueueDrainDelay is not null)
        {
            _owner.BytesToWrite = copy.Length;
            if (!_owner.Options.HoldOutputQueue && _owner.Options.OutputQueueDrainDelay is { } drainDelay)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(drainDelay, CancellationToken.None).ConfigureAwait(false);
                        _owner.BytesToWrite = 0;
                    }
                    catch
                    {
                        _owner.BytesToWrite = 0;
                    }
                });
            }
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfFaulted();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            SignalClosed();
            _rxAvailable.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Copies from the current partial RX chunk into <paramref name="buffer"/>.
    /// </summary>
    private int ConsumePartial(Memory<byte> buffer)
    {
        var source = _partial!;
        var remaining = source.Length - _partialOffset;
        var toCopy = Math.Min(remaining, buffer.Length);
        source.AsSpan(_partialOffset, toCopy).CopyTo(buffer.Span);
        _partialOffset += toCopy;
        if (_partialOffset >= source.Length)
        {
            _partial = null;
            _partialOffset = 0;
        }

        return toCopy;
    }

    /// <summary>
    /// Throws the stored fault or <see cref="ObjectDisposedException"/> when applicable.
    /// </summary>
    private void ThrowIfFaulted()
    {
        lock (_gate)
        {
            if (_fault is not null)
            {
                throw _fault;
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FakeSerialStream));
            }
        }
    }
}
