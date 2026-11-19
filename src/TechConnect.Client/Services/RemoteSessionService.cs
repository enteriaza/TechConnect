// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteSessionService.cs
//
// Purpose:
// Orchestrates remote console session lifecycle: connect/reconnect, serial↔remote
// bridging, control messages, and NOC attachment state — without owning the COM port.
//
// -----------------------------------------------------------------------------

using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Services;

/// <summary>
/// Coordinates a remote console session over an already-open serial port.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership:</b> Does not open/close the serial port. Owns session CTS, background
/// workers (reconnect, RX bridge, TX drain), and <see cref="RemoteOutboundBuffer"/>.
/// Transport hooks are attached for the session lifetime.
/// </para>
/// <para>
/// <b>Delivery semantics:</b> Local serial RX is always drained. While connected, chunks
/// are queued for remote send; while disconnected they are discarded (never replayed).
/// <c>BytesSent</c> means the transport send API accepted the payload — not that a remote
/// NOC client received it. Remote→serial writes use <see cref="ISerialConnectionService.WriteAsync"/>
/// (driver drain semantics of that service).
/// </para>
/// <para>
/// <b>Concurrency:</b> Start/stop/dispose serialize on <c>_lifecycleGate</c>. Session state
/// and snapshots use <c>_stateGate</c>. Workers are generation-gated so late callbacks after
/// stop are ignored. Snapshot UI publishes are rate-limited unless forced.
/// </para>
/// <para>
/// Unconfigured remote endpoint sets Error without starting workers; local serial remains usable.
/// </para>
/// </remarks>
internal sealed class RemoteSessionService : IRemoteSessionService
{
    /// <summary>
    /// Bound for joining session workers after cancel + transport abort.
    /// Long enough for orderly exit; short enough that UI disconnect cannot hang.
    /// </summary>
    internal static readonly TimeSpan BackgroundJoinTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Bound for acquiring the transport disconnect gate and completing abort teardown.</summary>
    internal static readonly TimeSpan TransportDisconnectTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Minimum interval between remote snapshot UI publishes (status/stats), not per-byte.</summary>
    private static readonly TimeSpan SnapshotPublishMinInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Local serial connection (not owned; must already be open on Start).</summary>
    private readonly ISerialConnectionService _serialConnection;

    /// <summary>Remote console transport (connect/send/disconnect).</summary>
    private readonly IRemoteConsoleTransport _transport;

    /// <summary>Backoff/reconnect loop runner for <see cref="MaintainTransportAsync"/>.</summary>
    private readonly IReconnectCoordinator _reconnectCoordinator;

    /// <summary>Remote host/buffer/reconnect options.</summary>
    private readonly IOptions<RemoteServiceOptions> _options;

    /// <summary>Clock for downtime and rate-limited snapshot publishes.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Structured logger.</summary>
    private readonly ILogger<RemoteSessionService> _logger;

    /// <summary>Serializes Start/Stop/Dispose.</summary>
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    /// <summary>Guards session/NOC/error/downtime fields used in snapshots.</summary>
    private readonly object _stateGate = new();

    /// <summary>Current remote session UI state.</summary>
    private RemoteSessionState _state = RemoteSessionState.Disconnected;

    /// <summary>NOC attachment derived from OperatorStatus (or Waiting after connect).</summary>
    private NocAttachmentState _nocState = NocAttachmentState.None;

    /// <summary>Last applied OperatorStatus stateVersion (stale lower versions ignored).</summary>
    private uint _lastOperatorStatusVersion;

    /// <summary>Client-generated session id for this StartAsync lifetime.</summary>
    private string? _clientSessionId;

    /// <summary>Server session id from successful HelloResult, if any.</summary>
    private string? _serverSessionId;

    /// <summary>Last operator-visible remote error string.</summary>
    private string? _lastRemoteError;

    /// <summary>Connect attempts counted in <see cref="MaintainTransportAsync"/>.</summary>
    private long _connectAttempts;

    /// <summary>Accumulated remote downtime excluding the current open downtime interval.</summary>
    private TimeSpan _cumulativeDowntime;

    /// <summary>When set, downtime is accruing from this UTC instant.</summary>
    private DateTimeOffset? _downtimeStartedUtc;

    /// <summary>UTC ticks of last rate-limited snapshot publish.</summary>
    private long _lastSnapshotPublishTicks;

    /// <summary>Linked CTS for the active session; null when idle.</summary>
    private CancellationTokenSource? _sessionCts;

    /// <summary>Background reconnect coordinator task.</summary>
    private Task? _reconnectTask;

    /// <summary>Background serial Incoming → outbound buffer bridge.</summary>
    private Task? _rxBridgeTask;

    /// <summary>Background outbound buffer → transport send drain.</summary>
    private Task? _txDrainTask;

    /// <summary>Owned remote outbound queue for this session.</summary>
    private RemoteOutboundBuffer? _outboundBuffer;

    /// <summary>COM port name required to match the open serial port for this session.</summary>
    private string? _boundPortName;

    /// <summary>Incremented on start/stop/error finalize to invalidate late callbacks.</summary>
    private int _sessionGeneration;

    /// <summary>True while transport event hooks are subscribed.</summary>
    private bool _transportHooksAttached;

    /// <summary>True after <see cref="DisposeAsync"/> begins.</summary>
    private bool _disposed;

    /// <summary>
    /// Creates a disconnected session orchestrator.
    /// </summary>
    public RemoteSessionService(
        ISerialConnectionService serialConnection,
        IRemoteConsoleTransport transport,
        IReconnectCoordinator reconnectCoordinator,
        IOptions<RemoteServiceOptions> options,
        TimeProvider timeProvider,
        ILogger<RemoteSessionService> logger)
    {
        _serialConnection = serialConnection ?? throw new ArgumentNullException(nameof(serialConnection));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reconnectCoordinator = reconnectCoordinator ?? throw new ArgumentNullException(nameof(reconnectCoordinator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public RemoteSessionState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>Client-generated id; distinct from <c>ServerSessionId</c> in snapshots.</remarks>
    public string? SessionId
    {
        get
        {
            lock (_stateGate)
            {
                return _clientSessionId;
            }
        }
    }

    /// <summary>Raised when <see cref="State"/> changes to a different value.</summary>
    public event EventHandler<RemoteSessionState>? StateChanged;

    /// <summary>Raised when a remote session snapshot is published (rate-limited unless forced).</summary>
    public event EventHandler<RemoteSessionSnapshot>? SnapshotChanged;

    /// <summary>
    /// Raised when remote→serial write fails for the current session.
    /// </summary>
    public event EventHandler<string>? Faulted;

    /// <inheritdoc />
    public RemoteSessionSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            return CreateSnapshot_NoLock();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Requires the serial port already open on <paramref name="portName"/>. Unconfigured
    /// remote endpoint transitions to Error without workers. Otherwise starts reconnect,
    /// RX bridge, and TX drain workers under a new session generation.
    /// </remarks>
    public async Task StartAsync(string portName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("A COM port name is required.", nameof(portName));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sessionCts is not null)
            {
                throw new InvalidOperationException("A remote session is already active.");
            }

            if (!_serialConnection.IsOpen ||
                !string.Equals(_serialConnection.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The serial port must be open on the selected COM port before starting the remote session.");
            }

            // Fail fast for expected empty/placeholder endpoint — no connect attempt / retry storm.
            // Leave remote ingress disabled so local serial RX cannot grow the unbounded Incoming channel.
            if (!RemoteEndpoint.IsConfigured(_options.Value))
            {
                _boundPortName = portName;
                _nocState = NocAttachmentState.Waiting;
                _lastOperatorStatusVersion = 0;
                _serialConnection.SetRemoteIngressEnabled(false);
                _ = _serialConnection.DrainPendingRemoteIngress();
                SetState(
                    RemoteSessionState.Error,
                    error: "Remote service is not configured. Local console remains available.");
                _logger.LogInformation(
                    "Remote service is not configured. Local console remains available. Configure RemoteService:Host when the central service is available.");
                return;
            }

            _boundPortName = portName;
            _clientSessionId = Guid.NewGuid().ToString("N");
            _serverSessionId = null;
            _lastRemoteError = null;
            _connectAttempts = 0;
            _cumulativeDowntime = TimeSpan.Zero;
            _downtimeStartedUtc = _timeProvider.GetUtcNow();
            _nocState = NocAttachmentState.Waiting;
            _lastOperatorStatusVersion = 0;
            _outboundBuffer = new RemoteOutboundBuffer(_options.Value.Buffer.MaximumQueuedBytes);
            Interlocked.Increment(ref _sessionGeneration);

            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var sessionToken = _sessionCts.Token;

            AttachTransportHooks();
            SetState(RemoteSessionState.Connecting, error: null);

            _logger.LogInformation(
                "Remote session starting. ClientSessionId={ClientSessionId}, Port={PortName}, Host={Host}, PortNumber={PortNumber}",
                _clientSessionId,
                portName,
                _options.Value.Host,
                _options.Value.Port);

            // Arm Incoming publication only for the lifetime of the bridge consumer (PERF-CLI-03).
            _serialConnection.SetRemoteIngressEnabled(true);
            _rxBridgeTask = Task.Run(() => BridgeSerialRxAsync(sessionToken), CancellationToken.None);
            _txDrainTask = Task.Run(() => DrainOutboundAsync(sessionToken), CancellationToken.None);
            _reconnectTask = Task.Run(
                () => RunReconnectUntilStoppedAsync(sessionToken),
                CancellationToken.None);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Cancels the session, aborts transport (bounded), completes the outbound buffer,
    /// joins workers with wall-clock timeouts, and returns to Disconnected. Serial stays open.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Stops any active session then disposes <c>_lifecycleGate</c>.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    /// <summary>
    /// Shared stop path used by <see cref="StopAsync"/> and <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>Caller must hold <c>_lifecycleGate</c>.</remarks>
    private async Task StopCoreAsync()
    {
        if (_sessionCts is null)
        {
            SetState(RemoteSessionState.Disconnected, error: null);
            return;
        }

        var opId = Guid.NewGuid().ToString("N")[..8];
        var started = Environment.TickCount64;
        var clientSessionId = _clientSessionId;
        _logger.LogInformation(
            "Remote stop begin Op={OperationId} ClientSessionId={ClientSessionId} Stage=disconnecting SerialOpen={SerialOpen}",
            opId,
            clientSessionId,
            _serialConnection.IsOpen);

        SetState(RemoteSessionState.Disconnecting, error: null);

        // Stop enqueueing into Incoming before cancelling the bridge so serial RX cannot race
        // unbounded growth while workers wind down (PERF-CLI-03). Local DataTransferred continues.
        _serialConnection.SetRemoteIngressEnabled(false);

        try
        {
            await _sessionCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        _logger.LogInformation(
            "Remote stop Op={OperationId} Stage=session_cancelled ElapsedMs={ElapsedMs}",
            opId,
            Environment.TickCount64 - started);

        // Abort transport before joining drain so a stalled SendAsync cannot block shutdown.
        await DisconnectTransportBoundedAsync(opId, started).ConfigureAwait(false);

        _outboundBuffer?.Complete();
        _logger.LogInformation(
            "Remote stop Op={OperationId} Stage=awaiting_workers ElapsedMs={ElapsedMs}",
            opId,
            Environment.TickCount64 - started);

        await WaitForBackgroundAsync(_reconnectTask, BackgroundJoinTimeout, "reconnect", opId)
            .ConfigureAwait(false);
        await WaitForBackgroundAsync(_rxBridgeTask, BackgroundJoinTimeout, "rx_bridge", opId)
            .ConfigureAwait(false);
        await WaitForBackgroundAsync(_txDrainTask, BackgroundJoinTimeout, "tx_drain", opId)
            .ConfigureAwait(false);

        // Bridge has stopped — drop any leftover Incoming items so they cannot retain memory.
        _ = _serialConnection.DrainPendingRemoteIngress();

        DetachTransportHooks();

        _sessionCts.Dispose();
        _sessionCts = null;
        _reconnectTask = null;
        _rxBridgeTask = null;
        _txDrainTask = null;
        _outboundBuffer?.Dispose();
        _outboundBuffer = null;
        _boundPortName = null;
        _nocState = NocAttachmentState.None;
        _lastOperatorStatusVersion = 0;
        Interlocked.Increment(ref _sessionGeneration);

        lock (_stateGate)
        {
            _clientSessionId = null;
            _serverSessionId = null;
        }

        SetState(RemoteSessionState.Disconnected, error: null);
        _logger.LogInformation(
            "Remote stop complete Op={OperationId} ClientSessionId={ClientSessionId} ElapsedMs={ElapsedMs} SerialOpen={SerialOpen}",
            opId,
            clientSessionId,
            Environment.TickCount64 - started,
            _serialConnection.IsOpen);
    }

    /// <summary>
    /// Calls transport disconnect with <see cref="TransportDisconnectTimeout"/> wall-clock bound.
    /// </summary>
    /// <param name="opId">Correlation id for stop/error logs.</param>
    /// <param name="startedTick">Environment.TickCount64 at operation start.</param>
    private async Task DisconnectTransportBoundedAsync(string opId, long startedTick)
    {
        _logger.LogInformation(
            "Remote stop Op={OperationId} Stage=aborting_transport ElapsedMs={ElapsedMs}",
            opId,
            Environment.TickCount64 - startedTick);

        try
        {
            using var disconnectCts = new CancellationTokenSource(TransportDisconnectTimeout);
            await _transport.DisconnectAsync(disconnectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Remote stop Op={OperationId} Stage=transport_disconnect_timeout TimeoutSeconds={TimeoutSeconds}",
                opId,
                TransportDisconnectTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Transport disconnect during session stop reported an error");
        }
    }

    /// <summary>
    /// Runs the reconnect coordinator until cancel; finalizes Error sessions without StopAsync.
    /// </summary>
    private async Task RunReconnectUntilStoppedAsync(CancellationToken sessionToken)
    {
        try
        {
            await _reconnectCoordinator.RunAsync(MaintainTransportAsync, sessionToken).ConfigureAwait(false);
        }
        finally
        {
            // Permanent configuration failure ends the reconnect loop without StopAsync.
            // Release workers so Connect can be retried, while preserving Error for the UI.
            if (!sessionToken.IsCancellationRequested && State == RemoteSessionState.Error)
            {
                await FinalizeErrorSessionAsync(waitForReconnect: false).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tears down workers after a permanent configuration Error while keeping Error state and serial open.
    /// </summary>
    /// <param name="waitForReconnect">When true, also joins the reconnect task.</param>
    private async Task FinalizeErrorSessionAsync(bool waitForReconnect)
    {
        var cts = _sessionCts;
        if (cts is null)
        {
            return;
        }

        // Disable ingress before cancelling workers so post-error serial RX cannot fill Incoming.
        _serialConnection.SetRemoteIngressEnabled(false);

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        var opId = "error-" + Guid.NewGuid().ToString("N")[..6];
        var started = Environment.TickCount64;

        await DisconnectTransportBoundedAsync(opId, started).ConfigureAwait(false);
        _outboundBuffer?.Complete();

        if (waitForReconnect)
        {
            await WaitForBackgroundAsync(_reconnectTask, BackgroundJoinTimeout, "reconnect", opId)
                .ConfigureAwait(false);
        }

        await WaitForBackgroundAsync(_rxBridgeTask, BackgroundJoinTimeout, "rx_bridge", opId)
            .ConfigureAwait(false);
        await WaitForBackgroundAsync(_txDrainTask, BackgroundJoinTimeout, "tx_drain", opId)
            .ConfigureAwait(false);

        _ = _serialConnection.DrainPendingRemoteIngress();

        DetachTransportHooks();

        cts.Dispose();
        if (ReferenceEquals(_sessionCts, cts))
        {
            _sessionCts = null;
        }

        _reconnectTask = null;
        _rxBridgeTask = null;
        _txDrainTask = null;
        _outboundBuffer?.Dispose();
        _outboundBuffer = null;
        Interlocked.Increment(ref _sessionGeneration);

        // Keep Error state and bound port; serial remains open for the technician.
        PublishSnapshot();
    }

    /// <summary>
    /// Single connect attempt for the reconnect coordinator: connect, hello, wait until drop.
    /// </summary>
    /// <remarks>
    /// On successful connect: resets backoff, drops any queued outbound bytes (no replay),
    /// clears NOC attachment, sends connector Hello, then polls until transport disconnects.
    /// Configuration errors are permanent; unavailable/other failures are transient for backoff.
    /// </remarks>
    private async Task MaintainTransportAsync(CancellationToken cancellationToken)
    {
        var attempt = Interlocked.Increment(ref _connectAttempts);
        SetState(
            attempt == 1 ? RemoteSessionState.WaitingForRemoteService : RemoteSessionState.Reconnecting,
            error: null);
        MarkDowntime();

        try
        {
            await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteTransportConfigurationException ex)
        {
            // Permanent — reconnect coordinator owns the single configuration-error log.
            SetState(RemoteSessionState.Error, error: ex.Message);
            throw;
        }
        catch (RemoteTransportUnavailableException ex)
        {
            SetState(
                attempt == 1 ? RemoteSessionState.WaitingForRemoteService : RemoteSessionState.Reconnecting,
                error: ex.Message);
            // Transient — reconnect coordinator owns the single connect-failure log.
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Expected socket/TLS connect failures arrive as RemoteTransportException and are
            // logged once by ReconnectCoordinator (no stack trace). Unexpected exceptions are
            // also logged only there so this layer does not duplicate.
            SetState(
                attempt == 1 ? RemoteSessionState.WaitingForRemoteService : RemoteSessionState.Reconnecting,
                error: "Remote connection failed.");
            throw;
        }

        // Real transports reach here when connected. Unavailable transport never does.
        ClearDowntime();
        _reconnectCoordinator.ResetBackoff();
        // New connection always starts with an empty remote outbound queue — never replay outage bytes.
        _outboundBuffer?.DropQueued();
        lock (_stateGate)
        {
            // Reconnect must not invent NOC session resumption.
            _nocState = NocAttachmentState.Waiting;
            _lastOperatorStatusVersion = 0;
            _serverSessionId = null;
            _lastRemoteError = null;
        }

        SetState(RemoteSessionState.ConnectedWaitingForNoc, error: null);
        _logger.LogInformation(
            "Remote transport connected. ClientSessionId={ClientSessionId}. NOC attachment remains Waiting until protocol attaches a session.",
            SessionId);

        await SendConnectorHelloAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _transport.IsConnected)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // Drop queued RX from the prior connection — never replay after reconnect.
                _outboundBuffer?.DropQueued();
                MarkDowntime();
                lock (_stateGate)
                {
                    _nocState = NocAttachmentState.Waiting;
                    _lastOperatorStatusVersion = 0;
                    _serverSessionId = null;
                }

                SetState(RemoteSessionState.Reconnecting, error: "Remote connection lost.");
                _logger.LogInformation(
                    "Remote transport disconnected; outbound queue cleared. ClientSessionId={ClientSessionId}",
                    SessionId);
            }
        }
    }

    /// <summary>
    /// Drains local serial Incoming: enqueue when connected, discard when not (no outage replay).
    /// </summary>
    /// <remarks>
    /// Unexpected bridge failures are logged, raise <see cref="Faulted"/>, move the session to
    /// <see cref="RemoteSessionState.Error"/>, keep draining Incoming (discard) so the unbounded
    /// channel cannot grow, and finalize remote workers without closing the serial port.
    /// Cancellation and normal Incoming completion are not treated as unexpected failures.
    /// </remarks>
    private async Task BridgeSerialRxAsync(CancellationToken cancellationToken)
    {
        var buffer = _outboundBuffer;
        if (buffer is null)
        {
            return;
        }

        var bridgeFaulted = false;

        try
        {
            await foreach (var chunk in _serialConnection.Incoming.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (chunk.Direction != SerialDataDirection.Rx)
                {
                    continue;
                }

                try
                {
                    // Keep draining local Incoming so serial RX never blocks. While remote is down
                    // (or after an unexpected bridge fault), discard for remote delivery — outage
                    // bytes must not survive reconnect / must not accumulate unboundedly.
                    if (!bridgeFaulted && _transport.IsConnected)
                    {
                        buffer.Enqueue(chunk);
                    }
                    else
                    {
                        buffer.Discard(chunk);
                    }

                    PublishSnapshot(force: false);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!bridgeFaulted)
                    {
                        ObserveUnexpectedBridgeFailure(ex, cancellationToken);
                        bridgeFaulted = true;
                    }

                    try
                    {
                        buffer.Discard(chunk);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    PublishSnapshot(force: false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (!bridgeFaulted)
            {
                ObserveUnexpectedBridgeFailure(ex, cancellationToken);
            }

            await DiscardIncomingAfterBridgeFailureAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Observes an unexpected RX bridge failure once, schedules remote-session finalization,
    /// and leaves the serial port open.
    /// </summary>
    private void ObserveUnexpectedBridgeFailure(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogError(
            ex,
            "Serial→remote RX bridge failed. ClientSessionId={ClientSessionId}",
            SessionId);

        const string message = "Serial to remote bridge failed.";
        SetState(RemoteSessionState.Error, message);
        try
        {
            Faulted?.Invoke(this, message);
        }
        catch (Exception handlerEx)
        {
            _logger.LogDebug(handlerEx, "Faulted event handler threw after RX bridge failure");
        }

        PublishSnapshot();

        // Finalize on a separate task so this bridge can exit after cancellation without
        // deadlocking on WaitForBackgroundAsync(_rxBridgeTask).
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await FinalizeErrorSessionAsync(waitForReconnect: true).ConfigureAwait(false);
                }
                catch (Exception finalizeEx)
                {
                    _logger.LogError(
                        finalizeEx,
                        "Failed to finalize session after RX bridge failure. ClientSessionId={ClientSessionId}",
                        SessionId);
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Continues draining Incoming after a channel-level bridge failure so pending RX cannot
    /// accumulate unboundedly until session cancellation.
    /// </summary>
    private async Task DiscardIncomingAfterBridgeFailureAsync(
        RemoteOutboundBuffer buffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var chunk in _serialConnection.Incoming.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (chunk.Direction != SerialDataDirection.Rx)
                {
                    continue;
                }

                try
                {
                    buffer.Discard(chunk);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                PublishSnapshot(force: false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "RX bridge discard loop ended after failure. ClientSessionId={ClientSessionId}",
                SessionId);
        }
    }

    /// <summary>
    /// Dequeues outbound chunks and sends via transport; drops on outage, requeues only for same-connection retry.
    /// </summary>
    /// <remarks>
    /// Successful send calls <see cref="RemoteOutboundBuffer.MarkSent"/> (transport acceptance only).
    /// </remarks>
    private async Task DrainOutboundAsync(CancellationToken cancellationToken)
    {
        var buffer = _outboundBuffer;
        if (buffer is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await buffer.DequeueAsync(cancellationToken).ConfigureAwait(false);
                if (chunk is null)
                {
                    break;
                }

                if (!_transport.IsConnected)
                {
                    // Never requeue across an outage — pending remote-bound bytes are discarded.
                    buffer.DropInFlight(chunk);
                    buffer.DropQueued();
                    PublishSnapshot(force: false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await _transport.SendSerialDataAsync(chunk.Data, cancellationToken).ConfigureAwait(false);
                    buffer.MarkSent(chunk.Data.Length);
                    PublishSnapshot(force: false);
                }
                catch (RemoteTransportUnavailableException)
                {
                    // Failed send on a dying connection — do not retry; clear any remainder.
                    buffer.DropInFlight(chunk);
                    buffer.DropQueued();
                    PublishSnapshot(force: false);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    buffer.Requeue(chunk);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to send serial data to remote transport. ClientSessionId={ClientSessionId}",
                        SessionId);
                    buffer.Requeue(chunk);
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remote outbound drain failed. ClientSessionId={ClientSessionId}", SessionId);
        }
    }

    /// <summary>
    /// Subscribes transport serial/control/fault handlers once per session attach.
    /// </summary>
    private void AttachTransportHooks()
    {
        if (_transportHooksAttached)
        {
            return;
        }

        _transport.SerialDataHandler = HandleRemoteSerialDataAsync;
        _transport.ControlMessageReceived += TransportOnControlMessageReceived;
        _transport.Faulted += TransportOnFaulted;
        _transportHooksAttached = true;
    }

    /// <summary>
    /// Clears transport serial/control/fault handlers.
    /// </summary>
    private void DetachTransportHooks()
    {
        if (!_transportHooksAttached)
        {
            return;
        }

        _transport.SerialDataHandler = null;
        _transport.ControlMessageReceived -= TransportOnControlMessageReceived;
        _transport.Faulted -= TransportOnFaulted;
        _transportHooksAttached = false;
    }

    /// <summary>
    /// Awaited remote→serial path. Owning a defensive copy and awaiting the serial writer
    /// applies TCP backpressure and surfaces write failures to the transport receive loop.
    /// </summary>
    private async Task HandleRemoteSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        var sessionCts = _sessionCts;
        var sessionId = SessionId;
        var boundPort = _boundPortName;

        if (sessionCts is null ||
            sessionCts.IsCancellationRequested ||
            data.IsEmpty ||
            !IsCurrentSession(generation, sessionId, boundPort))
        {
            return;
        }

        if (!_serialConnection.IsOpen ||
            !string.Equals(_serialConnection.PortName, boundPort, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Cannot write remote serial data because the bound serial port is not open.");
        }

        // Own the payload — transport receive buffers must not be referenced across awaits.
        var payload = data.ToArray();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token, cancellationToken);
        try
        {
            await _serialConnection.WriteAsync(payload, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (sessionCts.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentSession(generation, sessionId, boundPort))
            {
                return;
            }

            _logger.LogWarning(
                ex,
                "Failed to write remote TX data to serial port. ClientSessionId={ClientSessionId}",
                sessionId);
            Faulted?.Invoke(this, "Unable to write remote data to the serial port.");
            throw;
        }
    }

    /// <summary>
    /// Sends connector Hello control registration after transport connect.
    /// </summary>
    /// <remarks>
    /// Send failure records <c>LastRemoteError</c> and publishes a snapshot; it does not
    /// by itself tear down the transport (reconnect loop remains responsible).
    /// </remarks>
    private async Task SendConnectorHelloAsync(CancellationToken cancellationToken)
    {
        var sessionId = SessionId;
        var portName = _boundPortName;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        var hello = ControlProtocol.Create(
            ControlProtocol.TypeConnectorHello,
            new ConnectorHelloPayload
            {
                ClientSessionId = sessionId,
                Hostname = Environment.MachineName,
                ApplicationVersion = version,
                PortName = portName,
                Config = SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration)
            });

        try
        {
            await _transport.SendControlMessageAsync(ControlProtocol.Serialize(hello), cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Sent connector Hello. ClientSessionId={ClientSessionId}, Port={PortName}, BaudRate={BaudRate}",
                sessionId,
                portName,
                _serialConnection.CurrentConfiguration.BaudRate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to send connector Hello. ClientSessionId={ClientSessionId}",
                sessionId);
            lock (_stateGate)
            {
                _lastRemoteError = "Failed to send registration Hello.";
            }

            PublishSnapshot(force: true);
        }
    }

    /// <summary>
    /// Dispatches inbound control JSON (binary-protocol shapes and legacy ControlProtocol envelopes).
    /// </summary>
    /// <remarks>
    /// Async void event handler; generation-gated. Unknown/invalid messages are logged and may
    /// emit a control error response when parse fails on a legacy envelope.
    /// </remarks>
    private async void TransportOnControlMessageReceived(object? sender, string json)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        var sessionCts = _sessionCts;
        var sessionId = SessionId;
        var boundPort = _boundPortName;

        try
        {
            if (sessionCts is null ||
                sessionCts.IsCancellationRequested ||
                !IsCurrentSession(generation, sessionId, boundPort))
            {
                return;
            }

            // Binary-protocol control payloads are UTF-8 JSON without the legacy ControlProtocol envelope.
            if (TryHandleOperatorStatusJson(json, generation, sessionId, boundPort))
            {
                return;
            }

            if (TryHandleHelloResultJson(json, generation, sessionId, boundPort))
            {
                return;
            }

            if (TryGetBinarySetBaud(json, out var binarySetBaud) && binarySetBaud is not null)
            {
                await HandleSetBaudRequestAsync(
                        binarySetBaud.CorrelationId,
                        binarySetBaud.BaudRate,
                        binarySetBaud.DataBits,
                        binarySetBaud.Parity,
                        binarySetBaud.StopBits,
                        binarySetBaud.Handshake,
                        generation,
                        sessionId,
                        boundPort,
                        sessionCts.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (!ControlProtocol.TryParse(json, out var message, out var parseError) || message is null)
            {
                _logger.LogWarning(
                    "Ignoring invalid control message. ClientSessionId={ClientSessionId}, Reason={Reason}",
                    sessionId,
                    parseError);
                await SendControlErrorAsync(
                        correlationId: null,
                        code: "invalid_control",
                        parseError ?? "Invalid control message.",
                        sessionCts.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(message.Type, ControlProtocol.TypeSetBaud, StringComparison.Ordinal))
            {
                if (!ControlProtocol.TryGetPayload(message, out SetBaudPayload? payload, out var payloadError) ||
                    payload is null)
                {
                    await SendSetBaudResultAsync(
                            message.CorrelationId,
                            success: false,
                            payloadError ?? "Invalid setBaud payload.",
                            config: null,
                            sessionCts.Token)
                        .ConfigureAwait(false);
                    return;
                }

                await HandleSetBaudRequestAsync(
                        message.CorrelationId,
                        payload.BaudRate,
                        payload.DataBits,
                        payload.Parity,
                        payload.StopBits,
                        payload.Handshake,
                        generation,
                        sessionId,
                        boundPort,
                        sessionCts.Token)
                    .ConfigureAwait(false);
                return;
            }

            if (string.Equals(message.Type, ControlProtocol.TypeSessionPing, StringComparison.Ordinal))
            {
                await HandleSessionPingAsync(message.CorrelationId, sessionCts.Token).ConfigureAwait(false);
                return;
            }

            _logger.LogDebug(
                "Ignoring unsupported control message type {Type}. ClientSessionId={ClientSessionId}",
                message.Type,
                sessionId);
        }
        catch (OperationCanceledException) when (sessionCts?.IsCancellationRequested == true)
        {
        }
        catch (Exception ex)
        {
            if (!IsCurrentSession(generation, sessionId, boundPort))
            {
                return;
            }

            _logger.LogWarning(
                ex,
                "Control message handling failed. ClientSessionId={ClientSessionId}",
                sessionId);
        }
    }

    /// <summary>
    /// Validates and applies a remote setBaud request, then replies with setBaudResult (+ optional config report).
    /// </summary>
    /// <remarks>
    /// Uses <see cref="ISerialConnectionService.ApplyConfigurationAsync"/>; success means the local
    /// driver accepted reopen with the settings — not that a console device negotiated them.
    /// </remarks>
    private async Task HandleSetBaudRequestAsync(
        string? correlationId,
        int baudRate,
        int dataBits,
        string parity,
        string stopBits,
        string handshake,
        int generation,
        string? sessionId,
        string? boundPort,
        CancellationToken cancellationToken)
    {
        if (!_serialConnection.IsOpen)
        {
            await SendSetBaudResultAsync(
                    correlationId,
                    success: false,
                    "Serial port is not open.",
                    config: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!ControlProtocol.IsSupportedBaudRate(baudRate))
        {
            _logger.LogWarning(
                "Rejected unsupported baud rate {BaudRate}. ClientSessionId={ClientSessionId}",
                baudRate,
                sessionId);
            await SendSetBaudResultAsync(
                    correlationId,
                    success: false,
                    $"Baud rate {baudRate} is not supported.",
                    SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!SerialControlMapping.TryFromSetBaud(
                baudRate,
                dataBits,
                parity,
                stopBits,
                handshake,
                out var requested,
                out var parseError))
        {
            await SendSetBaudResultAsync(
                    correlationId,
                    success: false,
                    parseError ?? "Invalid serial configuration.",
                    SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration),
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            // ApplyConfigurationAsync serializes against RX/TX via the serial service gate.
            await _serialConnection.ApplyConfigurationAsync(requested, cancellationToken).ConfigureAwait(false);

            if (!IsCurrentSession(generation, sessionId, boundPort))
            {
                return;
            }

            var effective = SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration);
            _logger.LogInformation(
                "Applied remote serial configuration. ClientSessionId={ClientSessionId}, BaudRate={BaudRate}, DataBits={DataBits}, Parity={Parity}, StopBits={StopBits}",
                sessionId,
                effective.BaudRate,
                effective.DataBits,
                effective.Parity,
                effective.StopBits);

            await SendSetBaudResultAsync(
                    correlationId,
                    success: true,
                    error: null,
                    effective,
                    cancellationToken)
                .ConfigureAwait(false);

            await SendSerialConfigurationReportAsync(correlationId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsCurrentSession(generation, sessionId, boundPort))
            {
                return;
            }

            _logger.LogWarning(
                ex,
                "Serial configuration change failed. ClientSessionId={ClientSessionId}, BaudRate={BaudRate}",
                sessionId,
                baudRate);

            await SendSetBaudResultAsync(
                    correlationId,
                    success: false,
                    "Serial configuration change failed.",
                    SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends a SerialConfig control message reflecting <see cref="ISerialConnectionService.CurrentConfiguration"/>.
    /// </summary>
    private Task SendSerialConfigurationReportAsync(string? correlationId, CancellationToken cancellationToken)
    {
        var effective = SerialControlMapping.ToPayload(_serialConnection.CurrentConfiguration);
        var configReport = ControlProtocol.Create(
            ControlProtocol.TypeSerialConfig,
            effective,
            correlationId);
        return _transport.SendControlMessageAsync(ControlProtocol.Serialize(configReport), cancellationToken);
    }

    /// <summary>
    /// Sends a setBaudResult control message for <paramref name="correlationId"/>.
    /// </summary>
    private Task SendSetBaudResultAsync(
        string? correlationId,
        bool success,
        string? error,
        SerialConfigPayload? config,
        CancellationToken cancellationToken)
    {
        var message = ControlProtocol.Create(
            ControlProtocol.TypeSetBaudResult,
            new SetBaudResultPayload
            {
                Success = success,
                Error = error,
                Config = config
            },
            correlationId);
        return _transport.SendControlMessageAsync(ControlProtocol.Serialize(message), cancellationToken);
    }

    /// <summary>
    /// Answers sessionPing with hostname only — must not touch COM / serial I/O.
    /// </summary>
    private Task HandleSessionPingAsync(string? correlationId, CancellationToken cancellationToken)
    {
        // Application-level health check only — must not touch COM / serial I/O.
        var hostname = Environment.MachineName;
        var message = ControlProtocol.Create(
            ControlProtocol.TypeSessionPingResult,
            new SessionPingResultPayload
            {
                Success = true,
                Hostname = hostname
            },
            correlationId);
        return _transport.SendControlMessageAsync(ControlProtocol.Serialize(message), cancellationToken);
    }

    /// <summary>
    /// Sends a typed control Error payload to the remote peer.
    /// </summary>
    private Task SendControlErrorAsync(
        string? correlationId,
        string code,
        string messageText,
        CancellationToken cancellationToken)
    {
        var message = ControlProtocol.Create(
            ControlProtocol.TypeError,
            new ErrorPayload { Code = code, Message = messageText },
            correlationId);
        return _transport.SendControlMessageAsync(ControlProtocol.Serialize(message), cancellationToken);
    }

    /// <summary>
    /// Records a transport fault string into snapshot state for the current session generation.
    /// </summary>
    /// <remarks>Does not itself cancel the session; reconnect observes transport disconnect separately.</remarks>
    private void TransportOnFaulted(object? sender, string message)
    {
        var generation = Volatile.Read(ref _sessionGeneration);
        var sessionId = SessionId;
        if (!IsCurrentSession(generation, sessionId, _boundPortName))
        {
            return;
        }

        _lastRemoteError = message;
        _logger.LogInformation(
            "Remote transport faulted: {Message}. ClientSessionId={ClientSessionId}",
            message,
            sessionId);
        PublishSnapshot();
    }

    /// <summary>
    /// Returns whether captured generation/session/port still match the active session.
    /// </summary>
    private bool IsCurrentSession(int generation, string? sessionId, string? boundPort)
    {
        return Volatile.Read(ref _sessionGeneration) == generation &&
               _sessionCts is not null &&
               string.Equals(SessionId, sessionId, StringComparison.Ordinal) &&
               string.Equals(_boundPortName, boundPort, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Updates remote state under <c>_stateGate</c>, optionally storing <paramref name="error"/>.
    /// </summary>
    private void SetState(RemoteSessionState state, string? error)
    {
        lock (_stateGate)
        {
            if (error is not null)
            {
                _lastRemoteError = error;
            }

            SetState_NoLock(state, publish: true);
        }
    }

    /// <summary>
    /// Applies state without taking <c>_stateGate</c>; raises <see cref="StateChanged"/> on change.
    /// </summary>
    /// <param name="state">New remote session state.</param>
    /// <param name="publish">When true, publishes a snapshot even if state is unchanged.</param>
    private void SetState_NoLock(RemoteSessionState state, bool publish)
    {
        if (_state == state && !publish)
        {
            return;
        }

        var previous = _state;
        _state = state;
        if (previous != state)
        {
            _logger.LogInformation(
                "Remote session state {Previous} -> {Current}. ClientSessionId={ClientSessionId}",
                previous,
                state,
                _clientSessionId);
            StateChanged?.Invoke(this, state);
        }

        if (publish)
        {
            PublishSnapshot_NoLock();
        }
    }

    /// <summary>
    /// Publishes a snapshot under <c>_stateGate</c>.
    /// </summary>
    /// <param name="force">When false, applies <see cref="SnapshotPublishMinInterval"/> rate limiting.</param>
    private void PublishSnapshot(bool force = true)
    {
        lock (_stateGate)
        {
            PublishSnapshot_NoLock(force);
        }
    }

    /// <summary>
    /// Raises <see cref="SnapshotChanged"/> unless rate-limited (when <paramref name="force"/> is false).
    /// </summary>
    private void PublishSnapshot_NoLock(bool force = true)
    {
        if (!force)
        {
            var now = _timeProvider.GetUtcNow().UtcTicks;
            var last = Interlocked.Read(ref _lastSnapshotPublishTicks);
            if (now - last < SnapshotPublishMinInterval.Ticks)
            {
                return;
            }

            Interlocked.Exchange(ref _lastSnapshotPublishTicks, now);
        }
        else
        {
            Interlocked.Exchange(ref _lastSnapshotPublishTicks, _timeProvider.GetUtcNow().UtcTicks);
        }

        SnapshotChanged?.Invoke(this, CreateSnapshot_NoLock());
    }

    /// <summary>
    /// Builds the current UI snapshot; caller must hold <c>_stateGate</c>.
    /// </summary>
    private RemoteSessionSnapshot CreateSnapshot_NoLock()
    {
        var delivery = _outboundBuffer?.GetStats(
                            Volatile.Read(ref _connectAttempts),
                            GetDowntime_NoLock())
                        ?? new RemoteDeliveryStats();

        return new RemoteSessionSnapshot
        {
            SerialStatus = ResolveSerialStatus(),
            RemoteStatus = _state,
            NocStatus = _nocState,
            ClientSessionId = _clientSessionId,
            ServerSessionId = _serverSessionId,
            Delivery = delivery,
            LastRemoteError = _lastRemoteError
        };
    }

    /// <summary>
    /// Attempts to parse/apply binary-protocol OperatorStatus JSON (versioned NOC attachment).
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the payload was recognized as OperatorStatus (even if ignored as stale).
    /// </returns>
    private bool TryHandleOperatorStatusJson(
        string json,
        int generation,
        string? sessionId,
        string? boundPort)
    {
        if (!LooksLikeOperatorStatusJson(json))
        {
            return false;
        }

        OperatorStatusMessage status;
        try
        {
            status = ControlMessageSerializer.DeserializeOperatorStatus(Encoding.UTF8.GetBytes(json));
        }
        catch (ProtocolInvalidPayloadException ex)
        {
            _logger.LogWarning(
                "Ignoring invalid OperatorStatus. ClientSessionId={ClientSessionId}, Reason={Reason}",
                sessionId,
                ex.Message);
            return true;
        }

        if (!IsCurrentSession(generation, sessionId, boundPort))
        {
            return true;
        }

        lock (_stateGate)
        {
            if (!IsCurrentSession(generation, sessionId, boundPort))
            {
                return true;
            }

            var currentVersion = _lastOperatorStatusVersion;
            if (status.StateVersion < currentVersion)
            {
                _logger.LogDebug(
                    "Ignoring stale OperatorStatus v{Incoming} (current v{Current}). ClientSessionId={ClientSessionId}",
                    status.StateVersion,
                    currentVersion,
                    sessionId);
                return true;
            }

            if (status.StateVersion == currentVersion)
            {
                var expected = MapOperatorStatus(status.Attached, status.TerminalActive);
                if (expected == _nocState)
                {
                    return true;
                }

                _logger.LogWarning(
                    "OperatorStatus version conflict v{Version}: incoming Attached={Attached} TerminalActive={TerminalActive} conflicts with local {Local}. ClientSessionId={ClientSessionId}",
                    status.StateVersion,
                    status.Attached,
                    status.TerminalActive,
                    _nocState,
                    sessionId);
                return true;
            }

            // Higher version — apply.
            _lastOperatorStatusVersion = status.StateVersion;
            _nocState = MapOperatorStatus(status.Attached, status.TerminalActive);
            var remoteState = _nocState == NocAttachmentState.Active
                ? RemoteSessionState.NocSessionActive
                : RemoteSessionState.ConnectedWaitingForNoc;
            SetState_NoLock(remoteState, publish: true);
            _logger.LogInformation(
                "OperatorStatus applied v{Version} Noc={Noc}. ClientSessionId={ClientSessionId}",
                status.StateVersion,
                _nocState,
                sessionId);
        }

        return true;
    }

    /// <summary>
    /// Maps OperatorStatus attached/terminalActive flags to <see cref="NocAttachmentState"/>.
    /// </summary>
    private static NocAttachmentState MapOperatorStatus(bool attached, bool terminalActive)
    {
        if (!attached)
        {
            return NocAttachmentState.Waiting;
        }

        return terminalActive ? NocAttachmentState.Active : NocAttachmentState.Attached;
    }

    /// <summary>
    /// Heuristic: object with stateVersion/attached/terminalActive and no legacy <c>type</c> property.
    /// </summary>
    private static bool LooksLikeOperatorStatusJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("type", out _))
            {
                return false;
            }

            return root.TryGetProperty("stateVersion", out _) &&
                   root.TryGetProperty("attached", out _) &&
                   root.TryGetProperty("terminalActive", out _);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to parse/apply binary-protocol HelloResult registration outcome.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when recognized as HelloResult (success or failure), else false.
    /// </returns>
    /// <remarks>
    /// Failed registration keeps serial open and leaves remote in ConnectedWaitingForNoc with LastRemoteError.
    /// </remarks>
    private bool TryHandleHelloResultJson(
        string json,
        int generation,
        string? sessionId,
        string? boundPort)
    {
        // Discriminate HelloResult from other binary-protocol control JSON (SetBaudResult, Error, etc.)
        // that shares the same ControlMessageReceived UTF-8 surface.
        if (!LooksLikeHelloResultJson(json))
        {
            return false;
        }

        HelloResultMessage result;
        try
        {
            result = ControlMessageSerializer.DeserializeHelloResult(Encoding.UTF8.GetBytes(json));
        }
        catch (ProtocolInvalidPayloadException)
        {
            return false;
        }

        if (!IsCurrentSession(generation, sessionId, boundPort))
        {
            return true;
        }

        if (result.Success)
        {
            lock (_stateGate)
            {
                _serverSessionId = result.SessionId;
                _lastRemoteError = null;
            }

            _logger.LogInformation(
                "Connector registration succeeded. ClientSessionId={ClientSessionId}, ServerSessionId={ServerSessionId}",
                sessionId,
                result.SessionId);
            PublishSnapshot(force: true);

            // Report authoritative serial configuration after successful registration.
            _ = SendSerialConfigurationReportSafeAsync();
            return true;
        }

        lock (_stateGate)
        {
            _serverSessionId = null;
            _lastRemoteError = result.Error ?? "Connector registration failed.";
        }

        _logger.LogWarning(
            "Connector registration failed. ClientSessionId={ClientSessionId}, Reason={Reason}",
            sessionId,
            result.Error);
        PublishSnapshot(force: true);
        // Serial remains open; remote status stays ConnectedWaitingForNoc with LastRemoteError set.
        return true;
    }

    /// <summary>
    /// Heuristic discriminator for HelloResult vs other binary control JSON on the same UTF-8 surface.
    /// </summary>
    private static bool LooksLikeHelloResultJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            // Legacy ControlProtocol envelopes carry "type".
            if (root.TryGetProperty("type", out _))
            {
                return false;
            }

            if (!root.TryGetProperty("success", out _))
            {
                return false;
            }

            // SetBaudResult / Error shapes that must not be treated as HelloResult.
            if (root.TryGetProperty("config", out _) ||
                root.TryGetProperty("baudRate", out _) ||
                root.TryGetProperty("code", out _))
            {
                return false;
            }

            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to deserialize a binary-protocol SetBaud message (baudRate without success/config/type).
    /// </summary>
    private static bool TryGetBinarySetBaud(string json, out SetBaudMessage? message)
    {
        message = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("type", out _))
            {
                return false;
            }

            if (!root.TryGetProperty("baudRate", out _))
            {
                return false;
            }

            // SetBaudResult also has baudRate inside config; require no "success" property.
            if (root.TryGetProperty("success", out _) || root.TryGetProperty("config", out _))
            {
                return false;
            }

            message = ControlMessageSerializer.DeserializeSetBaud(Encoding.UTF8.GetBytes(json));
            return true;
        }
        catch (ProtocolInvalidPayloadException)
        {
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort SerialConfig report after Hello success; swallows non-cancel failures.
    /// </summary>
    private async Task SendSerialConfigurationReportSafeAsync()
    {
        try
        {
            var cts = _sessionCts;
            if (cts is null || cts.IsCancellationRequested || !_transport.IsConnected)
            {
                return;
            }

            await SendSerialConfigurationReportAsync(correlationId: null, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Failed to send SerialConfiguration after registration");
        }
    }

    /// <summary>
    /// Maps local serial open state into snapshot serial status (independent of remote).
    /// </summary>
    private SerialLinkStatus ResolveSerialStatus()
    {
        if (_serialConnection.IsOpen)
        {
            return SerialLinkStatus.Connected;
        }

        return SerialLinkStatus.Disconnected;
    }

    /// <summary>
    /// Starts accruing remote downtime if not already open.
    /// </summary>
    private void MarkDowntime()
    {
        lock (_stateGate)
        {
            _downtimeStartedUtc ??= _timeProvider.GetUtcNow();
        }
    }

    /// <summary>
    /// Closes an open downtime interval into <see cref="_cumulativeDowntime"/>.
    /// </summary>
    private void ClearDowntime()
    {
        lock (_stateGate)
        {
            if (_downtimeStartedUtc is { } started)
            {
                _cumulativeDowntime += _timeProvider.GetUtcNow() - started;
                _downtimeStartedUtc = null;
            }
        }
    }

    /// <summary>
    /// Total remote downtime including any in-progress interval; caller holds <c>_stateGate</c>.
    /// </summary>
    private TimeSpan GetDowntime_NoLock()
    {
        var total = _cumulativeDowntime;
        if (_downtimeStartedUtc is { } started)
        {
            total += _timeProvider.GetUtcNow() - started;
        }

        return total;
    }

    /// <summary>
    /// Joins a background worker with a wall-clock timeout (independent of <see cref="TimeProvider"/>).
    /// </summary>
    /// <remarks>
    /// On timeout, observes the task to avoid unobserved exceptions and returns without canceling it.
    /// </remarks>
    private async Task WaitForBackgroundAsync(
        Task? task,
        TimeSpan timeout,
        string workerName,
        string operationId)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            // Wall-clock timeout: must not depend on FakeTimeProvider advancing during shutdown.
            var finished = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, task))
            {
                _logger.LogWarning(
                    "Remote stop Op={OperationId} Stage=worker_join_timeout Worker={Worker} TimeoutSeconds={TimeoutSeconds}",
                    operationId,
                    workerName,
                    timeout.TotalSeconds);
                ObserveBackgroundTask(task, workerName);
                return;
            }

            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Already logged by the worker / coordinator.
        }
    }

    /// <summary>
    /// Attaches a continuation that observes faulted exceptions after a join timeout.
    /// </summary>
    private static void ObserveBackgroundTask(Task task, string name)
    {
        _ = task.ContinueWith(
            static (t, _) =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    _ = t.Exception.Flatten();
                }
            },
            name,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
