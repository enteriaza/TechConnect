// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    MainForm.cs
//
// Purpose:
// Primary WinForms UI: serial port selection/session, local console display,
// remote-session status, and bounded shutdown of serial and remote resources.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;
using TechConnect.Client.Services;

namespace TechConnect.Client.Forms;

/// <summary>
/// Technician console form for local serial attach and remote NOC session status.
/// </summary>
/// <remarks>
/// Connect/Disconnect controls the physical serial session only. Remote TCP/mTLS
/// session start runs in the background after serial is open and must not tear down
/// the local console on remote failure. Layout lives in the designer partial.
/// </remarks>
public partial class MainForm : Form
{
    /// <summary>Debounce interval for USB serial device-change refreshes.</summary>
    private const int DeviceChangeDebounceMilliseconds = 500;

    /// <summary>Interval for draining <see cref="SerialConsoleBuffer"/> into the text box.</summary>
    private const int ConsoleFlushMilliseconds = 75;

    /// <summary>Poll interval for TX/RX indicator color updates.</summary>
    private const int ActivityPollMilliseconds = 50;

    /// <summary>How long TX/RX indicators stay lit after traffic.</summary>
    private const int ActivityBlinkMilliseconds = 150;

    /// <summary>Combo-box placeholder when discovery returns no ports.</summary>
    private const string NoPortsPlaceholder = "(no serial ports available)";

    /// <summary>Default status text when idle and a port may be selected.</summary>
    private const string ReadyMessage = "Select a serial port and click Connect.";

    /// <summary>ASCII probe payload written by the loopback diagnostic.</summary>
    private const string LoopbackProbeText = "TECHCONNECTOR-RX-TEST-12345";

    /// <summary>Structured logger for UI and session lifecycle events.</summary>
    private readonly ILogger<MainForm> _logger;

    /// <summary>Enumerates available COM ports for the combo box.</summary>
    private readonly ISerialPortDiscoveryService _serialPortDiscovery;

    /// <summary>Owns the open serial port and local TX/RX statistics.</summary>
    private readonly ISerialConnectionService _serialConnection;

    /// <summary>Owns the remote console session lifecycle and snapshots.</summary>
    private readonly IRemoteSessionService _remoteSession;

    /// <summary>Batches local RX into console text for periodic UI flush.</summary>
    private readonly SerialConsoleBuffer _consoleBuffer = new();

    /// <summary>Debounces device-change port refreshes.</summary>
    private readonly System.Windows.Forms.Timer _deviceChangeDebounceTimer;

    /// <summary>Drains the console buffer onto the UI thread.</summary>
    private readonly System.Windows.Forms.Timer _consoleFlushTimer;

    /// <summary>Refreshes TX/RX indicator colors on a short poll.</summary>
    private readonly System.Windows.Forms.Timer _activityTimer;

    /// <summary>Serializes port-list refresh in-progress / queued flags.</summary>
    private readonly object _refreshGate = new();

    /// <summary>Optional Win32 device-change watcher attached after Load.</summary>
    private SerialDeviceChangeWatcher? _deviceChangeWatcher;

    /// <summary>Aggregate UI connection status for serial/remote presentation.</summary>
    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;

    /// <summary>Last remote-session snapshot applied to status labels.</summary>
    private RemoteSessionSnapshot _latestSnapshot = CreateIdleSnapshot();

    /// <summary>True while a port-list refresh is executing.</summary>
    private bool _refreshInProgress;

    /// <summary>True when another refresh was requested during an in-flight refresh.</summary>
    private bool _refreshQueued;

    /// <summary>True while ConnectFromUiAsync is opening the serial port.</summary>
    private bool _connectOperationInProgress;

    /// <summary>True while DisconnectFromUiAsync or fault-driven disconnect is running.</summary>
    private bool _disconnectOperationInProgress;

    /// <summary>When true, FormClosing allows the form to close after async shutdown.</summary>
    private bool _allowClose;

    /// <summary>When true, close has started and further UI connect work is suppressed.</summary>
    private bool _shutdownStarted;

    /// <summary>True while the technician's serial session is active (port open or opening).</summary>
    private bool _serialSessionActive;

    /// <summary>Last selected COM port name from the combo box.</summary>
    private string? _selectedPortName;

    /// <summary>COM port name for the active serial session, if any.</summary>
    private string? _connectedPortName;

    /// <summary>Cancels an in-flight serial open.</summary>
    private CancellationTokenSource? _connectCts;

    /// <summary>Cancels a background remote-session start.</summary>
    private CancellationTokenSource? _remoteStartCts;

    /// <summary>UTC time until which the TX indicator remains active.</summary>
    private DateTime _txIndicatorUntilUtc;

    /// <summary>UTC time until which the RX indicator remains active.</summary>
    private DateTime _rxIndicatorUntilUtc;

    /// <summary>Cumulative RX bytes accepted into the local console buffer (diagnostics).</summary>
    private long _consoleBytesAccepted;

    /// <summary>
    /// Initializes the form, wires timers and service events, and applies idle UI state.
    /// </summary>
    /// <param name="logger">Logger for this form.</param>
    /// <param name="serialPortDiscovery">Serial port discovery service.</param>
    /// <param name="serialConnection">Local serial connection service.</param>
    /// <param name="remoteSession">Remote session service (TCP/mTLS transport underneath).</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public MainForm(
        ILogger<MainForm> logger,
        ISerialPortDiscoveryService serialPortDiscovery,
        ISerialConnectionService serialConnection,
        IRemoteSessionService remoteSession)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _serialPortDiscovery = serialPortDiscovery ?? throw new ArgumentNullException(nameof(serialPortDiscovery));
        _serialConnection = serialConnection ?? throw new ArgumentNullException(nameof(serialConnection));
        _remoteSession = remoteSession ?? throw new ArgumentNullException(nameof(remoteSession));

        InitializeComponent();

        _deviceChangeDebounceTimer = new System.Windows.Forms.Timer
        {
            Interval = DeviceChangeDebounceMilliseconds
        };
        _deviceChangeDebounceTimer.Tick += DeviceChangeDebounceTimer_Tick;

        _consoleFlushTimer = new System.Windows.Forms.Timer
        {
            Interval = ConsoleFlushMilliseconds
        };
        _consoleFlushTimer.Tick += ConsoleFlushTimer_Tick;

        _activityTimer = new System.Windows.Forms.Timer
        {
            Interval = ActivityPollMilliseconds
        };
        _activityTimer.Tick += ActivityTimer_Tick;

        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        FormClosed += MainForm_FormClosed;
        _serialPortComboBox.SelectedIndexChanged += SerialPortComboBox_SelectedIndexChanged;

        _serialConnection.DataTransferred += SerialConnection_DataTransferred;
        _serialConnection.Faulted += SerialConnection_Faulted;
        _remoteSession.SnapshotChanged += RemoteSession_SnapshotChanged;

        ApplyLinkStatus(selectedPortName: null, errorMessage: ReadyMessage);
        UpdateConnectButtonUi();
        UpdateTrafficUi(force: true);

        _logger.LogInformation("MainForm initialized");
    }

    /// <summary>
    /// Starts device watching, UI timers, and the initial port-list refresh.
    /// </summary>
    private void MainForm_Load(object? sender, EventArgs e)
    {
        _deviceChangeWatcher = new SerialDeviceChangeWatcher(this, _logger);
        _deviceChangeWatcher.SerialDevicesChanged += DeviceChangeWatcher_SerialDevicesChanged;

        _consoleFlushTimer.Start();
        _activityTimer.Start();

        _ = RefreshPortListAsync(RefreshReason.Startup);
    }

    /// <summary>
    /// Cancels the first close so async serial/remote shutdown can finish, then re-closes.
    /// </summary>
    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_shutdownStarted)
        {
            e.Cancel = true;
            return;
        }

        _shutdownStarted = true;
        e.Cancel = true;
        _ = ShutdownThenCloseAsync();
    }

    /// <summary>
    /// Releases serial and remote resources with an 8-second bound, then allows the form to close.
    /// </summary>
    private async Task ShutdownThenCloseAsync()
    {
        try
        {
            _logger.LogInformation("MainForm closing; releasing serial and remote resources");
            // Bound shutdown so a stuck serial/remote cleanup cannot freeze the UI forever.
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await DisconnectCoreAsync(userInitiated: false)
                    .WaitAsync(shutdownCts.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Shutdown timed out after 8s while releasing resources; continuing application close");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while releasing resources during shutdown");
        }
        finally
        {
            _allowClose = true;
            if (!IsDisposed)
            {
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                    {
                        Close();
                    }
                }));
            }
        }
    }

    /// <summary>
    /// Stops timers, unsubscribes service events, and disposes the device-change watcher.
    /// </summary>
    private void MainForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        _deviceChangeDebounceTimer.Stop();
        _deviceChangeDebounceTimer.Tick -= DeviceChangeDebounceTimer_Tick;
        _deviceChangeDebounceTimer.Dispose();

        _consoleFlushTimer.Stop();
        _consoleFlushTimer.Tick -= ConsoleFlushTimer_Tick;
        _consoleFlushTimer.Dispose();

        _activityTimer.Stop();
        _activityTimer.Tick -= ActivityTimer_Tick;
        _activityTimer.Dispose();

        _serialConnection.DataTransferred -= SerialConnection_DataTransferred;
        _serialConnection.Faulted -= SerialConnection_Faulted;
        _remoteSession.SnapshotChanged -= RemoteSession_SnapshotChanged;

        if (_deviceChangeWatcher is not null)
        {
            _deviceChangeWatcher.SerialDevicesChanged -= DeviceChangeWatcher_SerialDevicesChanged;
            _deviceChangeWatcher.Dispose();
            _deviceChangeWatcher = null;
        }
    }

    /// <summary>
    /// Forwards window messages to the serial device-change watcher before base handling.
    /// </summary>
    /// <param name="m">Windows message.</param>
    protected override void WndProc(ref Message m)
    {
        _deviceChangeWatcher?.ProcessMessage(m);
        base.WndProc(ref m);
    }

    /// <summary>
    /// Schedules a debounced port refresh when USB serial devices change.
    /// </summary>
    private void DeviceChangeWatcher_SerialDevicesChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(ScheduleDebouncedDeviceChangeRefresh);
            return;
        }

        ScheduleDebouncedDeviceChangeRefresh();
    }

    /// <summary>
    /// Restarts the device-change debounce timer on the UI thread.
    /// </summary>
    private void ScheduleDebouncedDeviceChangeRefresh()
    {
        _deviceChangeDebounceTimer.Stop();
        _deviceChangeDebounceTimer.Start();
    }

    /// <summary>
    /// Runs a device-change port refresh after the debounce interval elapses.
    /// </summary>
    private void DeviceChangeDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _deviceChangeDebounceTimer.Stop();
        _logger.LogInformation("Refreshing serial ports after USB device change");
        _ = RefreshPortListAsync(RefreshReason.DeviceChange);
    }

    /// <summary>
    /// Handles the Refresh button: manual serial-port rediscovery.
    /// </summary>
    private void RefreshPortsButton_Click(object? sender, EventArgs e)
    {
        _logger.LogInformation("Manual serial port refresh requested");
        _ = RefreshPortListAsync(RefreshReason.Manual);
    }

    /// <summary>
    /// Toggles the physical serial session (connect when idle, disconnect when active).
    /// </summary>
    private async void ConnectButton_Click(object? sender, EventArgs e)
    {
        if (_shutdownStarted || _disconnectOperationInProgress)
        {
            return;
        }

        // Connect/Disconnect controls the physical serial session only.
        if (_serialSessionActive || _connectionStatus is ConnectionStatus.Connecting)
        {
            await DisconnectFromUiAsync().ConfigureAwait(true);
            return;
        }

        if (_connectionStatus is ConnectionStatus.Disconnecting || _connectOperationInProgress)
        {
            return;
        }

        await ConnectFromUiAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the selected serial port, prepares the local console, then starts remote session in the background.
    /// </summary>
    private async Task ConnectFromUiAsync()
    {
        if (_serialSessionActive ||
            _connectionStatus is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting)
        {
            return;
        }

        var selectedPort = GetSelectedValidPortName();
        if (selectedPort is null)
        {
            ApplyLinkStatus(
                selectedPortName: null,
                errorMessage: "Select a valid serial port before connecting.");
            return;
        }

        if (!_serialPortDiscovery.IsPortAvailable(selectedPort))
        {
            ApplyLinkStatus(
                selectedPortName: selectedPort,
                errorMessage: $"Serial port {selectedPort} is no longer available.",
                forceConnectionStatus: ConnectionStatus.Disconnected);
            _ = RefreshPortListAsync(RefreshReason.Manual);
            return;
        }

        var connectCts = new CancellationTokenSource();
        _connectCts = connectCts;
        var token = connectCts.Token;

        _connectOperationInProgress = true;
        _serialSessionActive = true;
        _connectedPortName = selectedPort;
        _selectedPortName = selectedPort;
        SetPortSelectionEnabled(false);
        _connectionStatus = ConnectionStatus.Connecting;
        ApplyLinkStatus(selectedPort, errorMessage: "Opening serial port...", forceConnectionStatus: ConnectionStatus.Connecting);

        try
        {
            _logger.LogInformation("Connect requested for serial port {PortName}", selectedPort);

            if (_serialConnection.IsOpen)
            {
                await _serialConnection.CloseAsync(CancellationToken.None).ConfigureAwait(true);
            }

            await _serialConnection
                .OpenAsync(selectedPort, SerialPortConfiguration.CreateDefault(), token)
                .ConfigureAwait(true);

            token.ThrowIfCancellationRequested();

            if (_shutdownStarted || IsDisposed ||
                _connectionStatus is ConnectionStatus.Disconnecting ||
                _disconnectOperationInProgress)
            {
                return;
            }

            if (_consoleTextBox.Text.StartsWith("[console ready]", StringComparison.Ordinal))
            {
                _consoleBuffer.ClearPending();
                _consoleBuffer.NotifyDisplayCleared();
                _consoleTextBox.Clear();
            }

            // Serial is up — local console is usable immediately. Remote TCP/mTLS starts independently.
            _connectionStatus = ConnectionStatus.ConnectedWaitingForNoc;
            ApplyLinkStatus(
                selectedPort,
                errorMessage: "Serial connected. Local console is ready.");
            UpdateTrafficUi(force: true);
            _logger.LogInformation("Serial port {PortName} connected; local console ready", selectedPort);

            StartRemoteSessionInBackground(selectedPort);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (_shutdownStarted || IsDisposed ||
                _connectionStatus is ConnectionStatus.Disconnecting ||
                _disconnectOperationInProgress)
            {
                return;
            }

            await FailSerialConnectAsync(selectedPort, "Connection cancelled.").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (_shutdownStarted || IsDisposed ||
                _connectionStatus is ConnectionStatus.Disconnecting ||
                _disconnectOperationInProgress)
            {
                return;
            }

            _logger.LogError(ex, "Failed to open serial port {PortName}", selectedPort);
            await FailSerialConnectAsync(selectedPort, FriendlyError("Unable to open serial port.", ex))
                .ConfigureAwait(true);
        }
        finally
        {
            _connectOperationInProgress = false;
            if (ReferenceEquals(_connectCts, connectCts))
            {
                _connectCts = null;
            }

            connectCts.Dispose();
            UpdateConnectButtonUi();
        }
    }

    /// <summary>
    /// Cancels any prior remote-start CTS and fires <see cref="RunRemoteSessionStartAsync"/>.
    /// </summary>
    /// <param name="portName">Serial port associated with the remote session.</param>
    private void StartRemoteSessionInBackground(string portName)
    {
        try
        {
            _remoteStartCts?.Cancel();
            _remoteStartCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        var cts = new CancellationTokenSource();
        _remoteStartCts = cts;
        _ = RunRemoteSessionStartAsync(portName, cts);
    }

    /// <summary>
    /// Starts the remote session without tearing down local serial on remote failure.
    /// </summary>
    /// <param name="portName">Expected open serial port name.</param>
    /// <param name="cts">Cancellation source owned by this background start.</param>
    private async Task RunRemoteSessionStartAsync(string portName, CancellationTokenSource cts)
    {
        try
        {
            if (_remoteSession.State is not RemoteSessionState.Disconnected)
            {
                await _remoteSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            cts.Token.ThrowIfCancellationRequested();

            if (!_serialConnection.IsOpen ||
                !string.Equals(_serialConnection.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await _remoteSession.StartAsync(portName, cts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "Remote session start requested for {PortName}. RemoteState={RemoteState}",
                portName,
                _remoteSession.State);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogDebug("Background remote session start cancelled for {PortName}", portName);
        }
        catch (Exception ex)
        {
            // Remote failures must never tear down the local serial console.
            _logger.LogWarning(ex, "Background remote session start failed for {PortName}", portName);
            if (!IsDisposed && !Disposing && !_shutdownStarted && _serialSessionActive)
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed || Disposing || !_serialSessionActive)
                    {
                        return;
                    }

                    ApplyLinkStatus(
                        _connectedPortName ?? portName,
                        errorMessage: "Serial connected. Remote session could not start.");
                }));
            }
        }
        finally
        {
            if (ReferenceEquals(_remoteStartCts, cts))
            {
                _remoteStartCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Rolls back a failed serial open and restores disconnected UI state.
    /// </summary>
    /// <param name="selectedPort">Port that was being opened, if known.</param>
    /// <param name="message">Operator-facing error text.</param>
    private async Task FailSerialConnectAsync(string? selectedPort, string message)
    {
        _serialSessionActive = false;
        try
        {
            if (_serialConnection.IsOpen || _connectedPortName is not null)
            {
                await _serialConnection.CloseAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        catch (Exception closeEx)
        {
            _logger.LogDebug(closeEx, "Serial close after failed connect");
        }

        _connectedPortName = null;
        _connectionStatus = ConnectionStatus.Disconnected;
        SetPortSelectionEnabled(true);
        ApplyLinkStatus(
            selectedPort ?? _selectedPortName,
            errorMessage: message,
            forceConnectionStatus: ConnectionStatus.Disconnected);
        UpdateTrafficUi(force: true);
    }

    /// <summary>
    /// User-initiated disconnect: closes serial first, then stops the remote session.
    /// </summary>
    private async Task DisconnectFromUiAsync()
    {
        if (_disconnectOperationInProgress || _shutdownStarted)
        {
            return;
        }

        _disconnectOperationInProgress = true;
        _connectionStatus = ConnectionStatus.Disconnecting;
        ApplyLinkStatus(
            _connectedPortName ?? _selectedPortName,
            errorMessage: null,
            forceConnectionStatus: ConnectionStatus.Disconnecting);

        try
        {
            await DisconnectCoreAsync(userInitiated: true).ConfigureAwait(true);

            if (_shutdownStarted || IsDisposed)
            {
                return;
            }

            _connectionStatus = ConnectionStatus.Disconnected;
            ApplyLinkStatus(_selectedPortName, errorMessage: ReadyMessage, forceConnectionStatus: ConnectionStatus.Disconnected);
        }
        catch (Exception ex)
        {
            if (_shutdownStarted || IsDisposed)
            {
                return;
            }

            _logger.LogError(ex, "Disconnect failed");
            _connectionStatus = ConnectionStatus.Disconnected;
            ApplyLinkStatus(
                _selectedPortName,
                errorMessage: FriendlyError("Unable to disconnect cleanly.", ex),
                forceConnectionStatus: ConnectionStatus.Disconnected);
        }
        finally
        {
            _disconnectOperationInProgress = false;
            if (!_shutdownStarted && !IsDisposed)
            {
                SetPortSelectionEnabled(true);
                UpdateConnectButtonUi();
                UpdateTrafficUi(force: true);
            }
        }
    }

    /// <summary>
    /// Shared disconnect path: cancel background work, close serial, then stop remote.
    /// </summary>
    /// <param name="userInitiated"><see langword="true"/> when the technician clicked Disconnect.</param>
    private async Task DisconnectCoreAsync(bool userInitiated)
    {
        var opId = Guid.NewGuid().ToString("N")[..8];
        var started = Environment.TickCount64;
        if (userInitiated)
        {
            _logger.LogInformation(
                "Disconnect begin Op={OperationId} Reason=user Port={PortName}",
                opId,
                _connectedPortName ?? "(none)");
        }
        else
        {
            _logger.LogInformation(
                "Disconnect begin Op={OperationId} Reason=shutdown_or_fault Port={PortName}",
                opId,
                _connectedPortName ?? "(none)");
        }

        CancelBackgroundConnectWork();

        // Close physical serial first — never wait on remote transport cleanup before releasing the COM port.
        _logger.LogInformation(
            "Disconnect Op={OperationId} Stage=closing_serial ElapsedMs={ElapsedMs}",
            opId,
            Environment.TickCount64 - started);
        try
        {
            if (_serialConnection.IsOpen || _connectedPortName is not null)
            {
                await _serialConnection.CloseAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
        finally
        {
            _serialSessionActive = false;
            _connectedPortName = null;
            _connectionStatus = ConnectionStatus.Disconnected;
        }

        // Reflect serial-down immediately; remote stop is bounded and must not freeze the UI forever.
        if (!_shutdownStarted && !IsDisposed)
        {
            _latestSnapshot = CreateIdleSnapshot();
            ApplyLinkStatus(
                _selectedPortName,
                errorMessage: "Serial disconnected. Stopping remote session...",
                forceConnectionStatus: ConnectionStatus.Disconnected);
            UpdateConnectButtonUi();
            UpdateTrafficUi(force: true);
        }

        _logger.LogInformation(
            "Disconnect Op={OperationId} Stage=stopping_remote ElapsedMs={ElapsedMs}",
            opId,
            Environment.TickCount64 - started);
        try
        {
            // Yield away from the UI sync context for remote teardown; marshal is only needed for UI above/below.
            await _remoteSession.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Remote session stop during disconnect reported an error");
        }

        _latestSnapshot = CreateIdleSnapshot();
        _logger.LogInformation(
            "Disconnect complete Op={OperationId} ElapsedMs={ElapsedMs} SerialOpen={SerialOpen} RemoteState={RemoteState}",
            opId,
            Environment.TickCount64 - started,
            _serialConnection.IsOpen,
            _remoteSession.State);
    }

    /// <summary>
    /// Cancels in-flight serial open and background remote-start tokens when present.
    /// </summary>
    private void CancelBackgroundConnectWork()
    {
        try
        {
            _connectCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _remoteStartCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Enqueues RX for the local console and extends TX/RX activity blink windows.
    /// </summary>
    private void SerialConnection_DataTransferred(object? sender, SerialDataChunk chunk)
    {
        // Local console path is independent of remote transport / outbound buffer.
        _consoleBuffer.Enqueue(chunk);
        if (chunk.Direction == SerialDataDirection.Rx)
        {
            var total = Interlocked.Add(ref _consoleBytesAccepted, chunk.Data.Length);
            if (total == chunk.Data.Length)
            {
                _logger.LogDebug(
                    "Console accepted first RX chunk ({ByteCount} bytes); local display path active",
                    chunk.Data.Length);
            }
        }

        var now = DateTime.UtcNow;
        if (chunk.Direction == SerialDataDirection.Tx)
        {
            _txIndicatorUntilUtc = now.AddMilliseconds(ActivityBlinkMilliseconds);
        }
        else
        {
            _rxIndicatorUntilUtc = now.AddMilliseconds(ActivityBlinkMilliseconds);
        }

        if (IsDisposed || Disposing)
        {
            return;
        }

        // Stats/indicators are refreshed on the UI timers; no per-byte Invoke.
    }

    /// <summary>
    /// Marshals a serial fault onto the UI thread for disconnect handling.
    /// </summary>
    private void SerialConnection_Faulted(object? sender, string message)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => _ = HandleSerialFaultAsync(message));
            return;
        }

        _ = HandleSerialFaultAsync(message);
    }

    /// <summary>
    /// Tears down serial/remote after a mid-session serial fault and refreshes the port list.
    /// </summary>
    /// <param name="message">Fault text shown in the status line.</param>
    private async Task HandleSerialFaultAsync(string message)
    {
        if (_connectionStatus is ConnectionStatus.Disconnected or ConnectionStatus.Disconnecting ||
            _disconnectOperationInProgress)
        {
            return;
        }

        _logger.LogWarning("Serial fault while connected to {PortName}: {Message}", _connectedPortName, message);

        _disconnectOperationInProgress = true;
        try
        {
            _connectionStatus = ConnectionStatus.Disconnecting;
            ApplyLinkStatus(_connectedPortName, errorMessage: message, forceConnectionStatus: ConnectionStatus.Disconnecting);
            await DisconnectCoreAsync(userInitiated: false).ConfigureAwait(true);
            _connectionStatus = ConnectionStatus.Disconnected;
            ApplyLinkStatus(_selectedPortName, errorMessage: message, forceConnectionStatus: ConnectionStatus.Disconnected);
            _ = RefreshPortListAsync(RefreshReason.DeviceChange);
        }
        finally
        {
            _disconnectOperationInProgress = false;
            SetPortSelectionEnabled(true);
            UpdateConnectButtonUi();
            UpdateTrafficUi(force: true);
        }
    }

    /// <summary>
    /// Marshals remote-session snapshot updates onto the UI thread.
    /// </summary>
    private void RemoteSession_SnapshotChanged(object? sender, RemoteSessionSnapshot snapshot)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => ApplyRemoteSnapshot(snapshot));
            return;
        }

        ApplyRemoteSnapshot(snapshot);
    }

    /// <summary>
    /// Applies remote status to labels without driving the serial Connect/Disconnect lifecycle.
    /// </summary>
    /// <param name="snapshot">Latest remote-session snapshot.</param>
    private void ApplyRemoteSnapshot(RemoteSessionSnapshot snapshot)
    {
        if (IsDisposed || Disposing || _shutdownStarted || _disconnectOperationInProgress)
        {
            return;
        }

        // Remote state never controls the serial Connect/Disconnect lifecycle.
        if (!_serialSessionActive || !_serialConnection.IsOpen)
        {
            if (_connectionStatus is ConnectionStatus.Disconnected)
            {
                return;
            }
        }

        if (_connectionStatus is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting)
        {
            _latestSnapshot = snapshot;
            return;
        }

        _latestSnapshot = snapshot;

        if (snapshot.RemoteStatus is RemoteSessionState.ConnectedWaitingForNoc
            or RemoteSessionState.NocSessionActive)
        {
            _connectionStatus = snapshot.RemoteStatus == RemoteSessionState.NocSessionActive
                ? ConnectionStatus.ConnectedNocSessionActive
                : ConnectionStatus.ConnectedWaitingForNoc;
        }
        else if (_serialSessionActive && _serialConnection.IsOpen)
        {
            // Keep serial-connected aggregate status while remote is down / not configured / reconnecting.
            _connectionStatus = ConnectionStatus.ConnectedWaitingForNoc;
        }

        var message = snapshot.RemoteStatus switch
        {
            RemoteSessionState.WaitingForRemoteService =>
                "Serial connected. Waiting for the remote console service (automatic retry).",
            RemoteSessionState.Reconnecting =>
                "Serial connected. Reconnecting to the remote console service...",
            RemoteSessionState.ConnectedWaitingForNoc =>
                snapshot.NocStatus switch
                {
                    NocAttachmentState.Attached =>
                        "Serial and remote connected. NOC operator attached (CLI).",
                    _ =>
                        "Serial and remote connected. Waiting for NOC engineer."
                },
            RemoteSessionState.NocSessionActive =>
                "NOC session active.",
            RemoteSessionState.Error =>
                snapshot.LastRemoteError ??
                "Remote service is not configured. Local console remains available.",
            RemoteSessionState.Connecting =>
                "Serial connected. Connecting to the remote console service...",
            _ => _serialSessionActive
                ? "Serial connected. Local console is ready."
                : snapshot.LastRemoteError
        };

        ApplyLinkStatus(_connectedPortName ?? _selectedPortName, message);
        UpdateTrafficUi(force: false);
    }

    /// <summary>
    /// Drains the console buffer into the text box and optionally trims leading text.
    /// </summary>
    private void ConsoleFlushTimer_Tick(object? sender, EventArgs e)
    {
        if (!_consoleBuffer.TryDrain(out var text, out var charactersToTrim))
        {
            return;
        }

        if (charactersToTrim > 0 && _consoleTextBox.TextLength > 0)
        {
            var trimCount = Math.Min(charactersToTrim, _consoleTextBox.TextLength);
            _consoleTextBox.Select(0, trimCount);
            _consoleTextBox.SelectedText = string.Empty;
        }

        var shouldStickToEnd =
            _consoleTextBox.SelectionStart >= Math.Max(0, _consoleTextBox.TextLength - 2);

        _consoleTextBox.AppendText(text);

        if (shouldStickToEnd)
        {
            _consoleTextBox.SelectionStart = _consoleTextBox.TextLength;
            _consoleTextBox.ScrollToCaret();
        }

        UpdateTrafficUi(force: false);
    }

    /// <summary>
    /// Periodic tick that refreshes TX/RX indicators and traffic statistics.
    /// </summary>
    private void ActivityTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTrafficUi(force: false);
    }

    /// <summary>
    /// Updates TX/RX indicator colors and byte-count statistics.
    /// </summary>
    /// <param name="force">When <see langword="true"/>, always rewrite control values.</param>
    private void UpdateTrafficUi(bool force)
    {
        var now = DateTime.UtcNow;
        var txActive = now < _txIndicatorUntilUtc;
        var rxActive = now < _rxIndicatorUntilUtc;

        var txColor = txActive ? Color.LimeGreen : Color.DimGray;
        var rxColor = rxActive ? Color.LimeGreen : Color.DimGray;

        if (force || _txIndicatorLabel.BackColor != txColor)
        {
            _txIndicatorLabel.BackColor = txColor;
        }

        if (force || _rxIndicatorLabel.BackColor != rxColor)
        {
            _rxIndicatorLabel.BackColor = rxColor;
        }

        var stats = _serialConnection.GetStats();
        var delivery = _latestSnapshot.Delivery;
        var statsText =
            $"TX: {stats.BytesTransmitted:N0} bytes  |  RX: {stats.BytesReceived:N0} bytes  |  Remote queued: {delivery.BytesQueued:N0}  dropped: {delivery.BytesDropped:N0}";
        if (force || !string.Equals(_statsLabel.Text, statsText, StringComparison.Ordinal))
        {
            _statsLabel.Text = statsText;
        }
    }

    /// <summary>
    /// Updates selection-dependent status when the technician changes the combo box.
    /// </summary>
    private void SerialPortComboBox_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_refreshInProgress || IsConnectedOrTransitioning())
        {
            return;
        }

        ApplySelectionState(discoveryError: null);
    }

    /// <summary>
    /// Clears pending console text and the on-screen text box.
    /// </summary>
    private void ClearConsoleButton_Click(object? sender, EventArgs e)
    {
        _consoleBuffer.ClearPending();
        _consoleBuffer.NotifyDisplayCleared();
        _consoleTextBox.Clear();
        _logger.LogInformation("Console display cleared by technician");
    }

    /// <summary>
    /// Runs the serial loopback diagnostic after confirmation when a port is open.
    /// </summary>
    private async void LoopbackTestButton_Click(object? sender, EventArgs e)
    {
        if (_shutdownStarted || !_serialConnection.IsOpen)
        {
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "This diagnostic writes a short test sequence to the open COM port.\n\n" +
            "Use ONLY with a physical TX↔RX loopback on the USB adapter " +
            "(adapter TX jumpered to adapter RX, plus ground). " +
            "Disconnect the network device first.\n\n" +
            "Without a loopback jumper, bytes are still transmitted to whatever is connected — " +
            "TX success alone does NOT prove RX.\n\n" +
            "Probe text (also shown in the console as TX): " + LoopbackProbeText + "\n\n" +
            "Continue?",
            "Serial loopback diagnostic",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (confirm != DialogResult.OK)
        {
            return;
        }

        _loopbackTestButton.Enabled = false;
        try
        {
            var probe = System.Text.Encoding.ASCII.GetBytes(LoopbackProbeText);
            var result = await _serialConnection
                .RunLoopbackProbeAsync(probe, TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(true);

            var settings = result.EffectiveSettings?.ToDiagnosticString() ?? "(unavailable)";
            var rx = result.ReceiveDiagnosticsAfter;
            var detail =
                result.Summary + Environment.NewLine +
                $"Effective settings: {settings}" + Environment.NewLine +
                $"ReaderActive={rx?.IsReaderActive}, BytesFromPort={rx?.BytesReceivedFromPort}, " +
                $"DataTransferredBytes={rx?.BytesDeliveredToDataTransferred}, LastRx={rx?.LastRxUtc?.ToString("O") ?? "none"}";

            _logger.LogInformation(
                "Loopback diagnostic UI result: Matched={Matched}, BytesWritten={BytesWritten}, BytesReceivedDuringProbe={BytesReceived}",
                result.ProbeMatched,
                result.BytesWritten,
                result.BytesReceivedDuringProbe);

            ApplyLinkStatus(
                _connectedPortName ?? _selectedPortName,
                errorMessage: detail);

            MessageBox.Show(
                this,
                detail,
                result.ProbeMatched ? "Loopback matched" : "Loopback did not match",
                MessageBoxButtons.OK,
                result.ProbeMatched ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loopback diagnostic failed");
            ApplyLinkStatus(
                _connectedPortName ?? _selectedPortName,
                errorMessage: FriendlyError("Loopback diagnostic failed.", ex));
        }
        finally
        {
            UpdateLoopbackButtonUi();
        }
    }

    /// <summary>
    /// Refreshes the serial-port list, coalescing concurrent requests into a follow-up pass.
    /// </summary>
    /// <param name="reason">Why the refresh was requested.</param>
    private async Task RefreshPortListAsync(RefreshReason reason)
    {
        lock (_refreshGate)
        {
            if (_refreshInProgress)
            {
                _refreshQueued = true;
                _logger.LogDebug("Serial port refresh queued ({Reason})", reason);
                return;
            }

            _refreshInProgress = true;
        }

        try
        {
            do
            {
                _refreshQueued = false;
                await PerformRefreshAsync(reason).ConfigureAwait(true);
                reason = RefreshReason.Queued;
            }
            while (ConsumeQueuedRefresh());
        }
        finally
        {
            lock (_refreshGate)
            {
                _refreshInProgress = false;
            }
        }
    }

    /// <summary>
    /// Consumes a queued refresh request under <see cref="_refreshGate"/>.
    /// </summary>
    /// <returns><see langword="true"/> when another refresh should run immediately.</returns>
    private bool ConsumeQueuedRefresh()
    {
        lock (_refreshGate)
        {
            if (!_refreshQueued)
            {
                return false;
            }

            _refreshQueued = false;
            return true;
        }
    }

    /// <summary>
    /// Discovers ports and rebuilds the combo box, preserving selection when possible.
    /// </summary>
    /// <param name="reason">Refresh trigger for logging.</param>
    private async Task PerformRefreshAsync(RefreshReason reason)
    {
        if (!IsConnectedOrTransitioning())
        {
            _errorLabel.Text = "Refreshing serial ports...";
            _errorLabel.Visible = true;
            _refreshPortsButton.Enabled = false;
        }

        IReadOnlyList<SerialPortInfo> ports;
        try
        {
            ports = await _serialPortDiscovery
                .GetAvailablePortsAsync(CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial port refresh failed ({Reason})", reason);
            if (!IsConnectedOrTransitioning())
            {
                _serialPortComboBox.BeginUpdate();
                try
                {
                    _serialPortComboBox.Items.Clear();
                    _serialPortComboBox.Items.Add(NoPortsPlaceholder);
                    _serialPortComboBox.SelectedIndex = 0;
                    _serialPortComboBox.Enabled = false;
                }
                finally
                {
                    _serialPortComboBox.EndUpdate();
                }

                _selectedPortName = null;
                UpdateConnectButtonUi();
                ApplyLinkStatus(
                    selectedPortName: null,
                    errorMessage: "Unable to refresh serial ports. Use Refresh to try again.",
                    forceConnectionStatus: ConnectionStatus.Disconnected);
            }

            return;
        }

        if (IsDisposed || Disposing)
        {
            return;
        }

        if (_connectedPortName is not null &&
            _serialConnection.IsOpen &&
            !ports.Any(port => string.Equals(port.PortName, _connectedPortName, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogWarning(
                "Connected serial port {PortName} disappeared during refresh ({Reason})",
                _connectedPortName,
                reason);
            _ = HandleSerialFaultAsync($"Serial port {_connectedPortName} was removed.");
            return;
        }

        var previousSelection = _connectedPortName ?? _selectedPortName ?? GetSelectedValidPortName();
        var preserveConnectedUi = IsConnectedOrTransitioning();

        _serialPortComboBox.BeginUpdate();
        try
        {
            _serialPortComboBox.SelectedIndexChanged -= SerialPortComboBox_SelectedIndexChanged;
            _serialPortComboBox.Items.Clear();

            if (ports.Count == 0)
            {
                _serialPortComboBox.Items.Add(NoPortsPlaceholder);
                _serialPortComboBox.SelectedIndex = 0;
            }
            else
            {
                foreach (var port in ports)
                {
                    _serialPortComboBox.Items.Add(port);
                }

                var restoredIndex = FindPortIndex(previousSelection);
                if (restoredIndex >= 0)
                {
                    _serialPortComboBox.SelectedIndex = restoredIndex;
                    _selectedPortName = previousSelection;
                    if (reason is RefreshReason.DeviceChange or RefreshReason.Manual or RefreshReason.Queued)
                    {
                        _logger.LogInformation("Preserved selected serial port {PortName}", previousSelection);
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(previousSelection) && !preserveConnectedUi)
                    {
                        _logger.LogInformation(
                            "Previously selected serial port {PortName} is no longer available",
                            previousSelection);
                    }

                    _serialPortComboBox.SelectedIndex = 0;
                    _selectedPortName = GetSelectedValidPortName();
                }
            }
        }
        finally
        {
            _serialPortComboBox.SelectedIndexChanged += SerialPortComboBox_SelectedIndexChanged;
            _serialPortComboBox.EndUpdate();
        }

        if (!preserveConnectedUi)
        {
            ApplySelectionState(discoveryError: null);
        }
        else
        {
            SetPortSelectionEnabled(false);
            ApplyLinkStatus(_connectedPortName ?? _selectedPortName, errorMessage: _errorLabel.Text);
            UpdateConnectButtonUi();
        }

        if (ports.Count == 0)
        {
            _logger.LogInformation("Serial port list is empty after refresh ({Reason})", reason);
        }
        else
        {
            _logger.LogInformation(
                "Serial port list updated ({Reason}): {Ports}",
                reason,
                string.Join(", ", ports.Select(static p => p.DisplayName)));
        }
    }

    /// <summary>
    /// Applies idle selection status and connect-button enablement from the combo box.
    /// </summary>
    /// <param name="discoveryError">Optional discovery error overriding the ready message.</param>
    private void ApplySelectionState(string? discoveryError)
    {
        var selectedPort = GetSelectedValidPortName();
        _selectedPortName = selectedPort;

        var errorMessage = discoveryError;
        if (errorMessage is null && selectedPort is null)
        {
            errorMessage = !HasRealPorts()
                ? "No serial ports are available. Connect a USB serial adapter or click Refresh."
                : ReadyMessage;
        }
        else if (errorMessage is null)
        {
            errorMessage = ReadyMessage;
        }

        if (!IsConnectedOrTransitioning())
        {
            ApplyLinkStatus(selectedPort, errorMessage, forceConnectionStatus: ConnectionStatus.Disconnected);
            SetPortSelectionEnabled(enabled: true);
        }

        UpdateConnectButtonUi();
    }

    /// <summary>
    /// Enables or disables port selection controls according to session state.
    /// </summary>
    /// <param name="enabled">Desired enablement when ports exist and not mid-transition.</param>
    private void SetPortSelectionEnabled(bool enabled)
    {
        _serialPortComboBox.Enabled = enabled && HasRealPorts();
        _refreshPortsButton.Enabled = enabled &&
            _connectionStatus is not ConnectionStatus.Connecting and not ConnectionStatus.Disconnecting;
    }

    /// <summary>
    /// Returns whether the combo box contains at least one <see cref="SerialPortInfo"/> item.
    /// </summary>
    private bool HasRealPorts() => _serialPortComboBox.Items.OfType<SerialPortInfo>().Any();

    /// <summary>
    /// Finds the combo-box index for a COM port name.
    /// </summary>
    /// <param name="portName">Port name to locate.</param>
    /// <returns>Zero-based index, or -1 when not found.</returns>
    private int FindPortIndex(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return -1;
        }

        for (var i = 0; i < _serialPortComboBox.Items.Count; i++)
        {
            if (_serialPortComboBox.Items[i] is SerialPortInfo info &&
                string.Equals(info.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns the selected COM port name when the selection is a real <see cref="SerialPortInfo"/>.
    /// </summary>
    private string? GetSelectedValidPortName() => GetSelectedPortInfo()?.PortName;

    /// <summary>
    /// Returns the selected <see cref="SerialPortInfo"/>, or <see langword="null"/> for placeholders.
    /// </summary>
    private SerialPortInfo? GetSelectedPortInfo() =>
        _serialPortComboBox.SelectedItem as SerialPortInfo;

    /// <summary>
    /// Resolves a friendly display name for a port from the current combo-box items.
    /// </summary>
    /// <param name="portName">COM port name.</param>
    /// <returns>Display name, the raw port name, or <see langword="null"/> when empty.</returns>
    private string? ResolvePortDisplayName(string? portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return null;
        }

        foreach (var item in _serialPortComboBox.Items)
        {
            if (item is SerialPortInfo info &&
                string.Equals(info.PortName, portName, StringComparison.OrdinalIgnoreCase))
            {
                return info.DisplayName;
            }
        }

        return portName;
    }

    /// <summary>
    /// Updates Connect/Disconnect button text and enablement from physical serial session state.
    /// </summary>
    private void UpdateConnectButtonUi()
    {
        if (_shutdownStarted)
        {
            _connectButton.Enabled = false;
            return;
        }

        if (_connectionStatus is ConnectionStatus.Disconnecting || _disconnectOperationInProgress)
        {
            _connectButton.Text = "Disconnecting...";
            _connectButton.Enabled = false;
            return;
        }

        // Button reflects physical serial session only — never remote TCP/mTLS state.
        if (_serialSessionActive || _connectionStatus is ConnectionStatus.Connecting || _serialConnection.IsOpen)
        {
            _connectButton.Text = "Disconnect";
            _connectButton.Enabled = true;
            return;
        }

        _connectButton.Text = "Connect";
        _connectButton.Enabled = GetSelectedValidPortName() is not null;
        UpdateLoopbackButtonUi();
    }

    /// <summary>
    /// Enables the loopback diagnostic only when a stable open serial session exists.
    /// </summary>
    private void UpdateLoopbackButtonUi()
    {
        _loopbackTestButton.Enabled =
            !_shutdownStarted &&
            !_disconnectOperationInProgress &&
            _serialConnection.IsOpen &&
            _connectionStatus is not ConnectionStatus.Connecting and not ConnectionStatus.Disconnecting;
    }

    /// <summary>
    /// Refreshes serial/remote/NOC status labels, selected-port text, and the error line.
    /// </summary>
    /// <param name="selectedPortName">Port to show as selected/connected.</param>
    /// <param name="errorMessage">Status or error text; hidden when null/whitespace.</param>
    /// <param name="forceConnectionStatus">Optional override for <see cref="_connectionStatus"/>.</param>
    private void ApplyLinkStatus(
        string? selectedPortName,
        string? errorMessage,
        ConnectionStatus? forceConnectionStatus = null)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (forceConnectionStatus is { } forced)
        {
            _connectionStatus = forced;
        }

        var snapshot = _remoteSession.GetSnapshot();
        if (_serialSessionActive ||
            _serialConnection.IsOpen ||
            _connectionStatus is ConnectionStatus.Connecting ||
            snapshot.RemoteStatus is RemoteSessionState.WaitingForRemoteService
                or RemoteSessionState.Reconnecting
                or RemoteSessionState.Connecting
                or RemoteSessionState.Error
                or RemoteSessionState.ConnectedWaitingForNoc
                or RemoteSessionState.NocSessionActive)
        {
            _latestSnapshot = snapshot;
        }
        else if (_connectionStatus is ConnectionStatus.Disconnected)
        {
            _latestSnapshot = CreateIdleSnapshot();
        }

        // Serial label comes from the physical port, not remote snapshot state.
        _serialStatusValueLabel.Text = ToPhysicalSerialText(_connectionStatus);
        _remoteStatusValueLabel.Text = ToRemoteText(_latestSnapshot.RemoteStatus, _connectionStatus);
        _nocStatusValueLabel.Text = ToNocText(_latestSnapshot.NocStatus);

        var displayName = ResolvePortDisplayName(selectedPortName);
        _selectedPortValueLabel.Text = string.IsNullOrWhiteSpace(displayName) ? "(none)" : displayName;
        _errorLabel.Text = errorMessage ?? string.Empty;
        _errorLabel.Visible = !string.IsNullOrWhiteSpace(errorMessage);
        UpdateConnectButtonUi();
    }

    /// <summary>
    /// Returns whether a serial session is open or mid connect/disconnect transition.
    /// </summary>
    private bool IsConnectedOrTransitioning() =>
        _serialSessionActive ||
        _serialConnection.IsOpen ||
        _connectionStatus is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting;

    /// <summary>
    /// Maps connection status to the physical Serial: label text.
    /// </summary>
    /// <param name="connectionStatus">Current aggregate connection status.</param>
    private string ToPhysicalSerialText(ConnectionStatus connectionStatus) =>
        connectionStatus switch
        {
            ConnectionStatus.Connecting => "Connecting",
            ConnectionStatus.Disconnecting => "Disconnecting",
            _ when _serialConnection.IsOpen || _serialSessionActive => "Connected",
            _ => "Disconnected"
        };

    /// <summary>
    /// Creates an idle remote snapshot used when serial/remote are fully down.
    /// </summary>
    private static RemoteSessionSnapshot CreateIdleSnapshot() =>
        new()
        {
            SerialStatus = SerialLinkStatus.Disconnected,
            RemoteStatus = RemoteSessionState.Disconnected,
            NocStatus = NocAttachmentState.None,
            ClientSessionId = null,
            ServerSessionId = null,
            Delivery = new RemoteDeliveryStats(),
            LastRemoteError = null
        };

    /// <summary>
    /// Builds a short operator-facing error string from a common serial exception type.
    /// </summary>
    /// <param name="prefix">Leading context sentence.</param>
    /// <param name="ex">Exception to classify.</param>
    private static string FriendlyError(string prefix, Exception ex)
    {
        var detail = ex switch
        {
            UnauthorizedAccessException => "The port is in use or access was denied.",
            ArgumentException => "The port name or settings are invalid.",
            InvalidOperationException => ex.Message,
            TimeoutException => "The serial operation timed out.",
            IOException => "The serial device reported an I/O error.",
            _ => "An unexpected error occurred."
        };

        return $"{prefix} {detail}";
    }

    /// <summary>
    /// Maps remote-session state to the Remote: label text.
    /// </summary>
    /// <param name="state">Remote session state.</param>
    /// <param name="connectionStatus">Physical connection status for the fully-idle case.</param>
    private static string ToRemoteText(RemoteSessionState state, ConnectionStatus connectionStatus)
    {
        if (connectionStatus is ConnectionStatus.Disconnected && state is RemoteSessionState.Disconnected)
        {
            return "Disconnected";
        }

        return state switch
        {
            RemoteSessionState.Connecting => "Connecting",
            RemoteSessionState.WaitingForRemoteService => "Waiting for service",
            RemoteSessionState.Reconnecting => "Reconnecting",
            RemoteSessionState.ConnectedWaitingForNoc => "Connected",
            RemoteSessionState.NocSessionActive => "Connected",
            RemoteSessionState.Disconnecting => "Disconnecting",
            RemoteSessionState.Error => "Not configured",
            _ => "Disconnected"
        };
    }

    /// <summary>
    /// Maps NOC attachment state to the NOC: label text.
    /// </summary>
    /// <param name="state">NOC attachment state.</param>
    private static string ToNocText(NocAttachmentState state) => state switch
    {
        NocAttachmentState.Waiting => "Waiting",
        NocAttachmentState.Attached => "Attached",
        NocAttachmentState.Active => "Active",
        _ => "Disconnected"
    };

    /// <summary>
    /// Why a serial-port list refresh was requested.
    /// </summary>
    private enum RefreshReason
    {
        /// <summary>Initial refresh after form Load.</summary>
        Startup,

        /// <summary>Technician clicked Refresh.</summary>
        Manual,

        /// <summary>USB/serial device-change notification.</summary>
        DeviceChange,

        /// <summary>Follow-up pass after a refresh was queued during an in-flight refresh.</summary>
        Queued
    }
}
