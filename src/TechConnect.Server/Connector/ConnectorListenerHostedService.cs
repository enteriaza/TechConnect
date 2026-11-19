// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorListenerHostedService.cs
//
// Purpose:
// Hosts the connector TCP/TLS listener: mTLS accept, Hello registration, framed
// protocol loop, serial forwarding into attachments, and connection teardown cleanup.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Connector;

/// <summary>
/// Background service that accepts connector TCP connections, requires mTLS, runs
/// the binary protocol loop, and cleans up registry/attachment state on disconnect.
/// </summary>
/// <remarks>
/// <para>
/// When disabled in configuration, <see cref="ExecuteAsync"/> returns without binding.
/// Capacity is bounded by <see cref="ConnectorListenerOptions.MaximumConcurrentConnections"/>.
/// Each accepted connection runs on a dedicated task; the finally path detaches operators
/// for that ConnectionId, fails pending baud/ping work, unregisters the session, removes
/// the tracker entry, and disposes the <see cref="ConnectorConnection"/>.
/// </para>
/// <para>
/// Attachment release on connector disconnect does not by itself end the operator SSH
/// session. Administrative disconnect disposes the connection; this listener still owns
/// registry/attachment cleanup in the connection finally block.
/// </para>
/// <para>
/// Fatal framing errors end the protocol loop without resynchronization. Unattached
/// SerialData is discarded. Operator outbound overflow detaches the slow operator
/// (<see cref="AttachmentDetachReason.OperatorBackpressure"/>) without terminating the connector.
/// </para>
/// </remarks>
internal sealed class ConnectorListenerHostedService : BackgroundService
{
    /// <summary>TLS versions offered during server authentication (TLS 1.2 and 1.3).</summary>
    private static readonly SslProtocols SupportedSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;

    /// <summary>Bound server options including connector listener settings.</summary>
    private readonly IOptions<TechConnectServerOptions> _options;

    /// <summary>Loads the server certificate used for connector mTLS.</summary>
    private readonly ServerCertificateProvider _serverCertificateProvider;

    /// <summary>Validates connector client certificates during the TLS handshake.</summary>
    private readonly ConnectorClientCertificateValidator _clientCertificateValidator;

    /// <summary>Tracks authenticated <see cref="ConnectorConnection"/> instances.</summary>
    private readonly ConnectorConnectionTracker _tracker;

    /// <summary>Registry of Hello-registered connector sessions.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Authoritative operator↔connector attachment ownership.</summary>
    private readonly OperatorAttachmentManager _attachmentManager;

    /// <summary>Cache of connector-reported serial configuration for CLI display.</summary>
    private readonly ConnectorSerialConfigurationCache _serialConfigCache;

    /// <summary>Outstanding SetBaud correlation tracker.</summary>
    private readonly PendingBaudRequestTracker _pendingBaudRequests;

    /// <summary>Outstanding SessionPing correlation tracker.</summary>
    private readonly PendingSessionPingTracker _sessionPingTracker;

    /// <summary>Clock for registration and activity timestamps.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Audit sink for registration/disconnect/attachment events.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Structured logger for this hosted service.</summary>
    private readonly ILogger<ConnectorListenerHostedService> _logger;

    /// <summary>In-flight connection handling tasks keyed by connection ID.</summary>
    private readonly ConcurrentDictionary<string, Task> _connectionTasks = new(StringComparer.Ordinal);

    /// <summary>Limits concurrent accepted connections; released when a connection task ends.</summary>
    private readonly SemaphoreSlim _connectionCapacity;

    /// <summary>Loaded server certificate retained until <see cref="Dispose"/>.</summary>
    private X509Certificate2? _serverCertificate;

    /// <summary>Active TCP listener while accepting; stopped on shutdown.</summary>
    private TcpListener? _listener;

    /// <summary>
    /// Creates the connector listener hosted service.
    /// </summary>
    /// <param name="options">Server options.</param>
    /// <param name="serverCertificateProvider">Server certificate provider.</param>
    /// <param name="clientCertificateValidator">Client certificate validator.</param>
    /// <param name="tracker">Connection tracker.</param>
    /// <param name="sessionRegistry">Session registry.</param>
    /// <param name="attachmentManager">Attachment manager.</param>
    /// <param name="serialConfigCache">Serial configuration cache.</param>
    /// <param name="pendingBaudRequests">Pending baud tracker.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="audit">Optional audit writer; defaults to no-op.</param>
    /// <param name="sessionPingTracker">Optional session ping tracker; defaults to a new instance.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ConnectorListenerHostedService(
        IOptions<TechConnectServerOptions> options,
        ServerCertificateProvider serverCertificateProvider,
        ConnectorClientCertificateValidator clientCertificateValidator,
        ConnectorConnectionTracker tracker,
        ConnectorSessionRegistry sessionRegistry,
        OperatorAttachmentManager attachmentManager,
        ConnectorSerialConfigurationCache serialConfigCache,
        PendingBaudRequestTracker pendingBaudRequests,
        TimeProvider timeProvider,
        ILogger<ConnectorListenerHostedService> logger,
        IAuditEventWriter? audit = null,
        PendingSessionPingTracker? sessionPingTracker = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _sessionPingTracker = sessionPingTracker ?? new PendingSessionPingTracker();
        _serverCertificateProvider = serverCertificateProvider
            ?? throw new ArgumentNullException(nameof(serverCertificateProvider));
        _clientCertificateValidator = clientCertificateValidator
            ?? throw new ArgumentNullException(nameof(clientCertificateValidator));
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _attachmentManager = attachmentManager ?? throw new ArgumentNullException(nameof(attachmentManager));
        _serialConfigCache = serialConfigCache ?? throw new ArgumentNullException(nameof(serialConfigCache));
        _pendingBaudRequests = pendingBaudRequests ?? throw new ArgumentNullException(nameof(pendingBaudRequests));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? new NoOpAuditEventWriter();

        var max = Math.Max(1, options.Value.Connector?.MaximumConcurrentConnections ?? 100);
        _connectionCapacity = new SemaphoreSlim(max, max);
    }

    /// <summary>
    /// Binds the TCP listener (when enabled) and accepts connections until cancellation.
    /// </summary>
    /// <param name="stoppingToken">Hosted-service stopping token; cancels Accept and handshake linkage.</param>
    /// <returns>A task that completes when accepting stops.</returns>
    /// <exception cref="InvalidOperationException">Configured listen address cannot be parsed.</exception>
    /// <remarks>
    /// Rejects new TCP clients immediately when capacity is exhausted. Handshake failures
    /// dispose the client without tracking. Does not clear registry/attachments here;
    /// that occurs in <see cref="StopAsync"/> and per-connection finally blocks.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connector = _options.Value.Connector ?? new ConnectorListenerOptions();
        if (!connector.Enabled)
        {
            _logger.LogInformation("Connector TCP/TLS listener is disabled by configuration");
            return;
        }

        _serverCertificate = _serverCertificateProvider.LoadRequiredCertificate();

        if (!IPAddress.TryParse(connector.ListenAddress, out var listenAddress))
        {
            throw new InvalidOperationException(
                $"Connector listen address '{connector.ListenAddress}' is invalid.");
        }

        var listener = new TcpListener(listenAddress, connector.Port);
        listener.Start();
        _listener = listener;

        _logger.LogInformation(
            "Connector TCP/TLS listener started on {ListenAddress}:{Port} (max concurrent {MaxConnections})",
            listenAddress,
            connector.Port,
            connector.MaximumConcurrentConnections);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    // TcpListener.Stop() during shutdown aborts Accept with a socket error
                    // (often before the hosted-service stopping token is cancelled).
                    break;
                }

                var remote = client.Client.RemoteEndPoint;
                _logger.LogInformation("Accepted connector TCP connection from {RemoteEndPoint}", remote);

                if (!await _connectionCapacity.WaitAsync(0, stoppingToken).ConfigureAwait(false))
                {
                    _logger.LogWarning(
                        "Rejecting connector connection from {RemoteEndPoint}: maximum concurrent connections reached",
                        remote);
                    try
                    {
                        client.Dispose();
                    }
                    catch
                    {
                    }

                    continue;
                }

                var connectionId = Guid.NewGuid().ToString("N");
                var task = Task.Run(
                    () => HandleConnectionAsync(client, connectionId, stoppingToken),
                    CancellationToken.None);

                if (!_connectionTasks.TryAdd(connectionId, task))
                {
                    _connectionCapacity.Release();
                    try
                    {
                        client.Dispose();
                    }
                    catch
                    {
                    }

                    continue;
                }

                _ = task.ContinueWith(
                    t =>
                    {
                        _connectionTasks.TryRemove(connectionId, out _);
                        _connectionCapacity.Release();
                        if (t.IsFaulted && t.Exception is not null)
                        {
                            _logger.LogWarning(
                                t.Exception.Flatten(),
                                "Connector connection task faulted. ConnectionId={ConnectionId}",
                                connectionId);
                        }
                    },
                    TaskScheduler.Default);
            }
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }

            _logger.LogInformation("Connector TCP/TLS listener stopped accepting connections");
        }
    }

    /// <summary>
    /// Stops accepting, closes tracked connections, and clears baud/attachment/serial/session state.
    /// </summary>
    /// <param name="cancellationToken">Linked with the configured shutdown timeout.</param>
    /// <returns>A task that completes when shutdown steps finish and the base stop completes.</returns>
    /// <remarks>
    /// Uses <see cref="ConnectorListenerOptions.ShutdownTimeoutSeconds"/> (minimum 1s) for
    /// <see cref="ConnectorConnectionTracker.CloseAllAsync"/> and waiting on connection tasks.
    /// Clears pending baud requests, all attachments (<see cref="AttachmentDetachReason.Shutdown"/>),
    /// serial cache, and session registry after connection close attempts.
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(
            Math.Max(1, _options.Value.Connector?.ShutdownTimeoutSeconds ?? 10));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        _logger.LogInformation(
            "Connector listener shutting down (timeout {TimeoutSeconds}s, active={Active})",
            timeout.TotalSeconds,
            _tracker.Count);

        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error stopping connector TcpListener during shutdown");
        }

        try
        {
            await _tracker.CloseAllAsync(timeout, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing active connector connections during shutdown");
        }

        _pendingBaudRequests.ClearAll("Server is shutting down.");
        _attachmentManager.ClearAll(AttachmentDetachReason.Shutdown);
        _serialConfigCache.Clear();
        _sessionRegistry.ClearAll();
        _logger.LogInformation("Connector attachments, serial config cache, and session registry cleared during shutdown");

        var pending = _connectionTasks.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Connector connection tasks did not complete within shutdown timeout ({Pending} remaining)",
                    _connectionTasks.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error waiting for connector connection tasks during shutdown");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Connector listener shutdown complete");
    }

    /// <summary>
    /// Disposes connection capacity and the loaded server certificate.
    /// </summary>
    public override void Dispose()
    {
        _connectionCapacity.Dispose();
        _serverCertificate?.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Performs mTLS authentication, tracks the connection, runs the protocol loop, then cleans up.
    /// </summary>
    /// <param name="client">Accepted TCP client; ownership transfers to <see cref="ConnectorConnection"/> on success.</param>
    /// <param name="connectionId">Pre-allocated connection ID for this accept.</param>
    /// <param name="stoppingToken">Host stopping token linked into handshake and lifetime CTS.</param>
    /// <returns>A task that completes when the connection has been disposed and cleanup finished.</returns>
    /// <remarks>
    /// Requires mutual authentication and a validated client identity. On teardown: detaches by
    /// ConnectionId (attachment only), best-effort OperatorStatus Waiting publish, unregisters
    /// session, fails baud/ping, removes serial cache, removes tracker entry, disposes connection.
    /// </remarks>
    private async Task HandleConnectionAsync(
        TcpClient client,
        string connectionId,
        CancellationToken stoppingToken)
    {
        ConnectorConnection? tracked = null;
        SslStream? ssl = null;
        TcpClient? ownedClient = client;

        try
        {
            ownedClient.NoDelay = true;
            var network = ownedClient.GetStream();
            ssl = new SslStream(network, leaveInnerStreamOpen: false);

            ConnectorClientIdentity? peer = null;
            var handshakeSeconds = Math.Max(1, _options.Value.Connector.HandshakeTimeoutSeconds);
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(handshakeSeconds));

            try
            {
                await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _serverCertificate!,
                            ClientCertificateRequired = true,
                            EnabledSslProtocols = SupportedSslProtocols,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                            {
                                X509Certificate2? ownedCopy = null;
                                var leaf = certificate as X509Certificate2;
                                if (certificate is not null && leaf is null)
                                {
                                    ownedCopy = new X509Certificate2(certificate);
                                    leaf = ownedCopy;
                                }

                                try
                                {
                                    if (!_clientCertificateValidator.TryValidate(
                                            leaf,
                                            chain,
                                            errors,
                                            out var identity,
                                            out var validationError))
                                    {
                                        _logger.LogWarning(
                                            "Connector TLS client certificate rejected from {RemoteEndPoint}: {Error}",
                                            ownedClient.Client.RemoteEndPoint,
                                            validationError);
                                        return false;
                                    }

                                    peer = identity;
                                    return true;
                                }
                                finally
                                {
                                    ownedCopy?.Dispose();
                                }
                            }
                        },
                        handshakeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AuthenticationException or OperationCanceledException or IOException)
            {
                _logger.LogWarning(
                    ex,
                    "Connector TLS handshake failed for {RemoteEndPoint}. ConnectionId={ConnectionId}",
                    ownedClient.Client.RemoteEndPoint,
                    connectionId);
                return;
            }

            if (peer is null ||
                !ssl.IsAuthenticated ||
                !ssl.IsMutuallyAuthenticated ||
                ssl.RemoteCertificate is null)
            {
                _logger.LogWarning(
                    "Connector TLS handshake completed without mutual authentication. ConnectionId={ConnectionId}",
                    connectionId);
                return;
            }

            var lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            tracked = new ConnectorConnection(
                connectionId,
                peer.Thumbprint,
                peer.Subject,
                ownedClient.Client.RemoteEndPoint,
                ownedClient,
                ssl,
                ssl.SslProtocol,
                _timeProvider.GetUtcNow(),
                lifetimeCts,
                logger: _logger);

            // Ownership of ssl + TcpClient transferred to ConnectorConnection.
            ssl = null;
            ownedClient = null;

            if (!_tracker.TryAdd(tracked))
            {
                _logger.LogWarning(
                    "Failed to track authenticated connector connection. ConnectionId={ConnectionId}",
                    connectionId);
                await tracked.DisposeAsync().ConfigureAwait(false);
                tracked = null;
                return;
            }

            _logger.LogInformation(
                "Connector TLS authenticated. ConnectionId={ConnectionId}, Subject={Subject}, Thumbprint={Thumbprint}, Protocol={Protocol}, Remote={RemoteEndPoint}",
                connectionId,
                peer.Subject,
                peer.Thumbprint,
                tracked.NegotiatedProtocol,
                tracked.RemoteEndPoint);

            await RunProtocolAsync(tracked, lifetimeCts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (tracked is not null)
            {
                if (_attachmentManager.TryDetachByConnectionId(
                        tracked.ConnectionId,
                        AttachmentDetachReason.ConnectorDisconnect,
                        out var detached))
                {
                    _logger.LogInformation(
                        "Connector disconnect detached operator. SessionId={SessionId}, AttachmentId={AttachmentId}, ConnectionId={ConnectionId}",
                        detached!.SessionId,
                        detached.AttachmentId,
                        tracked.ConnectionId);
                    _audit.TryWrite(
                        AuditEventType.AttachmentReleasedConnectorDisconnect,
                        AuditEventOutcome.Informational,
                        "Attachment released after connector disconnect",
                        new AuditEventContext
                        {
                            ActorUsername = detached.Operator?.Username,
                            SessionId = detached.SessionId,
                            RemoteEndpoint = tracked.RemoteEndPoint
                        });
                    // CLI session owns final Dispose of the attachment object.
                }

                // Best-effort Waiting notification; cleanup continues even if the write fails.
                try
                {
                    using var statusCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await tracked.OperatorStatus.TryPublishWaitingAsync(statusCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "OperatorStatus Waiting publish skipped during connector teardown. ConnectionId={ConnectionId}",
                        tracked.ConnectionId);
                }

                if (_sessionRegistry.TryUnregisterByConnectionId(tracked.ConnectionId, out var session))
                {
                    _pendingBaudRequests.FailConnectorSession(
                        session!.SessionId,
                        "Connector disconnected.");
                    _sessionPingTracker.FailConnectorSession(
                        session.SessionId,
                        "Connector disconnected.");
                    _serialConfigCache.Remove(session.SessionId);
                    _logger.LogInformation(
                        "Connector session removed. SessionId={SessionId}, IdentityPrefix={IdentityPrefix}…, ConnectionId={ConnectionId}",
                        session.SessionId,
                        AbbreviateIdentity(session.ConnectorIdentity),
                        tracked.ConnectionId);
                    _audit.TryWrite(
                        AuditEventType.ConnectorDisconnected,
                        AuditEventOutcome.Informational,
                        "Connector disconnected and unregistered",
                        new AuditEventContext
                        {
                            SessionId = session.SessionId,
                            ConnectorIdentity = AbbreviateIdentity(session.ConnectorIdentity),
                            RemoteEndpoint = session.RemoteEndpoint ?? tracked.RemoteEndPoint,
                            Details = session.Hostname is { } hostname
                                ? new Dictionary<string, string>(StringComparer.Ordinal)
                                {
                                    ["hostname"] = hostname
                                }
                                : null
                        });
                }

                // Drop the hot-path cache after protocol loop exit / unregister (PERF-SRV-02).
                tracked.RegisteredSession = null;
                tracked.RegisteredSessionId = null;

                _tracker.TryRemove(tracked.ConnectionId, out _);
                _logger.LogInformation(
                    "Connector disconnected. ConnectionId={ConnectionId}, ThumbprintPrefix={ThumbprintPrefix}…, Remote={RemoteEndPoint}",
                    tracked.ConnectionId,
                    AbbreviateIdentity(tracked.CertificateThumbprint),
                    tracked.RemoteEndPoint);
                await tracked.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                if (ssl is not null)
                {
                    await ssl.DisposeAsync().ConfigureAwait(false);
                }

                ownedClient?.Dispose();
            }
        }
    }

    /// <summary>
    /// Protocol loop: Hello registration + framed message acceptance.
    /// </summary>
    /// <param name="connection">Authenticated tracked connection.</param>
    /// <param name="cancellationToken">Lifetime token; cancel ends the loop quietly.</param>
    /// <returns>A task that completes when the loop exits (EOF, Disconnect, fatal framing, cancel, or I/O dispose).</returns>
    /// <remarks>
    /// Phase 6: SerialData is forwarded to the attached operator (if any) via a bounded queue.
    /// Unattached RX is discarded. Fatal <see cref="ProtocolException"/> ends the connection
    /// without resynchronization. After registration, each frame touches session activity.
    /// </remarks>
    private async Task RunProtocolAsync(
        ConnectorConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ProtocolFrame? frame;
                try
                {
                    frame = await connection.Protocol.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ProtocolException ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Fatal protocol framing error. ConnectionId={ConnectionId}",
                        connection.ConnectionId);
                    return;
                }

                if (frame is null)
                {
                    return;
                }

                // PERF-SRV-02: advance LastActivityUtc on the cached session without the registry gate.
                connection.RegisteredSession?.ObserveActivity(_timeProvider.GetUtcNow());

                switch (frame.Value.MessageType)
                {
                    case MessageType.Hello:
                        await HandleHelloAsync(connection, frame.Value, cancellationToken).ConfigureAwait(false);
                        break;

                    case MessageType.Disconnect:
                        _logger.LogInformation(
                            "Connector sent Disconnect. ConnectionId={ConnectionId}",
                            connection.ConnectionId);
                        return;

                    case MessageType.SerialData:
                        HandleSerialData(connection, frame.Value);
                        break;

                    case MessageType.SerialConfiguration:
                        HandleSerialConfiguration(connection, frame.Value);
                        break;

                    case MessageType.SetBaudResult:
                        HandleSetBaudResult(connection, frame.Value);
                        break;

                    case MessageType.SessionPingResult:
                        HandleSessionPingResult(connection, frame.Value);
                        break;

                    case MessageType.SetBaud:
                    case MessageType.HelloResult:
                    case MessageType.Error:
                    case MessageType.SessionPing:
                        _logger.LogDebug(
                            "Received framed {MessageType} ({PayloadLength} byte(s)). ConnectionId={ConnectionId}",
                            frame.Value.MessageType,
                            frame.Value.Payload.Length,
                            connection.ConnectionId);
                        break;

                    default:
                        throw new ProtocolUnknownMessageTypeException((byte)frame.Value.MessageType);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Forwards SerialData payload into the attachment outbound queue when registered and attached.
    /// </summary>
    /// <param name="connection">Source connection.</param>
    /// <param name="frame">
    /// SerialData frame; on enqueue success payload ownership transfers into the operator
    /// outbound queue (PERF-SRV-01). On discard/overflow the frame payload is abandoned.
    /// </param>
    /// <remarks>
    /// Pre-registration frames are discarded without disconnecting. Overflow detaches the
    /// operator for backpressure; the connector connection remains up.
    /// </remarks>
    private void HandleSerialData(ConnectorConnection connection, ProtocolFrame frame)
    {
        var sessionId = connection.RegisteredSessionId;
        if (sessionId is null)
        {
            // Not registered — discard without disconnecting.
            return;
        }

        // Transfer FrameReader payload ownership into the queue (no second ToArray copy).
        var result = _attachmentManager.TryEnqueueDeviceRx(sessionId, frame.Payload);
        if (result == DeviceRxEnqueueResult.Overflow)
        {
            _logger.LogWarning(
                "Operator outbound queue overflow; detaching slow operator. SessionId={SessionId}, ConnectionId={ConnectionId}",
                sessionId,
                connection.ConnectionId);
            _attachmentManager.TryDetachBySessionId(
                sessionId,
                AttachmentDetachReason.OperatorBackpressure,
                out _);
        }
    }

    /// <summary>
    /// Validates Hello, registers the session (reject-new identity), seeds serial cache, and replies.
    /// </summary>
    /// <param name="connection">Authenticated connection performing Hello.</param>
    /// <param name="frame">Hello frame payload.</param>
    /// <param name="cancellationToken">Cancels HelloResult / OperatorStatus writes.</param>
    /// <returns>A task that completes after the reply (and initial OperatorStatus on success).</returns>
    /// <remarks>
    /// Identity is <see cref="ConnectorConnection.CertificateThumbprint"/>. Duplicate identity
    /// rejects without replacing the existing session. Successful registration publishes initial
    /// OperatorStatus Waiting for this connection generation. Invalid payload audits and returns
    /// HelloResult failure without registering.
    /// </remarks>
    private async Task HandleHelloAsync(
        ConnectorConnection connection,
        ProtocolFrame frame,
        CancellationToken cancellationToken)
    {
        HelloMessage hello;
        try
        {
            hello = ControlMessageSerializer.DeserializeHello(frame.Payload);
        }
        catch (ProtocolInvalidPayloadException ex)
        {
            _logger.LogWarning(
                "Invalid Hello rejected. ConnectionId={ConnectionId}, Reason={Reason}",
                connection.ConnectionId,
                ex.Message);
            _audit.TryWrite(
                AuditEventType.ConnectorRegistrationRejected,
                AuditEventOutcome.Failure,
                "Connector Hello rejected",
                new AuditEventContext
                {
                    ConnectorIdentity = AbbreviateIdentity(connection.CertificateThumbprint),
                    RemoteEndpoint = connection.RemoteEndPoint
                });
            await connection.Protocol.WriteHelloResultAsync(
                    new HelloResultMessage
                    {
                        Success = false,
                        Error = ex.Message,
                        CorrelationId = null
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var registration = _sessionRegistry.TryRegister(
            connectorIdentity: connection.CertificateThumbprint,
            connectionId: connection.ConnectionId,
            remoteEndpoint: connection.RemoteEndPoint,
            hostname: hello.Hostname,
            applicationVersion: hello.ApplicationVersion,
            portName: hello.PortName,
            tlsProtocol: connection.NegotiatedProtocol.ToString(),
            connectedAtUtc: now);

        if (!registration.Succeeded)
        {
            _logger.LogWarning(
                "Connector registration rejected. ConnectionId={ConnectionId}, IdentityPrefix={IdentityPrefix}…, Reason={Reason}",
                connection.ConnectionId,
                AbbreviateIdentity(connection.CertificateThumbprint),
                registration.Error);
            var duplicate = registration.Status == ConnectorSessionRegistrationStatus.DuplicateIdentity;
            _audit.TryWrite(
                AuditEventType.ConnectorRegistrationRejected,
                duplicate ? AuditEventOutcome.Denied : AuditEventOutcome.Failure,
                duplicate
                    ? "Duplicate connector identity rejected"
                    : "Connector registration rejected",
                new AuditEventContext
                {
                    ConnectorIdentity = AbbreviateIdentity(connection.CertificateThumbprint),
                    RemoteEndpoint = connection.RemoteEndPoint,
                    Details = hello.Hostname is { } rejectedHost
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["hostname"] = rejectedHost
                        }
                        : null
                });
            await connection.Protocol.WriteHelloResultAsync(
                    new HelloResultMessage
                    {
                        Success = false,
                        Error = registration.Error,
                        CorrelationId = hello.CorrelationId
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var session = registration.Session!;
        connection.RegisteredSessionId = session.SessionId;
        connection.RegisteredSession = session;

        // Seed display cache from Hello-reported settings (connector-authoritative at registration).
        if (hello.BaudRate >= 1)
        {
            _serialConfigCache.Set(new CachedSerialConfiguration
            {
                SessionId = session.SessionId,
                PortName = session.PortName,
                BaudRate = hello.BaudRate,
                DataBits = hello.DataBits,
                Parity = hello.Parity,
                StopBits = hello.StopBits,
                Handshake = hello.Handshake,
                ReportedAtUtc = now
            });
        }

        _logger.LogInformation(
            "Connector registered. SessionId={SessionId}, IdentityPrefix={IdentityPrefix}…, Hostname={Hostname}, Version={Version}, Port={PortName}, Tls={TlsProtocol}, Remote={RemoteEndpoint}, ConnectionId={ConnectionId}",
            session.SessionId,
            AbbreviateIdentity(session.ConnectorIdentity),
            session.Hostname,
            session.ApplicationVersion,
            session.PortName,
            session.TlsProtocol,
            session.RemoteEndpoint,
            connection.ConnectionId);

        _audit.TryWrite(
            AuditEventType.ConnectorRegistered,
            AuditEventOutcome.Success,
            "Connector authenticated and registered",
            new AuditEventContext
            {
                SessionId = session.SessionId,
                ConnectorIdentity = AbbreviateIdentity(session.ConnectorIdentity),
                RemoteEndpoint = session.RemoteEndpoint,
                Details = session.Hostname is { } hostname
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["hostname"] = hostname
                    }
                    : null
            });

        await connection.Protocol.WriteHelloResultAsync(
                new HelloResultMessage
                {
                    Success = true,
                    ServerName = "TechConnect.Server",
                    SessionId = session.SessionId,
                    CorrelationId = hello.CorrelationId
                },
                cancellationToken)
            .ConfigureAwait(false);

        // Initial NOC status for this connection generation — never inherits prior attachment.
        await connection.OperatorStatus.PublishAsync(
                attached: false,
                terminalActive: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the serial configuration cache from a connector SerialConfiguration frame.
    /// </summary>
    /// <param name="connection">Source connection; ignored when not yet registered.</param>
    /// <param name="frame">SerialConfiguration frame.</param>
    /// <remarks>Invalid payloads are logged and ignored; the connection stays up.</remarks>
    private void HandleSerialConfiguration(ConnectorConnection connection, ProtocolFrame frame)
    {
        var sessionId = connection.RegisteredSessionId;
        if (sessionId is null)
        {
            return;
        }

        SerialConfigurationMessage config;
        try
        {
            config = ControlMessageSerializer.DeserializeSerialConfiguration(frame.Payload);
        }
        catch (ProtocolInvalidPayloadException ex)
        {
            _logger.LogWarning(
                "Invalid SerialConfiguration ignored. ConnectionId={ConnectionId}, Reason={Reason}",
                connection.ConnectionId,
                ex.Message);
            return;
        }

        _sessionRegistry.TryGetBySessionId(sessionId, out var session);
        _serialConfigCache.Set(new CachedSerialConfiguration
        {
            SessionId = sessionId,
            PortName = session?.PortName,
            BaudRate = config.BaudRate,
            DataBits = config.DataBits,
            Parity = config.Parity,
            StopBits = config.StopBits,
            Handshake = config.Handshake,
            ReportedAtUtc = _timeProvider.GetUtcNow()
        });
    }

    /// <summary>
    /// Completes a pending SetBaud for the attached operator when correlation matches.
    /// </summary>
    /// <param name="connection">Source connection.</param>
    /// <param name="frame">SetBaudResult frame.</param>
    /// <remarks>
    /// Requires registration and an attachment. Successful results with Config also refresh
    /// the serial cache. Late/unmatched correlation IDs are ignored. Does not detach or
    /// terminate the connector on unmatched results.
    /// </remarks>
    private void HandleSetBaudResult(ConnectorConnection connection, ProtocolFrame frame)
    {
        var sessionId = connection.RegisteredSessionId;
        if (sessionId is null)
        {
            return;
        }

        SetBaudResultMessage result;
        try
        {
            result = ControlMessageSerializer.DeserializeSetBaudResult(frame.Payload);
        }
        catch (ProtocolInvalidPayloadException ex)
        {
            _logger.LogWarning(
                "Invalid SetBaudResult ignored. ConnectionId={ConnectionId}, Reason={Reason}",
                connection.ConnectionId,
                ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.CorrelationId))
        {
            _logger.LogDebug(
                "SetBaudResult without correlation id ignored. ConnectionId={ConnectionId}",
                connection.ConnectionId);
            return;
        }

        if (!_attachmentManager.TryGetBySessionId(sessionId, out var attachment) || attachment is null)
        {
            return;
        }

        CachedSerialSnapshot? snapshot = null;
        if (result is { Success: true, Config: not null })
        {
            snapshot = new CachedSerialSnapshot
            {
                BaudRate = result.Config.BaudRate,
                DataBits = result.Config.DataBits,
                Parity = result.Config.Parity,
                StopBits = result.Config.StopBits,
                Handshake = result.Config.Handshake
            };

            _sessionRegistry.TryGetBySessionId(sessionId, out var session);
            _serialConfigCache.Set(new CachedSerialConfiguration
            {
                SessionId = sessionId,
                PortName = session?.PortName,
                BaudRate = result.Config.BaudRate,
                DataBits = result.Config.DataBits,
                Parity = result.Config.Parity,
                StopBits = result.Config.StopBits,
                Handshake = result.Config.Handshake,
                ReportedAtUtc = _timeProvider.GetUtcNow()
            });
        }

        var completed = _pendingBaudRequests.TryComplete(
            result.CorrelationId,
            attachment.OperatorSessionId,
            new PendingBaudResult
            {
                Success = result.Success,
                Error = result.Error,
                EffectiveBaudRate = result.Config?.BaudRate,
                Config = snapshot
            });

        if (!completed)
        {
            _logger.LogDebug(
                "Late or unmatched SetBaudResult ignored. CorrelationId={CorrelationId}, SessionId={SessionId}",
                result.CorrelationId,
                sessionId);
        }
    }

    /// <summary>
    /// Completes a pending SessionPing when the correlation ID matches.
    /// </summary>
    /// <param name="connection">Source connection.</param>
    /// <param name="frame">SessionPingResult frame.</param>
    /// <remarks>Unregistered connections, invalid payloads, and late correlations are ignored.</remarks>
    private void HandleSessionPingResult(ConnectorConnection connection, ProtocolFrame frame)
    {
        var sessionId = connection.RegisteredSessionId;
        if (sessionId is null)
        {
            return;
        }

        SessionPingResultMessage result;
        try
        {
            result = ControlMessageSerializer.DeserializeSessionPingResult(frame.Payload);
        }
        catch (ProtocolInvalidPayloadException ex)
        {
            _logger.LogWarning(
                "Invalid SessionPingResult ignored. ConnectionId={ConnectionId}, Reason={Reason}",
                connection.ConnectionId,
                ex.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.CorrelationId))
        {
            return;
        }

        var completed = _sessionPingTracker.TryComplete(
            result.CorrelationId,
            new PendingSessionPingResult
            {
                Success = result.Success,
                Error = result.Error,
                Hostname = result.Hostname
            });

        if (!completed)
        {
            _logger.LogDebug(
                "Late or unmatched SessionPingResult ignored. CorrelationId={CorrelationId}, SessionId={SessionId}",
                result.CorrelationId,
                sessionId);
        }
    }

    /// <summary>
    /// Formats a thumbprint for logs as first4…last4 (or unchanged when short/empty).
    /// </summary>
    /// <param name="thumbprint">Full certificate thumbprint.</param>
    /// <returns>Abbreviated display form; empty string when input is null/empty.</returns>
    private static string AbbreviateIdentity(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint))
        {
            return string.Empty;
        }

        return thumbprint.Length <= 8
            ? thumbprint
            : thumbprint[..4] + "…" + thumbprint[^4..];
    }
}
