// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    WebSocketRemoteConsoleTransport.cs
//
// Purpose:
// Frozen/legacy outbound WSS transport with optional client certificate; not
// registered by production DI (TcpMtlsRemoteConsoleTransport is used instead).
//
// -----------------------------------------------------------------------------

using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;

namespace TechConnect.Client.Transport;

/// <summary>
/// Frozen/legacy outbound WSS transport with optional mTLS client certificate.
/// </summary>
/// <remarks>
/// <para>
/// Not registered by production DI; retained for existing tests and historical
/// behaviour. Production uses <see cref="TcpMtlsRemoteConsoleTransport"/> with
/// TechConnect.Protocol framing over TCP/mTLS.
/// </para>
/// <para>
/// Framing contract (WebSocket message types, not TechConnect.Protocol frames):
/// binary messages carry raw serial bytes (one message = one chunk); text messages
/// carry versioned JSON control payloads. Server certificate validation uses the
/// standard .NET TLS pipeline and is never bypassed. Client certificates are optional
/// (unlike the TCP/mTLS transport, which requires one).
/// </para>
/// <para>
/// Connect cancels after <see cref="RemoteServiceOptions.ConnectTimeoutSeconds"/>.
/// Teardown aborts the socket, cancels the receive CTS, joins the receive task
/// (bounded by <see cref="ReceiveJoinTimeout"/>), then disposes the socket and
/// retained client certificate.
/// </para>
/// </remarks>
internal sealed class WebSocketRemoteConsoleTransport : IRemoteConsoleTransport
{
    /// <summary>
    /// After abort, receive/send loops should exit quickly. Bound prevents indefinite hang if the
    /// runtime fails to complete the task after Abort.
    /// </summary>
    internal static readonly TimeSpan ReceiveJoinTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Optional provider of a client certificate for WSS mTLS.</summary>
    private readonly IClientCertificateProvider _certificateProvider;

    /// <summary>Bound BaseUrl/path, timeouts, and maximum message size.</summary>
    private readonly IOptions<RemoteServiceOptions> _options;

    /// <summary>Structured logger for this transport.</summary>
    private readonly ILogger<WebSocketRemoteConsoleTransport> _logger;

    /// <summary>Serializes connect, disconnect, and dispose against concurrent callers.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Serializes outbound sends while the socket is open. Does not guarantee FIFO waiter order.
    /// Teardown uses Abort rather than waiting forever on this gate for a graceful close.
    /// </summary>
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    /// <summary>Active client WebSocket while connected; aborted and disposed on teardown.</summary>
    private ClientWebSocket? _socket;

    /// <summary>Client certificate retained for the active connection when configured; disposed on teardown.</summary>
    private X509Certificate2? _activeClientCertificate;

    /// <summary>Cancels the background receive loop during teardown.</summary>
    private CancellationTokenSource? _receiveCts;

    /// <summary>Background task that receives WebSocket messages until cancelled or faulted.</summary>
    private Task? _receiveTask;

    /// <summary>Non-zero while teardown is in progress or completed for the current socket.</summary>
    private int _tearingDown;

    /// <summary>True after <see cref="DisposeAsync"/> has run.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new WSS transport instance.
    /// </summary>
    /// <param name="certificateProvider">Optional client certificate source for mTLS.</param>
    /// <param name="options">Legacy BaseUrl-based endpoint and size limits.</param>
    /// <param name="logger">Logger for connect/teardown diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public WebSocketRemoteConsoleTransport(
        IClientCertificateProvider certificateProvider,
        IOptions<RemoteServiceOptions> options,
        ILogger<WebSocketRemoteConsoleTransport> logger)
    {
        _certificateProvider = certificateProvider ?? throw new ArgumentNullException(nameof(certificateProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConnected =>
        Volatile.Read(ref _tearingDown) == 0 &&
        _socket?.State == WebSocketState.Open;

    /// <inheritdoc />
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SerialDataHandler { get; set; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? SerialDataReceived;

    /// <summary>
    /// Reserved for typed serial-configuration events; currently a no-op (add/remove discarded).
    /// Configuration still arrives as text control JSON on <see cref="ControlMessageReceived"/>.
    /// </summary>
    public event EventHandler<SerialPortConfiguration>? ConfigurationReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<string>? ControlMessageReceived;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <inheritdoc />
    /// <remarks>
    /// Builds a <c>wss://</c> URI from legacy <c>BaseUrl</c>, optionally attaches a client
    /// certificate, and connects with the default .NET remote-certificate validation callback.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A WebSocket connection is already open.</exception>
    /// <exception cref="RemoteTransportConfigurationException">Endpoint or certificate is misconfigured.</exception>
    /// <exception cref="RemoteTransportUnavailableException">Connect timed out or failed.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_socket?.State == WebSocketState.Open && Volatile.Read(ref _tearingDown) == 0)
            {
                throw new InvalidOperationException("A WebSocket connection is already open.");
            }

            await TearDownSocketAsync(operationId: "connect-replace").ConfigureAwait(false);

            var options = _options.Value;
            if (!RemoteEndpoint.IsConfigured(options))
            {
                throw new RemoteTransportConfigurationException(
                    "Remote service endpoint is not configured. Set RemoteService:BaseUrl when the central service is available.");
            }

            var endpoint = RemoteEndpoint.CreateWebSocketUri(options);
            X509Certificate2? clientCertificate = null;
            try
            {
                clientCertificate = _certificateProvider.GetClientCertificate();
            }
            catch (RemoteTransportConfigurationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RemoteTransportConfigurationException(
                    "Unable to load the configured client certificate.",
                    ex);
            }

            if (_certificateProvider.IsConfigured && clientCertificate is null)
            {
                throw new RemoteTransportConfigurationException(
                    "A client certificate thumbprint is configured but no certificate was returned.");
            }

            var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(1, options.HeartbeatIntervalSeconds));

            // Intentionally do NOT set a custom RemoteCertificateValidationCallback.
            // Hostname and chain validation use the default .NET TLS behaviour.
            if (clientCertificate is not null)
            {
                socket.Options.ClientCertificates.Add(clientCertificate);
                _logger.LogInformation("Configured mTLS client certificate for WSS connection");
            }
            else
            {
                _logger.LogInformation("Connecting with WSS without a client certificate (thumbprint not configured)");
            }

            _logger.LogInformation("Opening WSS connection to {Host}{Path}", endpoint.Host, endpoint.AbsolutePath);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ConnectTimeoutSeconds)));

            try
            {
                await socket.ConnectAsync(endpoint, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                socket.Dispose();
                clientCertificate?.Dispose();
                throw new RemoteTransportUnavailableException(
                    "The WSS connection attempt timed out.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not RemoteTransportConfigurationException)
            {
                socket.Dispose();
                clientCertificate?.Dispose();
                _logger.LogWarning(ex, "WSS connection failed to {Host}", endpoint.Host);
                throw new RemoteTransportUnavailableException(
                    "Unable to establish a secure WebSocket connection.",
                    ex);
            }

            Volatile.Write(ref _tearingDown, 0);
            _socket = socket;
            _activeClientCertificate = clientCertificate;
            _receiveCts = new CancellationTokenSource();
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token), CancellationToken.None);

            _logger.LogInformation("WSS connection established to {Host}", endpoint.Host);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Sends one binary WebSocket message containing the raw serial chunk.</remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="RemoteTransportUnavailableException">Not connected, disconnecting, or oversize.</exception>
    public async Task SendSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (data.IsEmpty)
        {
            return;
        }

        var maxMessageBytes = Math.Max(1024, _options.Value.MaximumMessageBytes);
        if (data.Length > maxMessageBytes)
        {
            throw new RemoteTransportUnavailableException(
                $"Serial chunk size {data.Length} exceeds MaximumMessageBytes ({maxMessageBytes}).");
        }

        await SendFrameAsync(data, WebSocketMessageType.Binary, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>Sends one UTF-8 text WebSocket message containing the control JSON.</remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="json"/> is null or whitespace.</exception>
    /// <exception cref="RemoteTransportUnavailableException">Not connected, disconnecting, or oversize.</exception>
    public async Task SendControlMessageAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var maxMessageBytes = Math.Max(1024, _options.Value.MaximumMessageBytes);
        if (bytes.Length > maxMessageBytes)
        {
            throw new RemoteTransportUnavailableException(
                $"Control message size {bytes.Length} exceeds MaximumMessageBytes ({maxMessageBytes}).");
        }

        await SendFrameAsync(bytes, WebSocketMessageType.Text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a complete WebSocket message under <see cref="_sendGate"/>.
    /// </summary>
    /// <param name="data">Payload bytes.</param>
    /// <param name="messageType">Binary for serial, Text for control.</param>
    /// <param name="cancellationToken">Token that can cancel the send.</param>
    private async Task SendFrameAsync(
        ReadOnlyMemory<byte> data,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _tearingDown) != 0)
        {
            throw new RemoteTransportUnavailableException("Remote WebSocket transport is disconnecting.");
        }

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _tearingDown) != 0)
            {
                throw new RemoteTransportUnavailableException("Remote WebSocket transport is disconnecting.");
            }

            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                throw new RemoteTransportUnavailableException("Remote WebSocket transport is not connected.");
            }

            await socket.SendAsync(data, messageType, endOfMessage: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RemoteTransportUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            var label = messageType == WebSocketMessageType.Text ? "control send" : "send";
            _logger.LogWarning(ex, "WSS {Label} failed", label);
            RaiseFault($"Remote WebSocket {label} failed.");
            throw new RemoteTransportUnavailableException($"Remote WebSocket {label} failed.", ex);
        }
        finally
        {
            try
            {
                _sendGate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Application-level heartbeats wait for the server protocol; when connected this method
    /// is a no-op. TCP/WebSocket keepalive is configured on <see cref="ClientWebSocketOptions"/>.
    /// </remarks>
    public Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        // Application-level heartbeats wait for the server protocol.
        // TCP/WebSocket keepalive is configured on ClientWebSocketOptions.
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            return Task.FromException(new RemoteTransportUnavailableException(
                "Remote WebSocket transport is not connected."));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownSocketAsync(operationId: "disconnect").ConfigureAwait(false);
            _logger.LogInformation("WSS transport disconnected");
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Marks the instance disposed, tears down the socket, and disposes both gates.
    /// Safe to call more than once.
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
            await TearDownSocketAsync(operationId: "dispose").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            // Send gate disposed only after TearDown; no new WaitAsync callers after _disposed.
            _sendGate.Dispose();
        }
    }

    /// <summary>
    /// Receives WebSocket messages, reassembles fragments, and dispatches binary/text payloads.
    /// </summary>
    /// <param name="cancellationToken">Token cancelled during teardown.</param>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null)
        {
            return;
        }

        var buffer = new byte[16 * 1024];
        var message = new MemoryStream();
        WebSocketMessageType? accumulatingType = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   Volatile.Read(ref _tearingDown) == 0 &&
                   socket.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
                {
                    if (Volatile.Read(ref _tearingDown) == 0)
                    {
                        _logger.LogWarning(ex, "WSS receive failed");
                        RaiseFault("Remote WebSocket receive failed.");
                    }

                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation(
                        "WSS close received: {Status} {Description}",
                        socket.CloseStatus,
                        socket.CloseStatusDescription);
                    RaiseFault("Remote WebSocket closed.");
                    break;
                }

                accumulatingType ??= result.MessageType;

                var maxMessageBytes = Math.Max(1024, _options.Value.MaximumMessageBytes);
                if (message.Length + result.Count > maxMessageBytes)
                {
                    _logger.LogWarning(
                        "Closing WSS: inbound message exceeded {MaxBytes} bytes (payload not logged)",
                        maxMessageBytes);
                    // Oversized inbound: abort rather than risk a hung graceful close under load.
                    try
                    {
                        socket.Abort();
                    }
                    catch (Exception)
                    {
                    }

                    RaiseFault("Remote WebSocket message too large.");
                    break;
                }

                if (result.Count > 0)
                {
                    message.Write(buffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = message.ToArray();
                message.SetLength(0);
                var completedType = accumulatingType ?? result.MessageType;
                accumulatingType = null;

                if (payload.Length == 0)
                {
                    continue;
                }

                if (completedType == WebSocketMessageType.Text)
                {
                    var json = System.Text.Encoding.UTF8.GetString(payload);
                    try
                    {
                        ControlMessageReceived?.Invoke(this, json);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "ControlMessageReceived handler failed");
                    }

                    continue;
                }

                try
                {
                    if (SerialDataHandler is { } handler)
                    {
                        await handler(payload, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        SerialDataReceived?.Invoke(this, payload);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "SerialDataHandler / SerialDataReceived failed");
                    RaiseFault("Unable to deliver remote serial data to the local serial port.");
                }
            }
        }
        finally
        {
            await message.DisposeAsync().ConfigureAwait(false);
            if (!cancellationToken.IsCancellationRequested &&
                Volatile.Read(ref _tearingDown) == 0 &&
                !IsConnected)
            {
                RaiseFault("Remote WebSocket connection ended.");
            }
        }
    }

    /// <summary>
    /// Aborts the socket, cancels and joins the receive loop, then disposes socket and certificate.
    /// </summary>
    /// <param name="operationId">Short label included in teardown logs.</param>
    private async Task TearDownSocketAsync(string operationId)
    {
        var started = Environment.TickCount64;
        Volatile.Write(ref _tearingDown, 1);

        _logger.LogInformation(
            "WSS teardown begin Op={OperationId} Stage=cancel_receive",
            operationId);

        var receiveCts = _receiveCts;
        _receiveCts = null;
        if (receiveCts is not null)
        {
            try
            {
                await receiveCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        var socket = _socket;
        _socket = null;

        // Abort first so pending ReceiveAsync/SendAsync terminate even when CTS is ignored.
        if (socket is not null)
        {
            _logger.LogInformation(
                "WSS teardown Op={OperationId} Stage=abort_socket ElapsedMs={ElapsedMs}",
                operationId,
                Environment.TickCount64 - started);
            try
            {
                socket.Abort();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WSS Abort during teardown reported an error");
            }
        }

        var receiveTask = _receiveTask;
        _receiveTask = null;
        if (receiveTask is not null)
        {
            _logger.LogInformation(
                "WSS teardown Op={OperationId} Stage=await_receive ElapsedMs={ElapsedMs}",
                operationId,
                Environment.TickCount64 - started);

            var finished = await Task.WhenAny(receiveTask, Task.Delay(ReceiveJoinTimeout)).ConfigureAwait(false);
            if (!ReferenceEquals(finished, receiveTask))
            {
                _logger.LogWarning(
                    "WSS receive loop did not exit within {TimeoutSeconds}s after abort; continuing teardown. Op={OperationId}",
                    ReceiveJoinTimeout.TotalSeconds,
                    operationId);
                ObserveBackgroundTask(receiveTask, "wss-receive");
            }
            else
            {
                try
                {
                    await receiveTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WSS receive task completed with exception during teardown");
                }
            }
        }

        receiveCts?.Dispose();

        if (socket is not null)
        {
            _logger.LogInformation(
                "WSS teardown Op={OperationId} Stage=dispose_socket ElapsedMs={ElapsedMs}",
                operationId,
                Environment.TickCount64 - started);
            try
            {
                socket.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WSS Dispose during teardown reported an error");
            }
        }

        _activeClientCertificate?.Dispose();
        _activeClientCertificate = null;

        _logger.LogInformation(
            "WSS teardown complete Op={OperationId} ElapsedMs={ElapsedMs}",
            operationId,
            Environment.TickCount64 - started);
    }

    /// <summary>
    /// Observes a background task that outlived the join timeout so its exception is not unobserved.
    /// </summary>
    /// <param name="task">Task that may still be running.</param>
    /// <param name="name">Diagnostic label (not logged as payload).</param>
    private static void ObserveBackgroundTask(Task task, string name)
    {
        _ = task.ContinueWith(
            static (t, state) =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    // Observed so the exception is not unobserved; no payload logging.
                    _ = t.Exception.Flatten();
                }
            },
            name,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Raises <see cref="Faulted"/>, logging if a handler throws.
    /// </summary>
    /// <param name="message">Technician-safe fault description.</param>
    private void RaiseFault(string message)
    {
        try
        {
            Faulted?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transport Faulted handler failed");
        }
    }
}
