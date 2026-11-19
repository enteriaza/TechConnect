// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    FakeUiServices.cs
//
// Purpose:
// In-memory discovery and remote-session fakes plus reflection helpers for
// MainForm lifecycle and connection-state UI tests.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Controllable <see cref="ISerialPortDiscoveryService"/> returning a fixed port list.
/// </summary>
/// <remarks>
/// Limitation: does not simulate refresh races or OS enumeration failures beyond <see cref="ThrowOnGet"/>.
/// </remarks>
internal sealed class FakeSerialPortDiscoveryService : ISerialPortDiscoveryService
{
    /// <summary>
    /// Configured available ports.
    /// </summary>
    private readonly List<SerialPortInfo> _ports = new();

    /// <summary>
    /// When set, <see cref="GetAvailablePorts"/> throws this exception.
    /// </summary>
    public Exception? ThrowOnGet { get; set; }

    /// <summary>
    /// Replaces the available-port list with the given infos.
    /// </summary>
    public void SetPorts(params SerialPortInfo[] ports)
    {
        _ports.Clear();
        _ports.AddRange(ports);
    }

    /// <summary>
    /// Replaces the available-port list with names that have null friendly names.
    /// </summary>
    public void SetPorts(params string[] portNames)
    {
        _ports.Clear();
        foreach (var name in portNames)
        {
            _ports.Add(new SerialPortInfo { PortName = name, FriendlyName = null });
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> GetAvailablePorts()
    {
        if (ThrowOnGet is not null)
        {
            throw ThrowOnGet;
        }

        return _ports.ToArray();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SerialPortInfo>> GetAvailablePortsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GetAvailablePorts());
    }

    /// <inheritdoc />
    public bool IsPortAvailable(string portName) =>
        _ports.Any(p => string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Controllable <see cref="IRemoteSessionService"/> that can hold start/stop, simulate
/// configuration or transport errors, and raise snapshot/fault events for UI tests.
/// </summary>
/// <remarks>
/// Limitation: does not drive real serial or TCP/mTLS; NOC attachment stays at Waiting unless
/// a snapshot is raised explicitly; Delivery stats are always empty.
/// </remarks>
internal sealed class FakeRemoteSessionService : IRemoteSessionService
{
    /// <summary>
    /// Current remote session state.
    /// </summary>
    private RemoteSessionState _state = RemoteSessionState.Disconnected;
    /// <summary>
    /// Current client session id, if any.
    /// </summary>
    private string? _sessionId;
    /// <summary>
    /// Port name from the most recent StartAsync.
    /// </summary>
    private string? _lastPortName;
    /// <summary>
    /// Last remote error string for snapshots.
    /// </summary>
    private string? _lastRemoteError;
    /// <summary>
    /// Serial link status exposed on snapshots.
    /// </summary>
    private SerialLinkStatus _serialStatus = SerialLinkStatus.Disconnected;
    /// <summary>
    /// NOC attachment status exposed on snapshots.
    /// </summary>
    private NocAttachmentState _nocStatus = NocAttachmentState.None;
    /// <summary>
    /// Optional hold for StartAsync.
    /// </summary>
    private TaskCompletionSource? _holdStart;
    /// <summary>
    /// Optional hold for StopAsync.
    /// </summary>
    private TaskCompletionSource? _holdStop;
    /// <summary>
    /// Incremented on Stop to supersede in-flight starts.
    /// </summary>
    private int _lifecycleGeneration;

    /// <summary>
    /// Number of times <see cref="StartAsync"/> was entered.
    /// </summary>
    public int StartCount { get; private set; }

    /// <summary>
    /// Number of times <see cref="StopAsync"/> was entered.
    /// </summary>
    public int StopCount { get; private set; }

    /// <summary>When true, StartAsync ends in Error (remote host not configured).</summary>
    public bool StartAsConfigurationError { get; set; }

    /// <summary>When true, StartAsync ends in Error simulating a remote transport failure.</summary>
    public bool StartAsRemoteFailure { get; set; }

    /// <summary>
    /// Message stored as last remote error when <see cref="StartAsRemoteFailure"/> is true.
    /// </summary>
    public string RemoteFailureMessage { get; set; } = "Remote connection failed.";

    /// <summary>
    /// Port name passed to the most recent <see cref="StartAsync"/>.
    /// </summary>
    public string? LastStartedPortName => _lastPortName;

    /// <summary>
    /// Last remote error string exposed on snapshots.
    /// </summary>
    public string? LastRemoteError
    {
        get => _lastRemoteError;
        set => _lastRemoteError = value;
    }

    /// <inheritdoc />
    public RemoteSessionState State
    {
        get => _state;
        set => _state = value;
    }

    /// <inheritdoc />
    public string? SessionId
    {
        get => _sessionId;
        set => _sessionId = value;
    }

    /// <inheritdoc />
    public event EventHandler<RemoteSessionState>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<RemoteSessionSnapshot>? SnapshotChanged;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <summary>
    /// Causes the next <see cref="StartAsync"/> to wait until <see cref="ReleaseHeldStart"/>.
    /// </summary>
    public void HoldNextStart() =>
        _holdStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes a held start wait started by <see cref="HoldNextStart"/>.
    /// </summary>
    public void ReleaseHeldStart() => _holdStart?.TrySetResult();

    /// <summary>
    /// Causes the next <see cref="StopAsync"/> to wait until <see cref="ReleaseHeldStop"/>.
    /// </summary>
    public void HoldNextStop() =>
        _holdStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes a held stop wait started by <see cref="HoldNextStop"/>.
    /// </summary>
    public void ReleaseHeldStop() => _holdStop?.TrySetResult();

    /// <inheritdoc />
    public RemoteSessionSnapshot GetSnapshot() =>
        new()
        {
            SerialStatus = _serialStatus,
            RemoteStatus = _state,
            NocStatus = _nocStatus,
            ClientSessionId = _sessionId,
            ServerSessionId = null,
            Delivery = new RemoteDeliveryStats(),
            LastRemoteError = _lastRemoteError
        };

    /// <inheritdoc />
    public async Task StartAsync(string portName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCount++;
        _lastPortName = portName;
        var generation = Volatile.Read(ref _lifecycleGeneration);

        var hold = _holdStart;
        if (hold is not null)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(_holdStart, hold))
            {
                _holdStart = null;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _lifecycleGeneration))
        {
            // Stop/shutdown superseded this start attempt.
            return;
        }

        _serialStatus = SerialLinkStatus.Connected;
        _nocStatus = NocAttachmentState.Waiting;

        if (StartAsConfigurationError)
        {
            _sessionId = null;
            _lastRemoteError = "Remote service is not configured. Local console remains available.";
            _state = RemoteSessionState.Error;
        }
        else if (StartAsRemoteFailure)
        {
            _sessionId = null;
            _lastRemoteError = RemoteFailureMessage;
            _state = RemoteSessionState.Error;
        }
        else
        {
            _sessionId = Guid.NewGuid().ToString("N");
            _lastRemoteError = null;
            _state = RemoteSessionState.ConnectedWaitingForNoc;
        }

        StateChanged?.Invoke(this, _state);
        SnapshotChanged?.Invoke(this, GetSnapshot());
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StopCount++;
        Interlocked.Increment(ref _lifecycleGeneration);
        ReleaseHeldStart();

        var hold = _holdStop;
        if (hold is not null)
        {
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (ReferenceEquals(_holdStop, hold))
            {
                _holdStop = null;
            }
        }

        _sessionId = null;
        _lastRemoteError = null;
        _serialStatus = SerialLinkStatus.Disconnected;
        _nocStatus = NocAttachmentState.None;
        _state = RemoteSessionState.Disconnected;
        StateChanged?.Invoke(this, _state);
        SnapshotChanged?.Invoke(this, GetSnapshot());
    }

    /// <summary>
    /// Applies snapshot fields and raises <see cref="SnapshotChanged"/>.
    /// </summary>
    public void RaiseSnapshot(RemoteSessionSnapshot snapshot)
    {
        _state = snapshot.RemoteStatus;
        _serialStatus = snapshot.SerialStatus;
        _nocStatus = snapshot.NocStatus;
        _sessionId = snapshot.ClientSessionId;
        _lastRemoteError = snapshot.LastRemoteError;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    /// <summary>
    /// Raises <see cref="Faulted"/> with the given message.
    /// </summary>
    public void RaiseFault(string message) => Faulted?.Invoke(this, message);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ReleaseHeldStart();
        ReleaseHeldStop();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Reflection helpers for reading private UI fields in MainForm tests.
/// </summary>
internal static class UiReflection
{
    /// <summary>
    /// Reads a non-public instance field of type <typeparamref name="T"/> by name.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the field is missing.</exception>
    public static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field is null)
        {
            throw new InvalidOperationException($"Field '{fieldName}' was not found on {instance.GetType().Name}.");
        }

        return (T)field.GetValue(instance)!;
    }
}
