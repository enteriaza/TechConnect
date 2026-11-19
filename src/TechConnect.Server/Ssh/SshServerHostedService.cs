// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshServerHostedService.cs
//
// Purpose:
// Hosted SSH listener lifecycle: host key bind, auth (password/public-key),
// PTY/window-change/shell channel handling, CLI session spawn, and shutdown.
//
// -----------------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration;
using TechConnect.Server.Connector;
using TechConnect.Server.Copy;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Inspection;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Ssh;

/// <summary>
/// BackgroundService that owns the TechConnect SSH listener and shell CLI sessions.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: when <see cref="SshServerOptions.Enabled"/> is true, loads the host key,
/// accepts on the configured bind address/port, and stays alive until host stop.
/// Shutdown closes tracked sessions, disposes the server, and awaits the accept loop
/// within <see cref="SshServerOptions.ShutdownTimeoutSeconds"/>.
/// </para>
/// <para>
/// Authentication: advertises only methods that
/// <see cref="OperatorAuthenticationService"/> reports as available. Public-key query
/// only advertises key acceptance; full ClientPublicKey or ClientPassword is required
/// for a real principal. Max attempts:
/// <see cref="MaxClientAuthenticationAttempts"/>.
/// </para>
/// <para>
/// Channels: accepts pty-req (records <see cref="SshTerminalInfo"/>; no OS PTY),
/// window-change (updates dimensions), and shell (spawns <see cref="TechConnectCliSession"/>).
/// Rejects exec/command. Concurrent sessions are limited by
/// <see cref="SshServerOptions.MaximumConcurrentConnections"/>.
/// </para>
/// <para>
/// Terminal vs CLI vs protocol: SSH channel bytes are the operator CLI / serial terminal
/// path. Connector device data uses the separate TechConnect binary framing protocol
/// (see Protocol <c>FrameWriter</c> / <c>ProtocolConstants</c>), not this SSH channel.
/// Ctrl+] detach is handled inside the CLI session, not here.
/// </para>
/// </remarks>
internal sealed class SshServerHostedService : BackgroundService
{
    /// <summary>DevTunnels max client authentication attempts (password/public-key budget).</summary>
    internal const int MaxClientAuthenticationAttempts = 3;

    /// <summary>Bound TechConnect server options.</summary>
    private readonly IOptions<TechConnectServerOptions> _options;

    /// <summary>Lazy SSH host key provider.</summary>
    private readonly SshHostKeyProvider _hostKeyProvider;

    /// <summary>Operator password/public-key authentication facade.</summary>
    private readonly OperatorAuthenticationService _authentication;

    /// <summary>CLI command dispatcher injected into each shell session.</summary>
    private readonly TechConnectCliDispatcher _cliDispatcher;

    /// <summary>Operator-to-connector attachment manager.</summary>
    private readonly OperatorAttachmentManager _attachmentManager;

    /// <summary>Registry of active connector sessions.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Tracker of live connector TCP connections.</summary>
    private readonly ConnectorConnectionTracker _connectionTracker;

    /// <summary>Cached serial configuration reported by connectors.</summary>
    private readonly ConnectorSerialConfigurationCache _serialConfigCache;

    /// <summary>Pending baud-change request tracker.</summary>
    private readonly PendingBaudRequestTracker _pendingBaudRequests;

    /// <summary>Pending session-ping correlation tracker.</summary>
    private readonly PendingSessionPingTracker _sessionPingTracker;

    /// <summary>ICMP echo client for ping.</summary>
    private readonly IIcmpEchoClient _icmpEchoClient;

    /// <summary>ICMP traceroute client.</summary>
    private readonly IIcmpTracerouteClient _tracerouteClient;

    /// <summary>Local IPv4/IPv6 interface availability helper.</summary>
    private readonly IIPv6NetworkAvailability _ipv6Availability;

    /// <summary>DNS lookup service for nslookup/CLI.</summary>
    private readonly IDnsLookupService _dnsLookup;

    /// <summary>Configured DNS resolver.</summary>
    private readonly TechConnect.Server.NameResolution.IDnsResolver _dnsResolver;

    /// <summary>Destination resolver for ping/traceroute targets.</summary>
    private readonly IDestinationResolver _destinationResolver;

    /// <summary>CLI command authorization service.</summary>
    private readonly OperatorAuthorizationService _authorization;

    /// <summary>Administrative connector disconnect service.</summary>
    private readonly ConnectorDisconnectService _connectorDisconnect;

    /// <summary>Enable-secret privilege elevation service.</summary>
    private readonly EnablePrivilegeService _enablePrivilege;

    /// <summary>SSH transport + CLI presence tracker.</summary>
    private readonly SshOperatorSessionTracker _sessionTracker;

    /// <summary>Audit event writer.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Hosted-service logger.</summary>
    private readonly ILogger<SshServerHostedService> _logger;

    /// <summary>Logger factory for CLI and optional fallback clients.</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Factory for copy file sources.</summary>
    private readonly ICopyFileSourceFactory _copySources;

    /// <summary>Copy transfer orchestration service.</summary>
    private readonly CopyTransferService _copyTransfer;

    /// <summary>Registry of in-flight copy operations.</summary>
    private readonly ActiveCopyRegistry _activeCopies;

    /// <summary>Active DevTunnels SSH server instance, if running.</summary>
    private SshServer? _server;

    /// <summary>Accept-loop task for the SSH server.</summary>
    private Task? _acceptTask;

    /// <summary>Linked CTS cancelled when the hosted service run loop ends.</summary>
    private CancellationTokenSource? _runCts;

    /// <summary>Stopwatch ticks when each session opened (auth latency diagnostics).</summary>
    private readonly ConcurrentDictionary<SshServerSession, long> _sessionOpenedTicks = new();

    /// <summary>Per-channel PTY info keyed by SSH channel id.</summary>
    private readonly ConcurrentDictionary<uint, SshTerminalInfo> _terminals = new();

    /// <summary>
    /// Initializes the SSH hosted service with required and optional dependencies.
    /// </summary>
    /// <remarks>
    /// Optional parameters default to production implementations (or no-op audit) so
    /// unit tests can construct a minimal graph without the full DI container.
    /// </remarks>
    /// <param name="options">Bound TechConnect options.</param>
    /// <param name="hostKeyProvider">Host key provider.</param>
    /// <param name="authentication">Operator authentication service.</param>
    /// <param name="cliDispatcher">CLI dispatcher.</param>
    /// <param name="attachmentManager">Attachment manager.</param>
    /// <param name="sessionRegistry">Connector session registry.</param>
    /// <param name="connectionTracker">Connector connection tracker.</param>
    /// <param name="serialConfigCache">Serial configuration cache.</param>
    /// <param name="authorization">Authorization service.</param>
    /// <param name="connectorDisconnect">Connector disconnect service.</param>
    /// <param name="enablePrivilege">Enable privilege service.</param>
    /// <param name="sessionTracker">SSH session tracker.</param>
    /// <param name="logger">Service logger.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="audit">Optional audit writer; defaults to no-op.</param>
    /// <param name="sessionPingTracker">Optional session ping tracker.</param>
    /// <param name="icmpEchoClient">Optional ICMP echo client.</param>
    /// <param name="ipv6Availability">Optional IPv6 availability helper.</param>
    /// <param name="tracerouteClient">Optional traceroute client.</param>
    /// <param name="dnsLookup">Optional DNS lookup service.</param>
    /// <param name="dnsResolver">Optional DNS resolver.</param>
    /// <param name="destinationResolver">Optional destination resolver.</param>
    /// <param name="copySources">Optional copy source factory.</param>
    /// <param name="copyTransfer">Optional copy transfer service.</param>
    /// <param name="activeCopies">Optional active copy registry.</param>
    /// <param name="pendingBaudRequests">Pending baud request tracker.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public SshServerHostedService(
        IOptions<TechConnectServerOptions> options,
        SshHostKeyProvider hostKeyProvider,
        OperatorAuthenticationService authentication,
        TechConnectCliDispatcher cliDispatcher,
        OperatorAttachmentManager attachmentManager,
        ConnectorSessionRegistry sessionRegistry,
        ConnectorConnectionTracker connectionTracker,
        ConnectorSerialConfigurationCache serialConfigCache,
        PendingBaudRequestTracker pendingBaudRequests,
        OperatorAuthorizationService authorization,
        ConnectorDisconnectService connectorDisconnect,
        EnablePrivilegeService enablePrivilege,
        SshOperatorSessionTracker sessionTracker,
        ILogger<SshServerHostedService> logger,
        ILoggerFactory loggerFactory,
        IAuditEventWriter? audit = null,
        PendingSessionPingTracker? sessionPingTracker = null,
        IIcmpEchoClient? icmpEchoClient = null,
        IIPv6NetworkAvailability? ipv6Availability = null,
        IIcmpTracerouteClient? tracerouteClient = null,
        IDnsLookupService? dnsLookup = null,
        TechConnect.Server.NameResolution.IDnsResolver? dnsResolver = null,
        IDestinationResolver? destinationResolver = null,
        ICopyFileSourceFactory? copySources = null,
        CopyTransferService? copyTransfer = null,
        ActiveCopyRegistry? activeCopies = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostKeyProvider = hostKeyProvider ?? throw new ArgumentNullException(nameof(hostKeyProvider));
        _authentication = authentication ?? throw new ArgumentNullException(nameof(authentication));
        _cliDispatcher = cliDispatcher ?? throw new ArgumentNullException(nameof(cliDispatcher));
        _attachmentManager = attachmentManager ?? throw new ArgumentNullException(nameof(attachmentManager));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _connectionTracker = connectionTracker ?? throw new ArgumentNullException(nameof(connectionTracker));
        _serialConfigCache = serialConfigCache ?? throw new ArgumentNullException(nameof(serialConfigCache));
        _sessionPingTracker = sessionPingTracker ?? new PendingSessionPingTracker();
        _ipv6Availability = ipv6Availability ?? new SystemIPv6NetworkAvailability();
        _icmpEchoClient = icmpEchoClient ?? new RawSocketIcmpEchoClient(
            loggerFactory.CreateLogger<RawSocketIcmpEchoClient>());
        _tracerouteClient = tracerouteClient ?? new RawSocketIcmpTracerouteClient(
            loggerFactory.CreateLogger<RawSocketIcmpTracerouteClient>());
        _dnsResolver = dnsResolver ?? new TechConnect.Server.NameResolution.ConfiguredDnsResolver(
            [],
            TimeSpan.FromSeconds(3),
            loggerFactory.CreateLogger<TechConnect.Server.NameResolution.ConfiguredDnsResolver>());
        _dnsLookup = dnsLookup ?? new ConfiguredDnsLookupService(_dnsResolver);
        _destinationResolver = destinationResolver ?? new DestinationResolver(_dnsResolver, _ipv6Availability);
        _pendingBaudRequests = pendingBaudRequests ?? throw new ArgumentNullException(nameof(pendingBaudRequests));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _connectorDisconnect = connectorDisconnect ?? throw new ArgumentNullException(nameof(connectorDisconnect));
        _enablePrivilege = enablePrivilege ?? throw new ArgumentNullException(nameof(enablePrivilege));
        _sessionTracker = sessionTracker ?? throw new ArgumentNullException(nameof(sessionTracker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _audit = audit ?? new NoOpAuditEventWriter();
        _activeCopies = activeCopies ?? new ActiveCopyRegistry();
        _copyTransfer = copyTransfer ?? new CopyTransferService(
            _activeCopies,
            loggerFactory.CreateLogger<CopyTransferService>());
        _copySources = copySources ?? new CopyFileSourceFactory(loggerFactory);
    }

    /// <summary>
    /// Starts the SSH accept loop when enabled, then waits until host cancellation.
    /// </summary>
    /// <param name="stoppingToken">Host stop token.</param>
    /// <returns>A task that completes when the service is stopping.</returns>
    /// <exception cref="InvalidOperationException">Configured listen address is not a valid IP.</exception>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ssh = _options.Value.Ssh;
        if (!ssh.Enabled)
        {
            _logger.LogInformation("SSH listener is disabled by configuration");
            return;
        }

        var hostKey = _hostKeyProvider.GetHostKey();

        if (!IPAddress.TryParse(ssh.ListenAddress, out var listenAddress))
        {
            throw new InvalidOperationException(
                $"SSH listen address '{ssh.ListenAddress}' is invalid.");
        }

        var config = CreateSessionConfiguration();
        var trace = new TraceSource(nameof(SshServer), SourceLevels.Warning);
        var server = new TechConnectSshServer(config, trace)
        {
            Credentials = new SshServerCredentials(hostKey)
        };

        server.SessionOpened += OnSessionOpened;
        server.SessionAuthenticating += OnSessionAuthenticating;
        server.ChannelOpening += OnChannelOpening;
        server.ChannelRequest += OnChannelRequest;
        server.ExceptionRaised += OnExceptionRaised;

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _runCts = runCts;

        _server = server;
        _acceptTask = server.AcceptSessionsAsync(ssh.Port, listenAddress);

        _logger.LogInformation(
            "SSH listener started on {ListenAddress}:{Port} (host fingerprint {Fingerprint}; password={PasswordEnabled}, publickey={PublicKeyEnabled})",
            listenAddress,
            ssh.Port,
            _hostKeyProvider.Fingerprint,
            _authentication.PasswordAuthenticationAvailable,
            _authentication.PublicKeyAuthenticationAvailable);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            try
            {
                runCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _runCts = null;
        }
    }

    /// <summary>
    /// Closes active sessions, disposes the SSH server, and awaits the accept loop within the configured timeout.
    /// </summary>
    /// <param name="cancellationToken">Stop cancellation token.</param>
    /// <returns>A task that completes when shutdown work finishes.</returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Value.Ssh.ShutdownTimeoutSeconds));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        _logger.LogInformation("SSH listener shutting down (timeout {TimeoutSeconds}s)", timeout.TotalSeconds);

        try
        {
            await _sessionTracker.CloseAllAsync(timeout, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while closing active SSH sessions during shutdown");
        }

        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
            try
            {
                server.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing SSH server during shutdown");
            }
        }

        var acceptTask = Interlocked.Exchange(ref _acceptTask, null);
        if (acceptTask is not null)
        {
            try
            {
                await acceptTask.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "SSH accept loop did not complete within {TimeoutSeconds}s; continuing shutdown",
                    timeout.TotalSeconds);
            }
            catch (ObjectDisposedException ex)
            {
                _logger.LogDebug(ex, "SSH accept loop disposed during shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SSH accept loop ended with an error during shutdown");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SSH listener stopped");
    }

    /// <summary>
    /// Builds DevTunnels session configuration with only available auth methods and attempt budget.
    /// </summary>
    /// <returns>Configured <see cref="SshSessionConfiguration"/>.</returns>
    private SshSessionConfiguration CreateSessionConfiguration()
    {
        var config = new SshSessionConfiguration();
        config.AuthenticationMethods.Clear();

        if (_authentication.PublicKeyAuthenticationAvailable)
        {
            config.AuthenticationMethods.Add(AuthenticationMethods.PublicKey);
        }

        if (_authentication.PasswordAuthenticationAvailable)
        {
            config.AuthenticationMethods.Add(AuthenticationMethods.Password);
        }

        config.MaxClientAuthenticationAttempts = MaxClientAuthenticationAttempts;
        return config;
    }

    /// <summary>
    /// Handles SessionOpened: enforces connection limits and registers the session with its remote endpoint.
    /// </summary>
    /// <param name="sender">Expected <see cref="TechConnectSshServer"/>.</param>
    /// <param name="session">Newly opened SSH session.</param>
    private void OnSessionOpened(object? sender, SshServerSession session)
    {
        var max = _options.Value.Ssh.MaximumConcurrentConnections;
        if (_sessionTracker.Count >= max)
        {
            _logger.LogWarning(
                "Rejecting SSH session: concurrent connection limit {Maximum} reached",
                max);
            _ = CloseSessionSoonAsync(session, SshDisconnectReason.TooManyConnections, "Too many connections.");
            return;
        }

        var remoteEndpoint = (sender as TechConnectSshServer)?.TakePendingRemoteEndpoint();
        if (!_sessionTracker.TryAdd(session, remoteEndpoint))
        {
            return;
        }

        var openedTicks = Stopwatch.GetTimestamp();
        _sessionOpenedTicks[session] = openedTicks;

        session.Closed += (_, _) =>
        {
            _sessionTracker.Remove(session);
            _sessionOpenedTicks.TryRemove(session, out _);
            _logger.LogInformation("SSH session closed");
        };

        _logger.LogInformation(
            "SSH session opened from {RemoteEndpoint} ({ActiveCount} active); protocol negotiation complete",
            remoteEndpoint ?? "(unknown)",
            _sessionTracker.Count);
    }

    /// <summary>
    /// Assigns the authentication task for an incoming SSH authenticating event.
    /// </summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Authentication event args.</param>
    private void OnSessionAuthenticating(object? sender, SshAuthenticatingEventArgs e)
    {
        e.AuthenticationTask = AuthenticateAsync(sender, e);
    }

    /// <summary>
    /// Dispatches authentication by SSH authentication type.
    /// </summary>
    /// <param name="sender">Event sender (may be the session).</param>
    /// <param name="e">Authentication event args.</param>
    /// <returns>Authenticated principal, or <see langword="null"/> to reject.</returns>
    private Task<ClaimsPrincipal?> AuthenticateAsync(object? sender, SshAuthenticatingEventArgs e)
    {
        return e.AuthenticationType switch
        {
            SshAuthenticationType.ClientPublicKeyQuery => Task.FromResult(AuthenticatePublicKeyQuery(e)),
            SshAuthenticationType.ClientPublicKey => Task.FromResult(AuthenticatePublicKey(e)),
            SshAuthenticationType.ClientPassword => AuthenticatePasswordAsync(sender, e),
            _ => RejectUnsupported(e.AuthenticationType)
        };
    }

    /// <summary>
    /// Rejects authentication methods that are not password or public-key.
    /// </summary>
    /// <param name="method">Unsupported authentication type.</param>
    /// <returns>Always <see langword="null"/> (reject).</returns>
    private Task<ClaimsPrincipal?> RejectUnsupported(SshAuthenticationType method)
    {
        _logger.LogWarning("SSH authentication rejected: method {Method} is not permitted", method);
        return Task.FromResult<ClaimsPrincipal?>(null);
    }

    /// <summary>
    /// Handles public-key query: advertises key acceptance without creating a full operator principal.
    /// </summary>
    /// <param name="e">Authentication event args.</param>
    /// <returns>Query-only principal on success; <see langword="null"/> on reject.</returns>
    private ClaimsPrincipal? AuthenticatePublicKeyQuery(SshAuthenticatingEventArgs e)
    {
        if (e.PublicKey is null)
        {
            return null;
        }

        var ok = _authentication.TryAuthenticatePublicKeyQuery(e.Username, e.PublicKey, out var fingerprint);
        if (!ok)
        {
            _logger.LogInformation(
                "SSH public-key query rejected for user {Username} (fingerprint {Fingerprint})",
                e.Username ?? "(unknown)",
                fingerprint);
            return null;
        }

        // Query success only advertises key acceptance; full ClientPublicKey still required.
        return new ClaimsPrincipal(new ClaimsIdentity("ssh-publickey-query"));
    }

    /// <summary>
    /// Completes public-key authentication into an operator claims principal.
    /// </summary>
    /// <param name="e">Authentication event args.</param>
    /// <returns>Operator principal, or <see langword="null"/> on failure.</returns>
    private ClaimsPrincipal? AuthenticatePublicKey(SshAuthenticatingEventArgs e)
    {
        if (e.PublicKey is null)
        {
            _logger.LogWarning("SSH authentication rejected: public key was missing");
            return null;
        }

        var identity = _authentication.AuthenticatePublicKey(e.Username, e.PublicKey);
        return identity is null ? null : AuthenticatedOperatorClaims.CreatePrincipal(identity);
    }

    /// <summary>
    /// Completes password authentication (local and/or RADIUS per configuration).
    /// </summary>
    /// <param name="sender">Event sender used for session-open latency logging.</param>
    /// <param name="e">Authentication event args.</param>
    /// <returns>Operator principal, or <see langword="null"/> on failure.</returns>
    private async Task<ClaimsPrincipal?> AuthenticatePasswordAsync(object? sender, SshAuthenticatingEventArgs e)
    {
        var sw = Stopwatch.StartNew();
        var sinceOpenMs = TryGetMsSinceSessionOpened(sender as SshServerSession);
        _logger.LogInformation(
            "SSH password authentication callback entered for {Username} ({MsSinceSessionOpen}ms since session open)",
            e.Username ?? "(unknown)",
            sinceOpenMs?.ToString() ?? "n/a");

        var identity = await _authentication
            .AuthenticatePasswordAsync(e.Username, e.Password, e.Cancellation)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "SSH password authentication callback completed for {Username} in {ElapsedMs}ms success={Success}",
            e.Username ?? "(unknown)",
            sw.ElapsedMilliseconds,
            identity is not null);

        return identity is null ? null : AuthenticatedOperatorClaims.CreatePrincipal(identity);
    }

    /// <summary>
    /// Computes milliseconds since SessionOpened for diagnostics.
    /// </summary>
    /// <param name="session">SSH session, possibly null.</param>
    /// <returns>Elapsed milliseconds, or <see langword="null"/> when unknown.</returns>
    private double? TryGetMsSinceSessionOpened(SshServerSession? session)
    {
        if (session is null || !_sessionOpenedTicks.TryGetValue(session, out var opened))
        {
            return null;
        }

        return Stopwatch.GetElapsedTime(opened).TotalMilliseconds;
    }

    /// <summary>
    /// Logs channel-opening events (authorization happens on channel requests).
    /// </summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="e">Channel opening args.</param>
    private void OnChannelOpening(object? sender, SshChannelOpeningEventArgs e)
    {
        _logger.LogDebug("SSH channel opening ({ChannelType})", e.Channel.ChannelType);
    }

    /// <summary>
    /// Authorizes pty-req, window-change, and shell; rejects exec/command.
    /// </summary>
    /// <remarks>
    /// Shell spawns <see cref="RunCliAsync"/> on a background task. PTY acceptance
    /// records terminal info only — no OS terminal is allocated.
    /// </remarks>
    /// <param name="sender">Expected <see cref="SshChannel"/>.</param>
    /// <param name="e">Channel request event args.</param>
    private void OnChannelRequest(object? sender, SshRequestEventArgs<ChannelRequestMessage> e)
    {
        e.IsAuthorized = false;

        if (sender is not SshChannel channel)
        {
            return;
        }

        if (e.RequestType == ChannelRequestTypes.Terminal)
        {
            if (e.Principal is null)
            {
                return;
            }

            e.IsAuthorized = true;
            if (e.Request is TerminalRequestMessage pty)
            {
                var info = new SshTerminalInfo(pty.Term, (int)pty.Columns, (int)pty.Rows);
                _terminals[channel.ChannelId] = info;
                _logger.LogInformation(
                    "Accepted SSH PTY request term={Term} cols={Columns} rows={Rows} (no OS terminal allocated)",
                    info.Term,
                    info.Columns,
                    info.Rows);
            }
            else
            {
                _logger.LogInformation("Accepted SSH PTY request (no OS terminal allocated)");
            }

            return;
        }

        if (string.Equals(e.RequestType, "window-change", StringComparison.Ordinal))
        {
            e.IsAuthorized = e.Principal is not null;
            if (e.Request is ChannelRequestMessage request &&
                SshWindowChangeParser.TryParse(request, out var columns, out var rows))
            {
                if (!_terminals.TryGetValue(channel.ChannelId, out var existing))
                {
                    existing = new SshTerminalInfo(null, columns, rows);
                    _terminals[channel.ChannelId] = existing;
                    _logger.LogDebug(
                        "Accepted SSH window-change for channel {ChannelId} cols={Columns} rows={Rows}",
                        channel.ChannelId,
                        existing.Columns,
                        existing.Rows);
                }
                else if (existing.TryUpdateDimensions(columns, rows))
                {
                    _logger.LogDebug(
                        "Accepted SSH window-change for channel {ChannelId} term={Term} cols={Columns} rows={Rows}",
                        channel.ChannelId,
                        existing.Term,
                        existing.Columns,
                        existing.Rows);
                }
                else
                {
                    _logger.LogDebug(
                        "Ignored invalid SSH window-change for channel {ChannelId} cols={Columns} rows={Rows}; keeping cols={KeepColumns} rows={KeepRows}",
                        channel.ChannelId,
                        columns,
                        rows,
                        existing.Columns,
                        existing.Rows);
                }
            }
            else
            {
                _logger.LogDebug(
                    "Accepted SSH window-change for channel {ChannelId} (dimensions not parsed)",
                    channel.ChannelId);
            }

            return;
        }

        if (e.RequestType == ChannelRequestTypes.Shell)
        {
            if (e.Principal is null ||
                !AuthenticatedOperatorClaims.TryCreateOperator(e.Principal, out var identity) ||
                identity is null)
            {
                _logger.LogWarning("Rejected SSH shell request: session is not authenticated");
                return;
            }

            e.IsAuthorized = true;
            var runToken = _runCts?.Token ?? CancellationToken.None;
            _ = Task.Run(() => RunCliAsync(channel, identity, runToken), CancellationToken.None);
            return;
        }

        if (e.RequestType == ChannelRequestTypes.Command || e.Request is CommandRequestMessage)
        {
            _logger.LogInformation("Rejected SSH exec/command request");
        }
    }

    /// <summary>
    /// Runs the interactive CLI on an authorized shell channel until exit or disconnect.
    /// </summary>
    /// <remarks>
    /// Wraps the channel in <see cref="SshStream"/> and constructs
    /// <see cref="TechConnectCliSession"/>. Always closes the channel and session afterward.
    /// </remarks>
    /// <param name="channel">Authorized shell channel.</param>
    /// <param name="identity">Authenticated operator identity.</param>
    /// <param name="cancellationToken">Run-loop / shutdown token.</param>
    /// <returns>A task that completes when the CLI session ends.</returns>
    private async Task RunCliAsync(
        SshChannel channel,
        AuthenticatedOperator identity,
        CancellationToken cancellationToken)
    {
        var cliLogger = _loggerFactory.CreateLogger("TechConnect.Server.Ssh.Cli");
        try
        {
            var sinceOpenMs = TryGetMsSinceSessionOpened(channel.Session as SshServerSession);
            if (!_terminals.TryGetValue(channel.ChannelId, out var terminal) || terminal is null)
            {
                terminal = new SshTerminalInfo(
                    null,
                    SshTerminalInfo.DefaultColumns,
                    SshTerminalInfo.DefaultRows);
                _terminals[channel.ChannelId] = terminal;
            }

            _logger.LogInformation(
                "SSH shell channel established for {Operator} session={SessionId} term={Term} cols={Columns} rows={Rows} ({MsSinceSessionOpen}ms since session open)",
                identity.Username,
                identity.OperatorSessionId,
                terminal.Term ?? "(none)",
                terminal.Columns,
                terminal.Rows,
                sinceOpenMs?.ToString() ?? "n/a");
            await using var stream = new SshStream(channel);
            string? remoteEndpoint = null;
            if (channel.Session is SshServerSession sshSession)
            {
                _sessionTracker.TryGetRemoteEndpoint(sshSession, out remoteEndpoint);
            }

            var session = new TechConnectCliSession(
                stream,
                _cliDispatcher,
                _attachmentManager,
                _sessionRegistry,
                _connectionTracker,
                _serialConfigCache,
                _pendingBaudRequests,
                _authorization,
                _connectorDisconnect,
                cliLogger,
                identity,
                _enablePrivilege,
                terminal,
                _audit,
                _sessionPingTracker,
                _icmpEchoClient,
                _ipv6Availability,
                _tracerouteClient,
                _dnsLookup,
                _dnsResolver,
                _destinationResolver,
                _sessionTracker,
                remoteEndpoint,
                _copySources,
                _copyTransfer,
                _activeCopies,
                _loggerFactory);
            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("SSH CLI session cancelled during shutdown for {Operator}", identity.Username);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SshChannelException or SshConnectionException)
        {
            _logger.LogDebug(ex, "SSH CLI session ended for {Operator}", identity.Username);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error in SSH CLI session for {Operator}", identity.Username);
        }
        finally
        {
            _terminals.TryRemove(channel.ChannelId, out _);

            try
            {
                if (!channel.IsClosed)
                {
                    await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing SSH channel for {Operator}", identity.Username);
            }

            try
            {
                if (!channel.Session.IsClosed)
                {
                    await channel.Session.CloseAsync(SshDisconnectReason.ByApplication, "CLI session ended.")
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing SSH session after CLI exit for {Operator}", identity.Username);
            }

            _logger.LogInformation("SSH shell channel closed for {Operator}", identity.Username);
        }
    }

    /// <summary>
    /// Logs SSH server connection exceptions, treating shutdown races as debug.
    /// </summary>
    /// <param name="sender">Event sender.</param>
    /// <param name="exception">Raised exception.</param>
    private void OnExceptionRaised(object? sender, Exception exception)
    {
        if (exception is SshConnectionException sshEx &&
            sshEx.Message.Contains("shutting down", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(exception, "SSH connection closed during application shutdown");
            return;
        }

        _logger.LogWarning(exception, "SSH server reported a connection error");
    }

    /// <summary>
    /// Best-effort async close used when rejecting sessions over the concurrent connection limit.
    /// </summary>
    /// <param name="session">Session to close.</param>
    /// <param name="reason">SSH disconnect reason.</param>
    /// <param name="message">Disconnect message.</param>
    /// <returns>A task that completes when close/dispose finishes.</returns>
    private static async Task CloseSessionSoonAsync(
        SshServerSession session,
        SshDisconnectReason reason,
        string message)
    {
        try
        {
            await session.CloseAsync(reason, message).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                session.Dispose();
            }
            catch
            {
                // Ignored.
            }
        }
    }
}