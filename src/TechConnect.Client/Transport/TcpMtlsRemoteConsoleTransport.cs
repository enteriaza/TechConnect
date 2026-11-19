// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    TcpMtlsRemoteConsoleTransport.cs
//
// Purpose:
// Production remote-console transport: outbound TCP with mutual TLS, then
// TechConnect.Protocol binary framing over the authenticated SslStream.
//
// -----------------------------------------------------------------------------

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;

namespace TechConnect.Client.Transport;

/// <summary>
/// Production outbound transport: TCP plus mutual TLS, with TechConnect binary
/// framing over the authenticated <see cref="SslStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the sole <see cref="IRemoteConsoleTransport"/> in
/// <c>HostingExtensions.AddTechConnectClientCore</c>. Connects to
/// <see cref="RemoteServiceOptions.Host"/>:<see cref="RemoteServiceOptions.Port"/>,
/// authenticates as a TLS client with a required client certificate from
/// <see cref="IClientCertificateProvider"/>, and requires
/// <see cref="SslStream.IsMutuallyAuthenticated"/>. Offered protocols are TLS 1.2 and 1.3;
/// revocation checking follows <see cref="RemoteServiceOptions.CertificateRevocationMode"/>
/// (default <see cref="X509RevocationMode.NoCheck"/> for field/cellular deployments).
/// Platform chain trust and hostname/SAN validation remain enabled; there is no
/// accept-any-certificate path.
/// </para>
/// <para>
/// After mTLS succeeds, a <see cref="ProtocolSession"/> owns framing on the
/// <see cref="SslStream"/> (<see cref="FrameReader"/> / <see cref="FrameWriter"/>).
/// Serial bytes use <see cref="MessageType.SerialData"/>; control traffic uses typed
/// protocol messages. Legacy JSON control strings are translated on send via
/// <c>WriteLegacyControlAsync</c>. Inbound control frames are surfaced as UTF-8 JSON
/// text on <see cref="ControlMessageReceived"/>.
/// </para>
/// <para>
/// Connect uses a linked cancellation token with
/// <see cref="RemoteServiceOptions.ConnectTimeoutSeconds"/>. A background receive loop
/// is cancelled and joined (bounded by <see cref="ReceiveJoinTimeout"/>) during teardown.
/// <see cref="DisposeAsync"/> and <see cref="DisconnectAsync"/> tear down the protocol
/// session, SSL stream, and TCP client under a connection gate.
/// </para>
/// </remarks>
internal sealed class TcpMtlsRemoteConsoleTransport : IRemoteConsoleTransport
{
    /// <summary>
    /// Maximum time to wait for the receive loop to exit after teardown cancels it.
    /// </summary>
    internal static readonly TimeSpan ReceiveJoinTimeout = TimeSpan.FromSeconds(3);

    /// <summary>TLS versions offered during client authentication (TLS 1.2 and 1.3).</summary>
    private static readonly SslProtocols SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Loads the connector client certificate used for mutual TLS.</summary>
    private readonly IClientCertificateProvider _certificateProvider;

    /// <summary>Bound remote endpoint and framing size limits.</summary>
    private readonly IOptions<RemoteServiceOptions> _options;

    /// <summary>Structured logger for this transport.</summary>
    private readonly ILogger<TcpMtlsRemoteConsoleTransport> _logger;

    /// <summary>Serializes connect, disconnect, and dispose against concurrent callers.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Active TCP client while connected; disposed on teardown.</summary>
    private TcpClient? _tcpClient;

    /// <summary>Authenticated SSL stream wrapping the TCP network stream; disposed on teardown.</summary>
    private SslStream? _sslStream;

    /// <summary>Framing session over <see cref="_sslStream"/>; disposed on teardown.</summary>
    private ProtocolSession? _protocol;

    /// <summary>Client certificate retained for the active connection; disposed on teardown.</summary>
    private X509Certificate2? _activeClientCertificate;

    /// <summary>Cancels the background receive loop during teardown.</summary>
    private CancellationTokenSource? _receiveCts;

    /// <summary>Background task that reads framed messages until cancelled or faulted.</summary>
    private Task? _receiveTask;

    /// <summary>Non-zero while teardown is in progress or completed for the current socket.</summary>
    private int _tearingDown;

    /// <summary>True after <see cref="DisposeAsync"/> has run.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new TCP/mTLS transport instance.
    /// </summary>
    /// <param name="certificateProvider">Provider of the client certificate required for mTLS.</param>
    /// <param name="options">Remote service host, port, TLS host name, and size limits.</param>
    /// <param name="logger">Logger for connect/teardown diagnostics.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TcpMtlsRemoteConsoleTransport(
        IClientCertificateProvider certificateProvider,
        IOptions<RemoteServiceOptions> options,
        ILogger<TcpMtlsRemoteConsoleTransport> logger)
    {
        _certificateProvider = certificateProvider ?? throw new ArgumentNullException(nameof(certificateProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsConnected =>
        Volatile.Read(ref _tearingDown) == 0 &&
        _sslStream is { IsAuthenticated: true } &&
        _tcpClient is { Connected: true } &&
        _protocol is not null;

    /// <summary>Authenticated protocol session; <see langword="null"/> when disconnected.</summary>
    internal ProtocolSession? Protocol => _protocol;

    /// <inheritdoc />
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SerialDataHandler { get; set; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? SerialDataReceived;

    /// <inheritdoc />
    public event EventHandler<string>? ControlMessageReceived;

    /// <inheritdoc />
    public event EventHandler<string>? Faulted;

    /// <inheritdoc />
    /// <remarks>
    /// Requires a configured host/port and a non-null client certificate. Performs TCP connect,
    /// then <c>SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)</c>
    /// with mutual authentication, then starts the framed receive loop.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="InvalidOperationException">A connection is already open.</exception>
    /// <exception cref="RemoteTransportConfigurationException">Endpoint or certificate is misconfigured.</exception>
    /// <exception cref="RemoteTransportException">Connect, TLS, or socket failure (including connect timeout).</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (IsConnected)
            {
                throw new InvalidOperationException("A TCP/TLS connection is already open.");
            }

            await TearDownCoreAsync(operationId: "connect-replace").ConfigureAwait(false);

            var options = _options.Value;
            if (!RemoteEndpoint.IsConfigured(options))
            {
                throw new RemoteTransportConfigurationException(
                    "Remote service endpoint is not configured. Set RemoteService:Host when the central service is available.");
            }

            var tlsHostName = RemoteEndpoint.ResolveTlsHostName(options);
            X509Certificate2? clientCertificate;
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

            if (clientCertificate is null)
            {
                throw new RemoteTransportConfigurationException(
                    "A client certificate is required for mutual TLS to TechConnect.Server.");
            }

            var tcp = new TcpClient();
            SslStream? ssl = null;
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.ConnectTimeoutSeconds)));

                _logger.LogInformation(
                    "Connecting TCP/mTLS to {Host}:{Port} (TLS host name {TlsHostName})",
                    options.Host,
                    options.Port,
                    tlsHostName);

                await tcp.ConnectAsync(options.Host, options.Port, connectCts.Token).ConfigureAwait(false);
                tcp.NoDelay = true;

                ssl = new SslStream(
                    tcp.GetStream(),
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: null);

                var clientCertificates = new X509Certificate2Collection(clientCertificate);
                var revocationMode = options.CertificateRevocationMode;
                _logger.LogInformation(
                    "TCP/mTLS certificate revocation check mode is {RevocationMode}",
                    revocationMode);

                await ssl.AuthenticateAsClientAsync(
                        CreateSslClientAuthenticationOptions(
                            tlsHostName,
                            clientCertificates,
                            revocationMode),
                        connectCts.Token)
                    .ConfigureAwait(false);

                if (!ssl.IsAuthenticated || !ssl.IsMutuallyAuthenticated)
                {
                    throw new RemoteTransportException(
                        "TCP/TLS handshake completed without mutual authentication.");
                }

                var limits = CreateProtocolLimits(options);

                _tcpClient = tcp;
                _sslStream = ssl;
                _protocol = new ProtocolSession(ssl, limits);
                _activeClientCertificate = clientCertificate;
                Volatile.Write(ref _tearingDown, 0);

                _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _receiveTask = Task.Run(
                    () => ReceiveLoopAsync(_receiveCts.Token),
                    CancellationToken.None);

                _logger.LogInformation(
                    "TCP/mTLS connected. Protocol={Protocol}, Cipher={Cipher}, MaxMessageBytes={MaxMessageBytes}",
                    ssl.SslProtocol,
                    ssl.NegotiatedCipherSuite,
                    options.MaximumMessageBytes);

                tcp = null!;
                ssl = null;
                clientCertificate = null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RemoteTransportException(
                    $"TCP/mTLS connect timed out after {options.ConnectTimeoutSeconds}s.");
            }
            catch (AuthenticationException ex)
            {
                throw new RemoteTransportException(
                    "TLS authentication failed. The server certificate was rejected or mutual TLS could not be negotiated.",
                    ex);
            }
            catch (SocketException ex)
            {
                // Wrap without logging — ReconnectCoordinator owns the concise per-attempt failure log.
                throw new RemoteTransportException(
                    $"Unable to connect to {options.Host}:{options.Port}.",
                    ex);
            }
            finally
            {
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }

                tcp?.Dispose();
                clientCertificate?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>Writes a <see cref="MessageType.SerialData"/> frame via <see cref="ProtocolSession"/>.</remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="RemoteTransportUnavailableException">The TCP/TLS session is not open.</exception>
    public Task SendSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var protocol = _protocol;
        if (!IsConnected || protocol is null)
        {
            throw new RemoteTransportUnavailableException("The remote TCP/TLS connection is not open.");
        }

        return protocol.WriteSerialDataAsync(data, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Parses legacy control JSON and maps supported types to typed protocol writes
    /// (Hello, SetBaud, SetBaudResult, SerialConfiguration, Error, SessionPingResult).
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="RemoteTransportUnavailableException">The TCP/TLS session is not open.</exception>
    /// <exception cref="RemoteTransportException">The JSON is invalid or the control type is unsupported.</exception>
    public async Task SendControlMessageAsync(string json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var protocol = _protocol;
        if (!IsConnected || protocol is null)
        {
            throw new RemoteTransportUnavailableException("The remote TCP/TLS connection is not open.");
        }

        if (!ControlProtocol.TryParse(json, out var legacy, out var parseError) || legacy is null)
        {
            throw new RemoteTransportException(parseError ?? "Control message is invalid.");
        }

        await WriteLegacyControlAsync(protocol, legacy, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a typed Hello frame for protocol integration tests and future session code.
    /// </summary>
    /// <param name="message">Hello payload to write.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    /// <exception cref="RemoteTransportUnavailableException">The TCP/TLS session is not open.</exception>
    internal Task SendHelloAsync(HelloMessage message, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var protocol = _protocol;
        if (!IsConnected || protocol is null)
        {
            throw new RemoteTransportUnavailableException("The remote TCP/TLS connection is not open.");
        }

        return protocol.WriteHelloAsync(message, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>Teardown is idempotent and joins the receive loop under <see cref="_gate"/>.</remarks>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownCoreAsync(operationId: "disconnect").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Marks the instance disposed, tears down the active session, and disposes <see cref="_gate"/>.
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
            await TearDownCoreAsync(operationId: "dispose").ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    /// <summary>
    /// Reads framed messages until cancellation, peer close, or a fatal framing/transport fault.
    /// </summary>
    /// <param name="cancellationToken">Token cancelled during teardown.</param>
    /// <remarks>
    /// Fatal <see cref="ProtocolException"/> ends the loop without resynchronization and raises
    /// <see cref="Faulted"/>. A clean EOF (<see langword="null"/> frame) is treated as peer close.
    /// </remarks>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var protocol = _protocol;
        if (protocol is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ProtocolFrame? frame;
                try
                {
                    frame = await protocol.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolException ex)
                {
                    if (Volatile.Read(ref _tearingDown) == 0)
                    {
                        _logger.LogWarning(ex, "Fatal protocol framing error on receive");
                        RaiseFaulted($"Protocol framing error: {ex.Message}");
                    }

                    return;
                }

                if (frame is null)
                {
                    RaiseFaulted("Remote TCP/TLS connection closed by the peer.");
                    return;
                }

                await DispatchFrameAsync(frame.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or AuthenticationException)
        {
            if (Volatile.Read(ref _tearingDown) == 0)
            {
                RaiseFaulted($"Remote TCP/TLS connection faulted: {ex.GetType().Name}.");
            }
        }
        catch (Exception ex)
        {
            if (Volatile.Read(ref _tearingDown) == 0)
            {
                _logger.LogWarning(ex, "Unexpected error in TCP/TLS receive loop");
                RaiseFaulted("Remote TCP/TLS receive loop faulted unexpectedly.");
            }
        }
    }

    /// <summary>
    /// Dispatches one decoded frame to serial or control handlers.
    /// </summary>
    /// <param name="frame">Frame read from <see cref="FrameReader"/>.</param>
    /// <param name="cancellationToken">Token passed to <see cref="SerialDataHandler"/> when set.</param>
    private async Task DispatchFrameAsync(ProtocolFrame frame, CancellationToken cancellationToken)
    {
        switch (frame.MessageType)
        {
            case MessageType.SerialData:
                if (SerialDataHandler is { } handler)
                {
                    await handler(frame.Payload, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    SerialDataReceived?.Invoke(this, frame.Payload);
                }

                break;

            case MessageType.HelloResult:
            case MessageType.SerialConfiguration:
            case MessageType.SetBaud:
            case MessageType.SetBaudResult:
            case MessageType.Error:
            case MessageType.Disconnect:
            case MessageType.Hello:
            case MessageType.OperatorStatus:
                // Surface as UTF-8 JSON text for existing session handlers; type is in the frame header.
                var json = Encoding.UTF8.GetString(frame.Payload);
                ControlMessageReceived?.Invoke(this, json);
                break;

            case MessageType.SessionPing:
                DispatchSessionPing(frame.Payload);
                break;

            case MessageType.SessionPingResult:
                // Server-bound; connector ignores unexpected results.
                break;

            default:
                RaiseFaulted($"Unknown message type 0x{(byte)frame.MessageType:X2}.");
                break;
        }
    }

    /// <summary>
    /// Maps a legacy JSON control message onto the corresponding typed protocol write.
    /// </summary>
    /// <param name="protocol">Active framing session.</param>
    /// <param name="legacy">Parsed legacy control envelope.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <exception cref="RemoteTransportException">Payload is invalid or the type is unsupported.</exception>
    private static async Task WriteLegacyControlAsync(
        ProtocolSession protocol,
        ControlMessage legacy,
        CancellationToken cancellationToken)
    {
        if (string.Equals(legacy.Type, ControlProtocol.TypeConnectorHello, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out ConnectorHelloPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "connector.hello payload is invalid.");
            }

            await protocol.WriteHelloAsync(
                    new HelloMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        ClientSessionId = payload.ClientSessionId,
                        Hostname = payload.Hostname,
                        ApplicationVersion = payload.ApplicationVersion,
                        PortName = payload.PortName,
                        BaudRate = payload.Config.BaudRate,
                        DataBits = payload.Config.DataBits,
                        Parity = payload.Config.Parity,
                        StopBits = payload.Config.StopBits,
                        Handshake = payload.Config.Handshake
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(legacy.Type, ControlProtocol.TypeSetBaud, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out SetBaudPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "serial.setBaud payload is invalid.");
            }

            await protocol.WriteControlAsync(
                    MessageType.SetBaud,
                    new SetBaudMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        BaudRate = payload.BaudRate,
                        DataBits = payload.DataBits,
                        Parity = payload.Parity,
                        StopBits = payload.StopBits,
                        Handshake = payload.Handshake
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(legacy.Type, ControlProtocol.TypeSetBaudResult, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out SetBaudResultPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "serial.setBaudResult payload is invalid.");
            }

            await protocol.WriteControlAsync(
                    MessageType.SetBaudResult,
                    new SetBaudResultMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        Success = payload.Success,
                        Error = payload.Error,
                        Config = payload.Config is null
                            ? null
                            : new SerialConfigurationMessage
                            {
                                BaudRate = payload.Config.BaudRate,
                                DataBits = payload.Config.DataBits,
                                Parity = payload.Config.Parity,
                                StopBits = payload.Config.StopBits,
                                Handshake = payload.Config.Handshake
                            }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(legacy.Type, ControlProtocol.TypeSerialConfig, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out SerialConfigPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "serial.config payload is invalid.");
            }

            await protocol.WriteControlAsync(
                    MessageType.SerialConfiguration,
                    new SerialConfigurationMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        BaudRate = payload.BaudRate,
                        DataBits = payload.DataBits,
                        Parity = payload.Parity,
                        StopBits = payload.StopBits,
                        Handshake = payload.Handshake
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(legacy.Type, ControlProtocol.TypeError, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out ErrorPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "error payload is invalid.");
            }

            await protocol.WriteControlAsync(
                    MessageType.Error,
                    new ErrorMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        Code = payload.Code,
                        Message = payload.Message
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (string.Equals(legacy.Type, ControlProtocol.TypeSessionPingResult, StringComparison.Ordinal))
        {
            if (!ControlProtocol.TryGetPayload(legacy, out SessionPingResultPayload? payload, out var error) ||
                payload is null)
            {
                throw new RemoteTransportException(error ?? "session.pingResult payload is invalid.");
            }

            await protocol.WriteControlAsync(
                    MessageType.SessionPingResult,
                    new SessionPingResultMessage
                    {
                        CorrelationId = legacy.CorrelationId,
                        Success = payload.Success,
                        Error = payload.Error,
                        Hostname = payload.Hostname
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        throw new RemoteTransportException($"Unsupported control message type '{legacy.Type}' for binary framing.");
    }

    /// <summary>
    /// Deserializes an inbound SessionPing frame and raises <see cref="ControlMessageReceived"/> as legacy JSON.
    /// </summary>
    /// <param name="payload">Raw SessionPing payload bytes.</param>
    private void DispatchSessionPing(byte[] payload)
    {
        try
        {
            var ping = ControlMessageSerializer.DeserializeSessionPing(payload);
            var legacy = ControlProtocol.Create(
                ControlProtocol.TypeSessionPing,
                new { },
                ping.CorrelationId);
            ControlMessageReceived?.Invoke(this, ControlProtocol.Serialize(legacy));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid SessionPing frame");
        }
    }

    /// <summary>
    /// Cancels the receive loop, closes the socket, disposes protocol/SSL/TCP resources, and joins the receive task.
    /// </summary>
    /// <param name="operationId">Short label included in teardown logs (connect-replace, disconnect, dispose).</param>
    private async Task TearDownCoreAsync(string operationId)
    {
        if (Interlocked.Exchange(ref _tearingDown, 1) == 1 &&
            _sslStream is null &&
            _tcpClient is null)
        {
            return;
        }

        var started = Environment.TickCount64;
        _logger.LogInformation("TCP/mTLS teardown begin Op={OperationId}", operationId);

        try
        {
            if (_receiveCts is not null)
            {
                try
                {
                    await _receiveCts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            try
            {
                _tcpClient?.Client?.Close();
            }
            catch
            {
            }

            try
            {
                if (_protocol is not null)
                {
                    await _protocol.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                if (_sslStream is not null)
                {
                    await _sslStream.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch
            {
            }

            try
            {
                _tcpClient?.Dispose();
            }
            catch
            {
            }

            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.WaitAsync(ReceiveJoinTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "TCP/mTLS receive loop did not exit within {TimeoutMs}ms Op={OperationId}",
                        ReceiveJoinTimeout.TotalMilliseconds,
                        operationId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "TCP/mTLS receive join Op={OperationId}", operationId);
                }
            }
        }
        finally
        {
            _protocol = null;
            _sslStream = null;
            _tcpClient = null;
            _receiveTask = null;
            _receiveCts?.Dispose();
            _receiveCts = null;
            _activeClientCertificate?.Dispose();
            _activeClientCertificate = null;

            _logger.LogInformation(
                "TCP/mTLS teardown complete Op={OperationId} ElapsedMs={ElapsedMs}",
                operationId,
                Environment.TickCount64 - started);
        }
    }

    /// <summary>
    /// Raises <see cref="Faulted"/>, swallowing handler exceptions so teardown paths stay resilient.
    /// </summary>
    /// <param name="reason">Technician-safe fault description (no secrets or payloads).</param>
    private void RaiseFaulted(string reason)
    {
        try
        {
            Faulted?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Faulted event handler threw");
        }
    }

    /// <summary>
    /// Builds SSL client authentication options for the outbound mTLS handshake.
    /// </summary>
    /// <remarks>
    /// Exposed to tests so the configured revocation mode can be asserted without performing
    /// a live handshake. Does not weaken chain or hostname validation.
    /// </remarks>
    /// <param name="tlsHostName">Target host name used for certificate name validation.</param>
    /// <param name="clientCertificates">Client certificate collection (required for mTLS).</param>
    /// <param name="revocationMode">Configured <see cref="X509RevocationMode"/>.</param>
    /// <returns>Options passed to <c>SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)</c>.</returns>
    internal static SslClientAuthenticationOptions CreateSslClientAuthenticationOptions(
        string tlsHostName,
        X509Certificate2Collection clientCertificates,
        X509RevocationMode revocationMode) =>
        new()
        {
            TargetHost = tlsHostName,
            ClientCertificates = clientCertificates,
            EnabledSslProtocols = SupportedSslProtocols,
            CertificateRevocationCheckMode = revocationMode
        };

    /// <summary>
    /// Maps validated <see cref="RemoteServiceOptions.MaximumMessageBytes"/> onto protocol limits.
    /// </summary>
    /// <remarks>
    /// Frame and serial-data ceilings equal the configured maximum so the transport cannot
    /// accept larger inbound payloads than configuration validation allowed. Control payloads
    /// remain capped at the protocol control default when the message maximum is larger.
    /// </remarks>
    /// <param name="options">Validated remote service options.</param>
    /// <returns>Protocol limits applied to the framed session.</returns>
    internal static ProtocolLimits CreateProtocolLimits(RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var maxMessageBytes = options.MaximumMessageBytes;
        return new ProtocolLimits
        {
            MaxSerialDataPayloadBytes = maxMessageBytes,
            MaxFramePayloadBytes = maxMessageBytes,
            MaxControlPayloadBytes = Math.Min(
                ProtocolLimits.DefaultMaxControlPayloadBytes,
                maxMessageBytes)
        };
    }
}
