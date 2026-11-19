// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialConnectionService.cs
//
// Purpose:
// Owns a single serial port session: open/close, RX reader, TX with driver drain,
// reconfigure, and loopback probe diagnostics.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.IO.Ports;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;

namespace TechConnect.Client.Services;

/// <summary>
/// Application-owned serial connection: one open port, background RX reader, gated TX.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership:</b> Creates ports via <see cref="ISerialPortFactory"/> and disposes them
/// on close/teardown. The idle <see cref="Incoming"/> channel starts completed so consumers
/// do not hang before the first open.
/// </para>
/// <para>
/// <b>Concurrency:</b> Lifecycle and write/reconfigure operations serialize on
/// <c>_gate</c>. A single background reader publishes RX. <see cref="DataTransferred"/>
/// and <see cref="Faulted"/> may raise on the reader or write paths; handlers must not
/// block indefinitely.
/// </para>
/// <para>
/// <b>Delivery semantics:</b>
/// <list type="bullet">
/// <item><see cref="SerialTrafficStats.BytesReceived"/> — bytes read from the port stream (driver RX).</item>
/// <item><see cref="SerialTrafficStats.BytesTransmitted"/> — bytes accepted by the write path
/// after the driver output queue reports drained for that write; not remote-device acceptance.</item>
/// <item><see cref="Incoming"/> — RX chunks published for remote bridging / consumers.</item>
/// <item><see cref="DataTransferred"/> — UI/diagnostic notifications for RX and TX copies.</item>
/// </list>
/// Opening the port does not prove a console device is attached or responding.
/// </para>
/// </remarks>
internal sealed class SerialConnectionService : ISerialConnectionService
{
    /// <summary>
    /// Unbounded incoming channel: single writer (reader loop), multiple readers allowed.
    /// </summary>
    private static readonly UnboundedChannelOptions IncomingChannelOptions = new()
    {
        SingleReader = false,
        SingleWriter = true,
        AllowSynchronousContinuations = false
    };

    /// <summary>Structured logger (payload bytes are never logged).</summary>
    private readonly ILogger<SerialConnectionService> _logger;

    /// <summary>Clock for chunk timestamps and diagnostics (not used for drain wall-clock timeout).</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates concrete <see cref="ISerialPort"/> instances.</summary>
    private readonly ISerialPortFactory _portFactory;

    /// <summary>Serializes open/close/write/reconfigure/dispose.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards RX/TX byte counters and last-activity timestamps.</summary>
    private readonly object _statsGate = new();

    /// <summary>Bytes read from the port stream since open (driver RX).</summary>
    private long _bytesReceived;

    /// <summary>
    /// Bytes counted after write + flush + driver output-queue drain (not device ACK).
    /// </summary>
    private long _bytesTransmitted;

    /// <summary>UTC of last successful RX publish into stats.</summary>
    private DateTimeOffset? _lastRxUtc;

    /// <summary>UTC of last successful TX stats update after driver drain.</summary>
    private DateTimeOffset? _lastTxUtc;

    /// <summary>Currently owned port, or null when disconnected.</summary>
    private ISerialPort? _port;

    /// <summary>Cancels the active background reader.</summary>
    private CancellationTokenSource? _readerCts;

    /// <summary>Background <see cref="ReadLoopAsync"/> task for the current generation.</summary>
    private Task? _readerTask;

    /// <summary>
    /// Reader generation; incremented on start/stop so stale readers do not raise <see cref="Faulted"/>.
    /// </summary>
    private int _readerGeneration;

    /// <summary>Current RX fan-out channel; replaced on each successful open.</summary>
    private Channel<SerialDataChunk> _incoming;

    /// <summary>Last successfully applied framing configuration.</summary>
    private SerialPortConfiguration _configuration = SerialPortConfiguration.CreateDefault();

    /// <summary>True after <see cref="DisposeAsync"/> begins.</summary>
    private bool _disposed;

    // Aggregated RX diagnostics (payloads never logged).

    /// <summary>Count of BaseStream.ReadAsync completions (including zero-byte).</summary>
    private long _rxReadCalls;

    /// <summary>Count of zero-or-negative reads treated as port closed.</summary>
    private long _rxZeroReads;

    /// <summary>RX bytes successfully written to <see cref="_incoming"/>.</summary>
    private long _rxBytesPublished;

    /// <summary>RX bytes delivered via <see cref="DataTransferred"/>.</summary>
    private long _rxUiNotifications;

    /// <summary>BytesReceived watermark used to throttle RX progress logs.</summary>
    private long _lastLoggedRxBytes;

    /// <summary>Last reader error summary (type + message), if any.</summary>
    private string? _lastReadError;

    /// <summary>UTC when <see cref="_lastReadError"/> was recorded.</summary>
    private DateTimeOffset? _lastReadErrorUtc;

    /// <summary>Guards <see cref="_loopbackProbeInProgress"/>.</summary>
    private readonly object _probeGate = new();

    /// <summary>True while a loopback probe is running (single probe at a time).</summary>
    private bool _loopbackProbeInProgress;

    /// <summary>
    /// Creates a disconnected service with a completed idle incoming channel.
    /// </summary>
    public SerialConnectionService(
        ILogger<SerialConnectionService> logger,
        TimeProvider timeProvider,
        ISerialPortFactory portFactory)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _portFactory = portFactory ?? throw new ArgumentNullException(nameof(portFactory));
        _incoming = Channel.CreateUnbounded<SerialDataChunk>(IncomingChannelOptions);
        // Idle channel starts completed so consumers do not hang before the first Open.
        _incoming.Writer.TryComplete();
    }

    /// <inheritdoc />
    public bool IsOpen => _port?.IsOpen == true;

    /// <inheritdoc />
    public string? PortName => _port?.PortName;

    /// <inheritdoc />
    public SerialPortConfiguration CurrentConfiguration => _configuration;

    /// <inheritdoc />
    /// <remarks>
    /// Completes on close/teardown. After open, carries RX chunks only (TX is notified via
    /// <see cref="DataTransferred"/> but not written to this channel).
    /// </remarks>
    public ChannelReader<SerialDataChunk> Incoming => _incoming.Reader;

    /// <summary>
    /// Raised for each RX/TX chunk after local acceptance (defensive copy for TX).
    /// </summary>
    /// <remarks>
    /// Handler exceptions are logged and swallowed so the reader/write path continues.
    /// </remarks>
    public event EventHandler<SerialDataChunk>? DataTransferred;

    /// <summary>
    /// Raised when the reader or write path detects a fault that ends useful I/O.
    /// </summary>
    /// <remarks>
    /// Intentional close/reconfigure invalidates the reader generation so those paths
    /// do not raise this event.
    /// </remarks>
    public event EventHandler<string>? Faulted;

    /// <inheritdoc />
    /// <remarks>
    /// <c>BytesReceived</c> is driver RX; <c>BytesTransmitted</c> is post-drain write success.
    /// </remarks>
    public SerialTrafficStats GetStats()
    {
        lock (_statsGate)
        {
            return new SerialTrafficStats
            {
                BytesReceived = _bytesReceived,
                BytesTransmitted = _bytesTransmitted,
                LastRxUtc = _lastRxUtc,
                LastTxUtc = _lastTxUtc
            };
        }
    }

    /// <inheritdoc />
    /// <returns>Snapshot of driver-reported settings, or null when no port instance exists.</returns>
    public SerialPortEffectiveSettings? GetEffectiveSettings()
    {
        var port = _port;
        if (port is null)
        {
            return null;
        }

        return CaptureEffectiveSettings(port);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Distinguishes port RX, channel publish, and UI notification counters for diagnostics.
    /// </remarks>
    public SerialReceiveDiagnostics GetReceiveDiagnostics()
    {
        var readerTask = _readerTask;
        var stats = GetStats();
        return new SerialReceiveDiagnostics
        {
            IsReaderActive = readerTask is { IsCompleted: false },
            ReadCallCount = Interlocked.Read(ref _rxReadCalls),
            ZeroReadCount = Interlocked.Read(ref _rxZeroReads),
            BytesReceivedFromPort = stats.BytesReceived,
            BytesPublishedToIncomingChannel = Interlocked.Read(ref _rxBytesPublished),
            BytesDeliveredToDataTransferred = Interlocked.Read(ref _rxUiNotifications),
            LastRxUtc = stats.LastRxUtc,
            LastReadError = _lastReadError,
            LastReadErrorUtc = _lastReadErrorUtc
        };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Validates configuration, opens the port, re-asserts modem lines, and starts the RX reader.
    /// Open success means Windows accepted the COM open — not that a remote console responded.
    /// </remarks>
    public async Task OpenAsync(
        string portName,
        SerialPortConfiguration? initialConfiguration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("A COM port name is required.", nameof(portName));
        }

        var configuration = initialConfiguration ?? SerialPortConfiguration.CreateDefault();
        SerialPortConfigurationValidator.Validate(configuration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_port?.IsOpen == true)
            {
                throw new InvalidOperationException(
                    $"A serial connection is already open on {_port.PortName}.");
            }

            await TearDownPortAsync(CancellationToken.None).ConfigureAwait(false);

            ResetStats();
            _configuration = configuration;
            _incoming = Channel.CreateUnbounded<SerialDataChunk>(IncomingChannelOptions);

            var port = _portFactory.Create(portName);
            ApplyPortSettings(port, configuration);

            _logger.LogInformation(
                "Opening serial port {PortName} with {Configuration}, DtrEnable={DtrEnable}, RtsEnable={RtsEnable}",
                portName,
                SerialPortConfigurationValidator.ToLogString(configuration),
                port.DtrEnable,
                port.RtsEnable);

            try
            {
                port.Open();
            }
            catch
            {
                port.Dispose();
                _incoming.Writer.TryComplete();
                throw;
            }

            // Re-assert modem-control lines after Open — some drivers reset them on open.
            ApplyModemControlLines(port, configuration);

            _port = port;
            ResetRxDiagnostics();
            StartReader_NoLock();

            var effective = CaptureEffectiveSettings(port);
            _logger.LogInformation(
                "Serial port effective settings after open: {EffectiveSettings}",
                effective.ToDiagnosticString());
            _logger.LogInformation(
                "Serial RX reader active={ReaderActive} on {PortName}",
                GetReceiveDiagnostics().IsReaderActive,
                portName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the reader, closes/disposes the port, and completes <see cref="Incoming"/>.
    /// No-ops when already disposed or no port is held.
    /// </remarks>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_port is null)
            {
                return;
            }

            var portName = _port.PortName;
            _logger.LogInformation("Closing serial port {PortName}", portName);
            await TearDownPortAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Serial port {PortName} closed", portName);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes to <see cref="ISerialPort.BaseStream"/>, flushes, then waits until
    /// <see cref="ISerialPort.BytesToWrite"/> returns to zero (driver output queue drain).
    /// Stats and TX <see cref="DataTransferred"/> fire only after that drain succeeds.
    /// This still does not prove a remote serial device received the bytes.
    /// Empty payloads are no-ops. Faults raise <see cref="Faulted"/> and rethrow.
    /// </remarks>
    public async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.IsEmpty)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var port = _port;
            if (port is null || !port.IsOpen)
            {
                throw new InvalidOperationException("Cannot write because the serial port is not open.");
            }

            try
            {
                await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await WaitForDriverOutputDrainAsync(port, data.Length, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                _logger.LogError(ex, "Serial write failed on {PortName}", port.PortName);
                RaiseFault($"Serial write failed on {port.PortName}.");
                throw;
            }

            var copy = data.ToArray();
            Publish(new SerialDataChunk
            {
                TimestampUtc = _timeProvider.GetUtcNow(),
                Direction = SerialDataDirection.Tx,
                Data = copy
            }, countTowardIncomingChannel: false);

            // BytesTransmitted counts bytes accepted by the serial driver write path after the
            // driver output queue has drained for this write (not device-level acceptance).
            lock (_statsGate)
            {
                _bytesTransmitted += copy.LongLength;
                _lastTxUtc = _timeProvider.GetUtcNow();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits until <see cref="ISerialPort.BytesToWrite"/> returns to zero (driver output queue empty).
    /// Stream write/flush on Windows serial ports do not guarantee that bytes have left the UART —
    /// only that they were accepted into the driver buffer.
    /// </summary>
    private static async Task WaitForDriverOutputDrainAsync(
        ISerialPort port,
        int bytesJustWritten,
        CancellationToken cancellationToken)
    {
        if (bytesJustWritten <= 0)
        {
            return;
        }

        var baud = Math.Max(port.BaudRate, 1);
        // 10 bits/byte on the wire for 8N1; 3x margin for scheduling + USB-serial buffering.
        // Wall-clock timeout: Task.Delay is real time, independent of TimeProvider.
        var timeoutMs = Math.Max(500L, (long)bytesJustWritten * 10L * 1000L * 3L / baud);
        var sw = Stopwatch.StartNew();

        while (port.IsOpen && port.BytesToWrite > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException(
                    $"Serial driver output queue did not drain on {port.PortName} " +
                    $"({port.BytesToWrite} byte(s) remain after {timeoutMs}ms).");
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Writes a probe via <see cref="WriteAsync"/> and watches <see cref="DataTransferred"/>
    /// for an RX echo containing the probe sequence. Requires a physical TX↔RX loopback
    /// (or equivalent echo). Success validates adapter + Windows + app RX path for that echo;
    /// it does not prove a device-console RX path without loopback. Only one probe may run
    /// at a time.
    /// </remarks>
    public async Task<SerialLoopbackProbeResult> RunLoopbackProbeAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (probe.IsEmpty)
        {
            throw new ArgumentException("A non-empty probe sequence is required.", nameof(probe));
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Probe timeout must be positive.");
        }

        lock (_probeGate)
        {
            if (_loopbackProbeInProgress)
            {
                throw new InvalidOperationException("A serial loopback probe is already in progress.");
            }

            _loopbackProbeInProgress = true;
        }

        var started = _timeProvider.GetTimestamp();
        try
        {
            if (!IsOpen || _port is null)
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
                    EffectiveSettings = null,
                    ReceiveDiagnosticsAfter = GetReceiveDiagnostics()
                };
            }

            var portName = _port.PortName;
            var effective = CaptureEffectiveSettings(_port);
            var expected = probe.ToArray();
            var received = new List<byte>(expected.Length);
            var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnData(object? sender, SerialDataChunk chunk)
            {
                if (chunk.Direction != SerialDataDirection.Rx || chunk.Data.IsEmpty)
                {
                    return;
                }

                lock (received)
                {
                    received.AddRange(chunk.Data.Span);
                    if (ContainsSequence(received, expected))
                    {
                        matched.TrySetResult();
                    }
                }
            }

            DataTransferred += OnData;
            try
            {
                _logger.LogInformation(
                    "Serial loopback probe starting on {PortName}: writing {ByteCount} diagnostic bytes (timeout {TimeoutMs}ms). Requires physical TX↔RX loopback.",
                    portName,
                    expected.Length,
                    (int)timeout.TotalMilliseconds);

                await WriteAsync(probe, cancellationToken).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                try
                {
                    await matched.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Probe window elapsed without a match.
                }

                int receivedCount;
                bool probeMatched;
                lock (received)
                {
                    receivedCount = received.Count;
                    probeMatched = ContainsSequence(received, expected);
                }

                var elapsed = _timeProvider.GetElapsedTime(started);
                var timedOut = !probeMatched;
                var summary = probeMatched
                    ? $"Loopback PROBE MATCHED on {portName}: wrote {expected.Length} bytes, observed {receivedCount} RX bytes during probe. Adapter + Windows + app RX path delivered the echo."
                    : $"Loopback probe did NOT match on {portName}: wrote {expected.Length} bytes, observed {receivedCount} RX bytes during probe window ({elapsed.TotalMilliseconds:0}ms). This does not prove device-console RX. Without a TX↔RX loopback jumper, TX success alone does not validate RX.";

                _logger.LogInformation(
                    "Serial loopback probe finished on {PortName}: Matched={Matched}, BytesWritten={BytesWritten}, BytesReceivedDuringProbe={BytesReceived}, TimedOut={TimedOut}",
                    portName,
                    probeMatched,
                    expected.Length,
                    receivedCount,
                    timedOut);

                return new SerialLoopbackProbeResult
                {
                    PortName = portName,
                    BytesWritten = expected.Length,
                    BytesReceivedDuringProbe = receivedCount,
                    ProbeMatched = probeMatched,
                    TimedOut = timedOut,
                    Elapsed = elapsed,
                    Summary = summary,
                    EffectiveSettings = effective,
                    ReceiveDiagnosticsAfter = GetReceiveDiagnostics()
                };
            }
            finally
            {
                DataTransferred -= OnData;
            }
        }
        finally
        {
            lock (_probeGate)
            {
                _loopbackProbeInProgress = false;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stops the reader and closes (without disposing) so pending ReadAsync can unblock,
    /// applies settings, reopens, re-asserts modem lines, and restarts the reader.
    /// On failure, attempts to restore the previous configuration; if restore fails the
    /// port is abandoned. Does not guarantee the attached device accepted the new framing.
    /// </remarks>
    public async Task ApplyConfigurationAsync(
        SerialPortConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configuration);
        SerialPortConfigurationValidator.Validate(configuration);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var port = _port;
            if (port is null || !port.IsOpen)
            {
                throw new InvalidOperationException("Cannot change serial configuration while disconnected.");
            }

            var previous = _configuration;
            var portName = port.PortName;

            _logger.LogInformation(
                "Applying serial configuration on {PortName}: {Configuration}",
                portName,
                SerialPortConfigurationValidator.ToLogString(configuration));

            // System.IO.Ports.SerialPort must not change framing while BaseStream.ReadAsync is pending.
            // Cancel the reader, close (not dispose) to unblock ReadAsync, wait for exit, then reopen.
            await StopReaderAsync(port, disposePort: false).ConfigureAwait(false);

            try
            {
                ApplyPortSettings(port, configuration);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Serial driver rejected configuration on {PortName}: {Configuration}",
                    portName,
                    SerialPortConfigurationValidator.ToLogString(configuration));

                await RecoverAfterFailedReconfigureAsync(port, previous, ex).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The serial driver rejected the configuration change on {portName}.",
                    ex);
            }

            try
            {
                port.Open();
                ApplyModemControlLines(port, configuration);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Serial port reopen failed on {PortName} after configuration change",
                    portName);

                await RecoverAfterFailedReconfigureAsync(port, previous, ex).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"The serial port {portName} could not be reopened with the requested configuration.",
                    ex);
            }

            _configuration = configuration;
            StartReader_NoLock();

            if (!port.IsOpen || _readerTask is null || _readerTask.IsCompleted)
            {
                var stateError = new InvalidOperationException(
                    $"Serial configuration reported success but the port {portName} is not operational.");
                _logger.LogError(stateError, "Post-reconfigure health check failed on {PortName}", portName);
                await AbandonPortAsync(port).ConfigureAwait(false);
                throw stateError;
            }

            _logger.LogInformation(
                "Serial configuration applied on {PortName}: {Configuration}",
                portName,
                SerialPortConfigurationValidator.ToLogString(configuration));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Marks disposed, tears down any open port under <c>_gate</c>, then disposes the gate.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownPortAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Starts a new background reader generation for the current open port.
    /// </summary>
    /// <remarks>Caller must hold <c>_gate</c> (or otherwise ensure exclusive lifecycle).</remarks>
    private void StartReader_NoLock()
    {
        if (_port is null || !_port.IsOpen)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _readerGeneration);
        _readerCts = new CancellationTokenSource();
        var ct = _readerCts.Token;
        var port = _port;
        _readerTask = Task.Run(() => ReadLoopAsync(port, ct, generation), CancellationToken.None);
        _logger.LogInformation(
            "Serial reader started on {PortName} (IsOpen={IsOpen}, DtrEnable={DtrEnable}, RtsEnable={RtsEnable})",
            port.PortName,
            port.IsOpen,
            port.DtrEnable,
            port.RtsEnable);
    }

    /// <summary>
    /// Cancels the reader, optionally closes/disposes the port to unblock stuck ReadAsync, and joins.
    /// </summary>
    /// <param name="portToUnblock">Port whose close unblocks a cancellation-ignoring ReadAsync.</param>
    /// <param name="disposePort">When true, dispose after close; when false, close only (reconfigure).</param>
    /// <remarks>
    /// Increments reader generation so intentional stops do not raise <see cref="Faulted"/>.
    /// Join waits up to 3 wall-clock seconds; timeout logs a warning and continues.
    /// </remarks>
    private async Task StopReaderAsync(ISerialPort? portToUnblock = null, bool disposePort = true)
    {
        var cts = _readerCts;
        var readerTask = _readerTask;
        _readerCts = null;
        _readerTask = null;

        // Invalidate the in-flight reader so intentional close/reconfigure does not raise Faulted.
        Interlocked.Increment(ref _readerGeneration);

        if (cts is null && readerTask is null)
        {
            if (portToUnblock is not null)
            {
                CloseAndOptionallyDispose(portToUnblock, disposePort);
            }

            return;
        }

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        // SerialPort.BaseStream.ReadAsync often ignores CancellationToken. Closing the
        // port unblocks the stuck read so teardown/reconfigure cannot hang indefinitely.
        if (portToUnblock is not null)
        {
            CloseAndOptionallyDispose(portToUnblock, disposePort);
        }

        if (readerTask is not null)
        {
            try
            {
                var completed = await Task.WhenAny(readerTask, Task.Delay(TimeSpan.FromSeconds(3)))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(completed, readerTask))
                {
                    _logger.LogWarning(
                        "Serial reader did not terminate within 3s after cancellation{PortAction}; continuing",
                        portToUnblock is null
                            ? string.Empty
                            : disposePort
                                ? "/port dispose"
                                : "/port close");
                }
                else
                {
                    await readerTask.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Serial reader ended with an unexpected exception during stop");
            }
        }

        cts?.Dispose();
        _logger.LogInformation("Serial reader stopped");
    }

    /// <summary>
    /// Best-effort close, optionally followed by dispose; close/dispose errors are debug-logged.
    /// </summary>
    private void CloseAndOptionallyDispose(ISerialPort port, bool disposePort)
    {
        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Serial port close while stopping reader reported an error");
        }
        finally
        {
            if (disposePort)
            {
                try
                {
                    port.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Serial port dispose while stopping reader reported an error");
                }
            }
        }
    }

    /// <summary>
    /// Attempts to restore <paramref name="previous"/> framing and restart the reader after a failed reconfigure.
    /// </summary>
    /// <param name="port">Port still owned by this service.</param>
    /// <param name="previous">Configuration that was active before the failed change.</param>
    /// <param name="primaryFailure">Original failure (logged context only).</param>
    /// <remarks>
    /// On restore failure, abandons the port via <see cref="AbandonPortAsync"/>.
    /// </remarks>
    private async Task RecoverAfterFailedReconfigureAsync(
        ISerialPort port,
        SerialPortConfiguration previous,
        Exception primaryFailure)
    {
        try
        {
            if (!port.IsOpen)
            {
                ApplyPortSettings(port, previous);
                port.Open();
                ApplyModemControlLines(port, previous);
            }

            _configuration = previous;
            StartReader_NoLock();
        }
        catch (Exception restoreEx)
        {
            _logger.LogError(
                restoreEx,
                "Failed to restore serial port {PortName} after configuration failure ({Primary})",
                port.PortName,
                primaryFailure.GetType().Name);
            await AbandonPortAsync(port).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clears ownership of <paramref name="port"/>, stops/disposes it, and completes Incoming.
    /// </summary>
    private async Task AbandonPortAsync(ISerialPort port)
    {
        if (ReferenceEquals(_port, port))
        {
            _port = null;
        }

        await StopReaderAsync(port, disposePort: true).ConfigureAwait(false);
        _incoming.Writer.TryComplete();
    }

    /// <summary>
    /// Releases the current port reference, stops the reader with dispose, and completes Incoming.
    /// </summary>
    /// <param name="cancellationToken">Currently unused; teardown always completes.</param>
    private async Task TearDownPortAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        var port = _port;
        _port = null;

        await StopReaderAsync(port, disposePort: true).ConfigureAwait(false);

        _incoming.Writer.TryComplete();
    }

    /// <summary>
    /// Background loop reading the port stream and publishing RX chunks.
    /// </summary>
    /// <param name="port">Port captured at reader start.</param>
    /// <param name="cancellationToken">Reader CTS token.</param>
    /// <param name="generation">Generation that must still be current to raise faults.</param>
    /// <remarks>
    /// Published bytes are what the driver delivered to the app — not proof of device intent.
    /// Zero-length reads are treated as closed. I/O faults raise <see cref="Faulted"/> only
    /// for the current generation.
    /// </remarks>
    private async Task ReadLoopAsync(ISerialPort port, CancellationToken cancellationToken, int generation)
    {
        var buffer = new byte[4096];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await port.BaseStream
                        .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException)
                {
                    if (cancellationToken.IsCancellationRequested || !IsCurrentReader(generation))
                    {
                        break;
                    }

                    RecordReadError(ex);
                    _logger.LogError(ex, "Serial read failed on {PortName}", port.PortName);
                    RaiseFault($"The serial device on {port.PortName} became unavailable.");
                    break;
                }

                Interlocked.Increment(ref _rxReadCalls);

                if (read <= 0)
                {
                    Interlocked.Increment(ref _rxZeroReads);
                    if (!cancellationToken.IsCancellationRequested && IsCurrentReader(generation))
                    {
                        _logger.LogWarning(
                            "Serial read returned {Bytes} bytes on {PortName}; treating port as closed",
                            read,
                            port.PortName);
                        RaiseFault($"The serial device on {port.PortName} closed the connection.");
                    }

                    break;
                }

                var payload = new byte[read];
                Buffer.BlockCopy(buffer, 0, payload, 0, read);

                var chunk = new SerialDataChunk
                {
                    TimestampUtc = _timeProvider.GetUtcNow(),
                    Direction = SerialDataDirection.Rx,
                    Data = payload
                };

                // Stats reflect bytes received from the port, independent of UI rendering.
                lock (_statsGate)
                {
                    _bytesReceived += read;
                    _lastRxUtc = chunk.TimestampUtc;
                }

                Publish(chunk, countTowardIncomingChannel: true);
                MaybeLogRxProgress(port.PortName);
            }
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested && IsCurrentReader(generation))
            {
                RecordReadError(ex);
                _logger.LogError(ex, "Serial reader faulted on {PortName}", port.PortName);
                RaiseFault($"Serial reader faulted on {port.PortName}.");
            }
        }
        finally
        {
            _logger.LogInformation(
                "Serial reader terminated on {PortName}. ReadCalls={ReadCalls}, ZeroReads={ZeroReads}, BytesPublished={BytesPublished}, UiNotifications={UiNotifications}, BytesReceived={BytesReceived}",
                port.PortName,
                Interlocked.Read(ref _rxReadCalls),
                Interlocked.Read(ref _rxZeroReads),
                Interlocked.Read(ref _rxBytesPublished),
                Interlocked.Read(ref _rxUiNotifications),
                GetStats().BytesReceived);
        }
    }

    /// <summary>
    /// Returns whether <paramref name="generation"/> is still the active reader generation.
    /// </summary>
    private bool IsCurrentReader(int generation) =>
        Volatile.Read(ref _readerGeneration) == generation;

    /// <summary>
    /// Optionally writes RX to <see cref="_incoming"/> and always raises <see cref="DataTransferred"/>.
    /// </summary>
    /// <param name="chunk">Chunk to publish (owned byte array).</param>
    /// <param name="countTowardIncomingChannel">
    /// True for RX destined for remote/consumers; false for TX UI notification only.
    /// </param>
    private void Publish(SerialDataChunk chunk, bool countTowardIncomingChannel)
    {
        if (countTowardIncomingChannel)
        {
            if (_incoming.Writer.TryWrite(chunk))
            {
                Interlocked.Add(ref _rxBytesPublished, chunk.Data.Length);
            }
            else
            {
                _logger.LogWarning(
                    "Incoming serial channel rejected an RX chunk of {ByteCount} bytes (channel completed or full)",
                    chunk.Data.Length);
            }
        }

        try
        {
            DataTransferred?.Invoke(this, chunk);
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                Interlocked.Add(ref _rxUiNotifications, chunk.Data.Length);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial DataTransferred handler failed");
        }
    }

    /// <summary>
    /// Logs first RX at Information, then Debug every additional 4 KiB (no payload content).
    /// </summary>
    private void MaybeLogRxProgress(string portName)
    {
        var received = GetStats().BytesReceived;
        var previous = Interlocked.Read(ref _lastLoggedRxBytes);

        // First byte: Information. Thereafter: Debug every 4 KiB to avoid payload-adjacent spam.
        if (previous == 0 && received > 0)
        {
            if (Interlocked.CompareExchange(ref _lastLoggedRxBytes, received, 0) == 0)
            {
                _logger.LogInformation(
                    "Serial RX first data on {PortName}: {BytesReceived} bytes received, reader active",
                    portName,
                    received);
            }

            return;
        }

        if (received - previous < 4096)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _lastLoggedRxBytes, received, previous) == previous)
        {
            _logger.LogDebug(
                "Serial RX progress on {PortName}: {BytesReceived} bytes received ({ReadCalls} reads)",
                portName,
                received,
                Interlocked.Read(ref _rxReadCalls));
        }
    }

    /// <summary>
    /// Clears aggregated RX diagnostic counters for a new open.
    /// </summary>
    private void ResetRxDiagnostics()
    {
        Interlocked.Exchange(ref _rxReadCalls, 0);
        Interlocked.Exchange(ref _rxZeroReads, 0);
        Interlocked.Exchange(ref _rxBytesPublished, 0);
        Interlocked.Exchange(ref _rxUiNotifications, 0);
        Interlocked.Exchange(ref _lastLoggedRxBytes, 0);
        _lastReadError = null;
        _lastReadErrorUtc = null;
    }

    /// <summary>
    /// Stores a short last-error summary for diagnostics (no payload bytes).
    /// </summary>
    private void RecordReadError(Exception ex)
    {
        _lastReadError = ex.GetType().Name + ": " + ex.Message;
        _lastReadErrorUtc = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Reads current driver-reported settings from an open or closed port instance.
    /// </summary>
    private static SerialPortEffectiveSettings CaptureEffectiveSettings(ISerialPort port) =>
        new()
        {
            PortName = port.PortName,
            IsOpen = port.IsOpen,
            BaudRate = port.BaudRate,
            DataBits = port.DataBits,
            Parity = port.Parity,
            StopBits = port.StopBits,
            Handshake = port.Handshake,
            DtrEnable = port.DtrEnable,
            RtsEnable = port.RtsEnable,
            ReadTimeout = port.ReadTimeout,
            WriteTimeout = port.WriteTimeout,
            ReadBufferSize = port.ReadBufferSize,
            WriteBufferSize = port.WriteBufferSize
        };

    /// <summary>
    /// Returns whether <paramref name="haystack"/> contains <paramref name="needle"/> as a contiguous sequence.
    /// </summary>
    private static bool ContainsSequence(List<byte> haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Count < needle.Length)
        {
            return false;
        }

        var limit = haystack.Count - needle.Length;
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Apply framing parameters. Modem-control lines are applied separately because
    /// Handshake.RequestToSend owns RTS and forbids setting <see cref="ISerialPort.RtsEnable"/>.
    /// </summary>
    private static void ApplyPortSettings(ISerialPort port, SerialPortConfiguration configuration)
    {
        port.BaudRate = configuration.BaudRate;
        port.DataBits = configuration.DataBits;
        port.Parity = configuration.Parity;
        port.StopBits = configuration.StopBits;
        port.Handshake = configuration.Handshake;
        port.ReadTimeout = SerialPort.InfiniteTimeout;
        port.WriteTimeout = 5000;
        ApplyModemControlLines(port, configuration);
    }

    /// <summary>
    /// Handshake.None does not assert DTR/RTS. Console devices commonly require both asserted
    /// or they will not transmit (RX stays at 0 while the reader blocks on ReadAsync).
    /// </summary>
    private static void ApplyModemControlLines(ISerialPort port, SerialPortConfiguration configuration)
    {
        port.DtrEnable = true;

        if (configuration.Handshake is Handshake.RequestToSend or Handshake.RequestToSendXOnXOff)
        {
            // RTS is managed by the handshake engine; setting RtsEnable would throw.
            return;
        }

        port.RtsEnable = true;
    }

    /// <summary>
    /// Invokes <see cref="Faulted"/>; handler exceptions are logged and swallowed.
    /// </summary>
    private void RaiseFault(string message)
    {
        try
        {
            Faulted?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial Faulted handler failed");
        }
    }

    /// <summary>
    /// Zeros RX/TX counters and last-activity timestamps under <see cref="_statsGate"/>.
    /// </summary>
    private void ResetStats()
    {
        lock (_statsGate)
        {
            _bytesReceived = 0;
            _bytesTransmitted = 0;
            _lastRxUtc = null;
            _lastTxUtc = null;
        }
    }
}
