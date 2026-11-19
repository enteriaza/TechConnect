// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    FakeRemoteTransport.cs
//
// Purpose:
// In-memory IRemoteConsoleTransport and ISerialConnectionService fakes for
// session, reconnect, and pipeline tests without real TCP/mTLS or COM hardware.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Threading.Channels;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Controllable in-memory <see cref="IRemoteConsoleTransport"/> that records sends,
/// can hold/fail connect, and can emit inbound serial/control/fault events.
/// </summary>
/// <remarks>
/// Limitation: does not speak real framing or TLS;
/// concurrent connection accounting is approximate for test assertions only.
/// </remarks>
internal sealed class FakeRemoteTransport : IRemoteConsoleTransport
{
    /// <summary>
    /// Serializes connect/disconnect.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    /// <summary>
    /// Optional channel capturing sent serial payloads.
    /// </summary>
    private readonly Channel<byte[]> _sendChannel = Channel.CreateUnbounded<byte[]>();
    /// <summary>
    /// ConnectAsync entry counter.
    /// </summary>
    private int _connectCalls;
    /// <summary>
    /// Approximate active connection count.
    /// </summary>
    private int _activeConnections;
    /// <summary>
    /// Whether ConnectAsync completed successfully.
    /// </summary>
    private bool _connected;
    /// <summary>
    /// Whether DisposeAsync completed.
    /// </summary>
    private bool _disposed;
    /// <summary>
    /// Optional hold for ConnectAsync until ReleaseHeldConnect.
    /// </summary>
    private TaskCompletionSource? _holdConnect;
    /// <summary>
    /// Optional exception thrown from ConnectAsync.
    /// </summary>
    private Exception? _connectException;
    /// <summary>
    /// Optional exception thrown from SendSerialDataAsync.
    /// </summary>
    private Exception? _sendException;

    /// <inheritdoc />
    public bool IsConnected => _connected;

    /// <summary>
    /// Number of times <see cref="ConnectAsync"/> was entered.
    /// </summary>
    public int ConnectCallCount => Volatile.Read(ref _connectCalls);

    /// <summary>
    /// Approximate count of currently connected sessions (incremented on connect, decremented on drop/disconnect).
    /// </summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    /// <summary>
    /// High-water mark of <see cref="ActiveConnections"/> observed during the test.
    /// </summary>
    public int MaxConcurrentConnections { get; private set; }

    /// <summary>
    /// Serial payloads accepted by <see cref="SendSerialDataAsync"/>.
    /// </summary>
    public ConcurrentQueue<byte[]> SentPayloads { get; } = new();

    /// <summary>
    /// Control JSON strings accepted by <see cref="SendControlMessageAsync"/>.
    /// </summary>
    public ConcurrentQueue<string> SentControlMessages { get; } = new();

    /// <summary>
    /// Optional async handler invoked instead of raising <see cref="SerialDataReceived"/> when set.
    /// </summary>
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SerialDataHandler { get; set; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? SerialDataReceived;

    /// <inheritdoc />
    public event EventHandler<string>? ControlMessageReceived;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// Configures the next successful hold release path to throw from <see cref="ConnectAsync"/>.
    /// </summary>
    public void SetConnectException(Exception? exception) => _connectException = exception;

    /// <summary>
    /// Configures <see cref="SendSerialDataAsync"/> to throw when connected.
    /// </summary>
    public void SetSendException(Exception? exception) => _sendException = exception;

    /// <summary>
    /// Optional delay applied inside <see cref="SendSerialDataAsync"/> to simulate a slow remote.
    /// </summary>
    public TimeSpan? SendDelay { get; set; }

    /// <summary>
    /// Causes the next <see cref="ConnectAsync"/> to wait until <see cref="ReleaseHeldConnect"/>.
    /// </summary>
    public void HoldNextConnect() => _holdConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes a held connect wait started by <see cref="HoldNextConnect"/>.
    /// </summary>
    public void ReleaseHeldConnect() => _holdConnect?.TrySetResult();

    /// <summary>
    /// Synchronously delivers inbound serial data via handler or <see cref="SerialDataReceived"/>.
    /// </summary>
    public void EmitSerialData(ReadOnlyMemory<byte> data)
    {
        if (SerialDataHandler is { } handler)
        {
            handler(data, CancellationToken.None).GetAwaiter().GetResult();
            return;
        }

        SerialDataReceived?.Invoke(this, data);
    }

    /// <summary>
    /// Asynchronously delivers inbound serial data via handler or <see cref="SerialDataReceived"/>.
    /// </summary>
    public Task EmitSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (SerialDataHandler is { } handler)
        {
            return handler(data, cancellationToken);
        }

        SerialDataReceived?.Invoke(this, data);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Raises <see cref="ControlMessageReceived"/> with the given JSON.
    /// </summary>
    public void EmitControlMessage(string json) => ControlMessageReceived?.Invoke(this, json);

    /// <summary>
    /// Raises <see cref="Faulted"/> with the given message.
    /// </summary>
    public void EmitFault(string message) => Faulted?.Invoke(this, message);

    /// <summary>
    /// Marks the transport disconnected and raises <see cref="Faulted"/> (simulates mid-session drop).
    /// </summary>
    public void SimulateDrop()
    {
        _connected = false;
        Interlocked.Decrement(ref _activeConnections);
        Faulted?.Invoke(this, "Remote transport dropped.");
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _connectCalls);

            // Keep _holdConnect assigned until ReleaseHeldConnect completes it.
            var hold = _holdConnect;
            if (hold is not null)
            {
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (ReferenceEquals(_holdConnect, hold))
                {
                    _holdConnect = null;
                }
            }

            if (_connectException is not null)
            {
                throw _connectException;
            }

            if (_connected)
            {
                throw new InvalidOperationException("Already connected.");
            }

            _connected = true;
            var active = Interlocked.Increment(ref _activeConnections);
            MaxConcurrentConnections = Math.Max(MaxConcurrentConnections, active);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
        {
            throw new RemoteTransportUnavailableException("Not connected.");
        }

        if (_sendException is not null)
        {
            throw _sendException;
        }

        if (SendDelay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        var copy = data.ToArray();
        SentPayloads.Enqueue(copy);
        await _sendChannel.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendControlMessageAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_connected)
        {
            return Task.FromException(new RemoteTransportUnavailableException("Not connected."));
        }

        SentControlMessages.Enqueue(json);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connected)
            {
                _connected = false;
                Interlocked.Decrement(ref _activeConnections);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Dispose();
        _sendChannel.Writer.TryComplete();
    }
}

/// <summary>
/// Controllable in-memory <see cref="ISerialConnectionService"/> for session/UI tests.
/// </summary>
/// <remarks>
/// Limitation: <see cref="RunLoopbackProbeAsync"/> uses a short fixed delay rather than production probe timing;
/// diagnostics fields are simplified (many counters derived from byte totals only).
/// </remarks>
internal sealed class FakeSerialConnectionService : ISerialConnectionService
{
    /// <summary>
    /// Incoming RX channel for FakeSerialConnectionService.
    /// </summary>
    private readonly System.Threading.Channels.Channel<SerialDataChunk> _incoming =
        System.Threading.Channels.Channel.CreateUnbounded<SerialDataChunk>();

    /// <summary>
    /// Synchronizes open/config/write state.
    /// </summary>
    private readonly object _gate = new();
    /// <summary>
    /// Captured WriteAsync payloads.
    /// </summary>
    private readonly List<byte[]> _writes = new();
    /// <summary>
    /// Whether the fake serial service is open.
    /// </summary>
    private bool _isOpen;
    /// <summary>
    /// Current open port name, if any.
    /// </summary>
    private string? _portName;
    /// <summary>
    /// Current serial configuration.
    /// </summary>
    private SerialPortConfiguration _configuration = SerialPortConfiguration.CreateDefault();
    /// <summary>
    /// Accumulated RX bytes.
    /// </summary>
    private long _bytesReceived;
    /// <summary>
    /// Accumulated TX bytes.
    /// </summary>
    private long _bytesTransmitted;

    /// <summary>
    /// Bytes accepted from InjectRx/Open RX but not written to Incoming while ingress was disabled.
    /// </summary>
    private long _bytesSkippedRemoteIngress;

    /// <summary>
    /// Bytes successfully written onto Incoming.
    /// </summary>
    private long _bytesPublishedToIncoming;

    /// <summary>
    /// When non-zero, InjectRx publishes onto Incoming for remote bridging.
    /// </summary>
    private int _remoteIngressEnabled;

    /// <summary>
    /// In-flight InjectRx publishers between enablement check and TryWrite completion.
    /// </summary>
    private int _ingressPublishInFlight;

    /// <summary>
    /// Whether DisposeAsync completed for the fake serial service.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// OpenAsync entry counter.
    /// </summary>
    private int _openCallCount;
    /// <summary>
    /// Optional hold for OpenAsync until ReleaseHeldOpen.
    /// </summary>
    private TaskCompletionSource? _holdOpen;

    /// <inheritdoc />
    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _isOpen;
            }
        }
    }

    /// <inheritdoc />
    public string? PortName
    {
        get
        {
            lock (_gate)
            {
                return _portName;
            }
        }
    }

    /// <inheritdoc />
    public SerialPortConfiguration CurrentConfiguration
    {
        get
        {
            lock (_gate)
            {
                return _configuration;
            }
        }
    }

    /// <inheritdoc />
    public System.Threading.Channels.ChannelReader<SerialDataChunk> Incoming => _incoming.Reader;

    /// <inheritdoc />
    public bool IsRemoteIngressEnabled => Volatile.Read(ref _remoteIngressEnabled) != 0;

    /// <inheritdoc />
    public void SetRemoteIngressEnabled(bool enabled)
    {
        if (enabled)
        {
            Volatile.Write(ref _remoteIngressEnabled, 1);
            return;
        }

        Volatile.Write(ref _remoteIngressEnabled, 0);
        var spinner = new SpinWait();
        while (Volatile.Read(ref _ingressPublishInFlight) != 0)
        {
            spinner.SpinOnce();
        }
    }

    /// <inheritdoc />
    public int DrainPendingRemoteIngress()
    {
        var drainedBytes = 0;
        while (_incoming.Reader.TryRead(out var chunk))
        {
            drainedBytes += chunk.Data.Length;
        }

        if (drainedBytes > 0)
        {
            Interlocked.Add(ref _bytesSkippedRemoteIngress, drainedBytes);
        }

        return drainedBytes;
    }

    /// <summary>
    /// Snapshot of payloads accepted by <see cref="WriteAsync"/>.
    /// </summary>
    public IReadOnlyList<byte[]> Writes
    {
        get
        {
            lock (_gate)
            {
                return _writes.ToArray();
            }
        }
    }

    /// <summary>
    /// Count of successful writes recorded so far.
    /// </summary>
    public int WriteCount
    {
        get
        {
            lock (_gate)
            {
                return _writes.Count;
            }
        }
    }

    /// <summary>
    /// Number of times <see cref="OpenAsync"/> was entered.
    /// </summary>
    public int OpenCallCount => Volatile.Read(ref _openCallCount);

    /// <summary>
    /// Causes the next <see cref="OpenAsync"/> to wait until <see cref="ReleaseHeldOpen"/>.
    /// </summary>
    public void HoldNextOpen() =>
        _holdOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes a held open wait started by <see cref="HoldNextOpen"/>.
    /// </summary>
    public void ReleaseHeldOpen() => _holdOpen?.TrySetResult();

    /// <inheritdoc />
    public event EventHandler<SerialDataChunk>? DataTransferred;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <summary>When true, WriteAsync echoes into InjectRx (loopback simulation for UI tests).</summary>
    public bool LoopbackOnWrite { get; set; }

    /// <summary>Optional delay inside WriteAsync to simulate a slow serial writer.</summary>
    public TimeSpan? WriteDelay { get; set; }

    /// <inheritdoc />
    public SerialTrafficStats GetStats()
    {
        lock (_gate)
        {
            return new SerialTrafficStats
            {
                BytesReceived = _bytesReceived,
                BytesTransmitted = _bytesTransmitted
            };
        }
    }

    /// <inheritdoc />
    public SerialPortEffectiveSettings? GetEffectiveSettings()
    {
        lock (_gate)
        {
            if (!_isOpen || _portName is null)
            {
                return null;
            }

            return new SerialPortEffectiveSettings
            {
                PortName = _portName,
                IsOpen = true,
                BaudRate = _configuration.BaudRate,
                DataBits = _configuration.DataBits,
                Parity = _configuration.Parity,
                StopBits = _configuration.StopBits,
                Handshake = _configuration.Handshake,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = -1,
                WriteTimeout = 5000,
                ReadBufferSize = 4096,
                WriteBufferSize = 2048
            };
        }
    }

    /// <inheritdoc />
    public SerialReceiveDiagnostics GetReceiveDiagnostics()
    {
        var stats = GetStats();
        return new SerialReceiveDiagnostics
        {
            IsReaderActive = IsOpen,
            ReadCallCount = stats.BytesReceived > 0 ? 1 : 0,
            ZeroReadCount = 0,
            BytesReceivedFromPort = stats.BytesReceived,
            BytesPublishedToIncomingChannel = Interlocked.Read(ref _bytesPublishedToIncoming),
            BytesSkippedRemoteIngressDisabled = Interlocked.Read(ref _bytesSkippedRemoteIngress),
            IsRemoteIngressEnabled = IsRemoteIngressEnabled,
            PendingIncomingItems = _incoming.Reader.CanCount ? _incoming.Reader.Count : -1,
            BytesDeliveredToDataTransferred = stats.BytesReceived,
            LastRxUtc = stats.LastRxUtc,
            LastReadError = null,
            LastReadErrorUtc = null
        };
    }

    /// <inheritdoc />
    public async Task OpenAsync(string portName, SerialPortConfiguration? initialConfiguration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _openCallCount);

        // Keep _holdOpen assigned until ReleaseHeldOpen completes it (do not null beforehand).
        var hold = _holdOpen;
        if (hold is not null)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(_holdOpen, hold))
            {
                _holdOpen = null;
            }
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _isOpen = true;
            _portName = portName;
            _configuration = initialConfiguration ?? SerialPortConfiguration.CreateDefault();
            Volatile.Write(ref _remoteIngressEnabled, 0);
            Interlocked.Exchange(ref _bytesSkippedRemoteIngress, 0);
            Interlocked.Exchange(ref _bytesPublishedToIncoming, 0);
        }
    }

    /// <inheritdoc />
    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _isOpen = false;
            _portName = null;
        }

        _incoming.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteDelay is { } delay)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        byte[] copy;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen)
            {
                throw new InvalidOperationException("Port closed.");
            }

            copy = data.ToArray();
            _writes.Add(copy);
            _bytesTransmitted += copy.Length;
        }

        DataTransferred?.Invoke(this, new SerialDataChunk
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = SerialDataDirection.Tx,
            Data = copy
        });

        if (LoopbackOnWrite)
        {
            InjectRx(copy);
        }
    }

    /// <inheritdoc />
    public async Task<SerialLoopbackProbeResult> RunLoopbackProbeAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            return new SerialLoopbackProbeResult
            {
                PortName = PortName ?? "(none)",
                BytesWritten = 0,
                BytesReceivedDuringProbe = 0,
                ProbeMatched = false,
                TimedOut = false,
                Elapsed = TimeSpan.Zero,
                Summary = "Loopback probe aborted: serial port is not open.",
                EffectiveSettings = GetEffectiveSettings(),
                ReceiveDiagnosticsAfter = GetReceiveDiagnostics()
            };
        }

        var expected = probe.ToArray();
        var received = new List<byte>();
        void OnData(object? _, SerialDataChunk chunk)
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                received.AddRange(chunk.Data.Span);
            }
        }

        DataTransferred += OnData;
        try
        {
            await WriteAsync(probe, cancellationToken).ConfigureAwait(false);
            await Task.Delay(Math.Min(50, (int)timeout.TotalMilliseconds), cancellationToken).ConfigureAwait(false);

            var matched = IndexOfSequence(received, expected) >= 0;

            return new SerialLoopbackProbeResult
            {
                PortName = PortName!,
                BytesWritten = expected.Length,
                BytesReceivedDuringProbe = received.Count,
                ProbeMatched = matched,
                TimedOut = !matched,
                Elapsed = TimeSpan.FromMilliseconds(50),
                Summary = matched
                    ? "Loopback PROBE MATCHED (fake)."
                    : "Loopback probe did NOT match (fake).",
                EffectiveSettings = GetEffectiveSettings(),
                ReceiveDiagnosticsAfter = GetReceiveDiagnostics()
            };
        }
        finally
        {
            DataTransferred -= OnData;
        }
    }

    /// <summary>
    /// Returns the first index of <paramref name="needle"/> in <paramref name="haystack"/>, or -1.
    /// </summary>
    private static int IndexOfSequence(List<byte> haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Count - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public Task ApplyConfigurationAsync(SerialPortConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isOpen)
            {
                throw new InvalidOperationException("Port closed.");
            }

            _configuration = configuration;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Publishes an RX chunk (optionally onto Incoming when remote ingress is enabled) and raises
    /// <see cref="DataTransferred"/>.
    /// </summary>
    public void InjectRx(byte[] data)
    {
        var chunk = new SerialDataChunk
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = SerialDataDirection.Rx,
            Data = data
        };

        lock (_gate)
        {
            _bytesReceived += data.Length;
        }

        if (Volatile.Read(ref _remoteIngressEnabled) == 0)
        {
            Interlocked.Add(ref _bytesSkippedRemoteIngress, data.Length);
        }
        else
        {
            Interlocked.Increment(ref _ingressPublishInFlight);
            try
            {
                if (Volatile.Read(ref _remoteIngressEnabled) == 0)
                {
                    Interlocked.Add(ref _bytesSkippedRemoteIngress, data.Length);
                }
                else if (!_incoming.Writer.TryWrite(chunk))
                {
                    Interlocked.Add(ref _bytesSkippedRemoteIngress, data.Length);
                    throw new InvalidOperationException("Incoming channel completed.");
                }
                else
                {
                    Interlocked.Add(ref _bytesPublishedToIncoming, data.Length);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _ingressPublishInFlight);
            }
        }

        DataTransferred?.Invoke(this, chunk);
    }

    /// <summary>
    /// Completes the Incoming channel, optionally with an error (surfaces to RX bridge readers).
    /// </summary>
    /// <param name="error">
    /// When non-null, <see cref="Incoming"/> consumers observe this exception after queued items.
    /// </param>
    public void CompleteIncoming(Exception? error = null) =>
        _incoming.Writer.TryComplete(error);

    /// <summary>
    /// Raises <see cref="Faulted"/> with the given message.
    /// </summary>
    public void RaiseFault(string message) => Faulted?.Invoke(this, message);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
