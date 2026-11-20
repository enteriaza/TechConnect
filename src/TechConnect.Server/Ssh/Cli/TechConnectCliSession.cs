// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TechConnectCliSession.cs
//
// Purpose:
// SSH operator CLI loop and serial terminal mode including Ctrl+] detach,
// enable/disable, connect/attach, and diagnostics.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.Extensions.Logging;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Connector;
using TechConnect.Server.Copy;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.NameResolution;
using TechConnect.Server.Inspection;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// SSH operator CLI and serial terminal mode.
/// </summary>
/// <remarks>
/// <para>
/// Ctrl+] (<see cref="TechConnectCliDispatcher.DetachControlByte"/> / 0x1D) leaves
/// terminal mode without forwarding that byte; attachment is kept so
/// <c>show serial</c> / <c>attach</c> can run at the privilege prompt.
/// Use <c>disconnect</c> (DisconnectSelf, privilege ≥ 1) to release the attachment.
/// Administrative <c>disconnect &lt;session&gt;</c> requires privilege ≥ 7 per
/// <c>CliCommandPolicy</c>. <c>show serial</c> also requires ≥ 7.
/// </para>
/// <para>
/// Active privilege may be raised via <c>enable</c> and restored via <c>disable</c>
/// (both policy minimum 1; enable still requires a configured secret at runtime).
/// Terminal I/O forwards operator bytes and connector serial/protocol frames;
/// CLI text is UTF-8 over the SSH channel and is distinct from TechConnect binary
/// framing used on the connector path.
/// </para>
/// </remarks>
internal sealed class TechConnectCliSession
{
    /// <summary>SSH channel stream (not owned for disposal of the session transport).</summary>
    private readonly Stream _stream;

    /// <summary>Command parser/dispatcher.</summary>
    private readonly TechConnectCliDispatcher _dispatcher;

    /// <summary>Operator attachment manager.</summary>
    private readonly OperatorAttachmentManager _attachmentManager;

    /// <summary>Connector session registry.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Connector connection tracker.</summary>
    private readonly ConnectorConnectionTracker _connectionTracker;

    /// <summary>Serial configuration cache.</summary>
    private readonly ConnectorSerialConfigurationCache _serialConfigCache;

    /// <summary>Pending baud request tracker.</summary>
    private readonly PendingBaudRequestTracker _pendingBaudRequests;

    /// <summary>Pending session-ping tracker.</summary>
    private readonly PendingSessionPingTracker _sessionPingTracker;

    /// <summary>ICMP echo client.</summary>
    private readonly IIcmpEchoClient _icmp;

    /// <summary>ICMP traceroute client.</summary>
    private readonly IIcmpTracerouteClient _traceroute;

    /// <summary>Local address availability helper.</summary>
    private readonly IIPv6NetworkAvailability _ipv6Availability;

    /// <summary>DNS lookup service.</summary>
    private readonly IDnsLookupService _dnsLookup;

    /// <summary>Configured DNS resolver.</summary>
    private readonly IDnsResolver _dnsResolver;

    /// <summary>Destination resolver for diagnostics.</summary>
    private readonly IDestinationResolver _destinationResolver;

    /// <summary>Operator CLI presence tracker.</summary>
    private readonly SshOperatorSessionTracker _operatorSessions;

    /// <summary>Authorization service (policy-backed).</summary>
    private readonly OperatorAuthorizationService _authorization;

    /// <summary>Connector disconnect service.</summary>
    private readonly ConnectorDisconnectService _connectorDisconnect;

    /// <summary>Enable-secret privilege service.</summary>
    private readonly EnablePrivilegeService _enablePrivilege;

    /// <summary>Audit writer.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Session logger.</summary>
    private readonly ILogger _logger;

    /// <summary>Logger factory for nested components.</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Copy source factory.</summary>
    private readonly ICopyFileSourceFactory _copySources;

    /// <summary>Copy transfer service.</summary>
    private readonly CopyTransferService _copyTransfer;

    /// <summary>Active copy registry.</summary>
    private readonly ActiveCopyRegistry _activeCopies;

    /// <summary>Immutable login identity.</summary>
    private readonly AuthenticatedOperator _operator;

    /// <summary>Login privilege (disable restores toward level 1 / original semantics).</summary>
    private readonly PrivilegeLevel _originalPrivilege;

    /// <summary>Effective privilege for authorization and prompt.</summary>
    private PrivilegeLevel _activePrivilege;

    /// <summary>Operator username for logging/audit.</summary>
    private readonly string _operatorIdentity;

    /// <summary>Operator session id.</summary>
    private readonly string _operatorSessionId;

    /// <summary>Live PTY dimensions for this shell channel.</summary>
    private readonly SshTerminalInfo _terminal;

    /// <summary>Tab/? discovery helper.</summary>
    private readonly CliCommandDiscovery _discovery;

    /// <summary>Formatted SSH remote endpoint when known.</summary>
    private readonly string? _remoteEndpoint;

    /// <summary>Serializes SSH channel writes from CLI and terminal pumps.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Interactive line reader for CLI mode.</summary>
    private InteractiveLineReader? _lineReader;

    /// <summary>Temporary prompt override (for example enable password prompt).</summary>
    private string? _linePromptOverride;

    /// <summary>Configuration management (null when the host does not provide it).</summary>
    private readonly ConfigurationManagementService? _configurationManagement;

    /// <summary>Configuration-mode interpreter (null when management is unavailable).</summary>
    private readonly ConfigurationModeProcessor? _configurationProcessor;

    /// <summary>Active private configuration session; null outside configuration mode.</summary>
    private ConfigurationEditSession? _configurationEdit;

    /// <summary>
    /// Initializes a CLI session over an SSH shell channel stream.
    /// </summary>
    /// <param name="stream">SSH channel stream.</param>
    /// <param name="dispatcher">CLI dispatcher.</param>
    /// <param name="attachmentManager">Attachment manager.</param>
    /// <param name="sessionRegistry">Session registry.</param>
    /// <param name="connectionTracker">Connection tracker.</param>
    /// <param name="serialConfigCache">Serial config cache.</param>
    /// <param name="pendingBaudRequests">Pending baud tracker.</param>
    /// <param name="authorization">Authorization service.</param>
    /// <param name="connectorDisconnect">Disconnect service.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="authenticatedOperator">Authenticated operator.</param>
    /// <param name="enablePrivilege">Enable privilege service.</param>
    /// <param name="terminal">Optional PTY info.</param>
    /// <param name="audit">Optional audit writer.</param>
    /// <param name="sessionPingTracker">Optional session ping tracker.</param>
    /// <param name="icmpEchoClient">Optional ICMP client.</param>
    /// <param name="ipv6Availability">Optional IPv6 helper.</param>
    /// <param name="tracerouteClient">Optional traceroute client.</param>
    /// <param name="dnsLookup">Optional DNS lookup.</param>
    /// <param name="dnsResolver">Optional DNS resolver.</param>
    /// <param name="destinationResolver">Optional destination resolver.</param>
    /// <param name="operatorSessions">Optional operator session tracker.</param>
    /// <param name="remoteEndpoint">Optional remote endpoint string.</param>
    /// <param name="copySources">Optional copy source factory.</param>
    /// <param name="copyTransfer">Optional copy transfer service.</param>
    /// <param name="activeCopies">Optional active copy registry.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="configurationManagement">Optional configuration management (enables configuration mode).</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public TechConnectCliSession(
        Stream stream,
        TechConnectCliDispatcher dispatcher,
        OperatorAttachmentManager attachmentManager,
        ConnectorSessionRegistry sessionRegistry,
        ConnectorConnectionTracker connectionTracker,
        ConnectorSerialConfigurationCache serialConfigCache,
        PendingBaudRequestTracker pendingBaudRequests,
        OperatorAuthorizationService authorization,
        ConnectorDisconnectService connectorDisconnect,
        ILogger logger,
        AuthenticatedOperator authenticatedOperator,
        EnablePrivilegeService enablePrivilege,
        SshTerminalInfo? terminal = null,
        IAuditEventWriter? audit = null,
        PendingSessionPingTracker? sessionPingTracker = null,
        IIcmpEchoClient? icmpEchoClient = null,
        IIPv6NetworkAvailability? ipv6Availability = null,
        IIcmpTracerouteClient? tracerouteClient = null,
        IDnsLookupService? dnsLookup = null,
        IDnsResolver? dnsResolver = null,
        IDestinationResolver? destinationResolver = null,
        SshOperatorSessionTracker? operatorSessions = null,
        string? remoteEndpoint = null,
        ICopyFileSourceFactory? copySources = null,
        CopyTransferService? copyTransfer = null,
        ActiveCopyRegistry? activeCopies = null,
        ILoggerFactory? loggerFactory = null,
        ConfigurationManagementService? configurationManagement = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _attachmentManager = attachmentManager ?? throw new ArgumentNullException(nameof(attachmentManager));
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _connectionTracker = connectionTracker ?? throw new ArgumentNullException(nameof(connectionTracker));
        _serialConfigCache = serialConfigCache ?? throw new ArgumentNullException(nameof(serialConfigCache));
        _pendingBaudRequests = pendingBaudRequests ?? throw new ArgumentNullException(nameof(pendingBaudRequests));
        _sessionPingTracker = sessionPingTracker ?? new PendingSessionPingTracker();
        _ipv6Availability = ipv6Availability ?? new SystemIPv6NetworkAvailability();
        _icmp = icmpEchoClient ?? new RawSocketIcmpEchoClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RawSocketIcmpEchoClient>.Instance);
        _traceroute = tracerouteClient ?? new RawSocketIcmpTracerouteClient(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RawSocketIcmpTracerouteClient>.Instance);
        _dnsResolver = dnsResolver ?? new ConfiguredDnsResolver(
            [],
            TimeSpan.FromSeconds(3),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfiguredDnsResolver>.Instance);
        _dnsLookup = dnsLookup ?? new ConfiguredDnsLookupService(_dnsResolver);
        _destinationResolver = destinationResolver ?? new DestinationResolver(_dnsResolver, _ipv6Availability);
        _operatorSessions = operatorSessions ?? new SshOperatorSessionTracker();
        _remoteEndpoint = remoteEndpoint;
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _connectorDisconnect = connectorDisconnect ?? throw new ArgumentNullException(nameof(connectorDisconnect));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
        _activeCopies = activeCopies ?? new ActiveCopyRegistry();
        _copyTransfer = copyTransfer ?? new CopyTransferService(
            _activeCopies,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CopyTransferService>.Instance);
        _copySources = copySources ?? new CopyFileSourceFactory(_loggerFactory);
        _operator = authenticatedOperator ?? throw new ArgumentNullException(nameof(authenticatedOperator));
        _enablePrivilege = enablePrivilege ?? throw new ArgumentNullException(nameof(enablePrivilege));
        _audit = audit ?? new NoOpAuditEventWriter();
        _originalPrivilege = _operator.Privilege;
        _activePrivilege = _originalPrivilege;
        _operatorIdentity = _operator.Username;
        _operatorSessionId = _operator.OperatorSessionId;
        _terminal = terminal ?? new SshTerminalInfo(null, SshTerminalInfo.DefaultColumns, SshTerminalInfo.DefaultRows);
        _discovery = new CliCommandDiscovery(_sessionRegistry, _ipv6Availability);
        _configurationManagement = configurationManagement;
        _configurationProcessor = configurationManagement is null
            ? null
            : new ConfigurationModeProcessor(configurationManagement, _authorization);
    }

    /// <summary>Whether the session is in configuration mode.</summary>
    public bool IsInConfigurationMode => _configurationEdit is not null;

    /// <summary>
    /// Operator session identifier for this CLI session.
    /// </summary>
    public string OperatorSessionId => _operatorSessionId;

    /// <summary>Login identity; privilege is the original (immutable) login level.</summary>
    public AuthenticatedOperator AuthenticatedOperator => _operator;

    /// <summary>Effective privilege for authorization, discovery, and the CLI prompt.</summary>
    public PrivilegeLevel ActivePrivilege => _activePrivilege;

    /// <summary>
    /// Login identity with <see cref="ActivePrivilege"/> substituted for authorization/discovery.
    /// </summary>
    private AuthenticatedOperator ActiveIdentity =>
        _operator with { Privilege = _activePrivilege };

    /// <summary>
    /// Returns the privilege-dependent CLI prompt string.
    /// </summary>
    /// <returns>Operator or administrator prompt.</returns>
    private string GetPrompt() =>
        _configurationEdit is { } edit
            ? ConfigurationModeProcessor.GetPrompt(edit)
            : TechConnectCliDispatcher.GetPrompt(_activePrivilege);

    /// <summary>
    /// Runs the CLI read/dispatch loop until exit, channel close, or cancellation.
    /// </summary>
    /// <remarks>
    /// Registers CLI presence, writes the welcome banner, then reads lines and executes
    /// <see cref="CliCommandResult"/> actions (connect/attach/terminal mode, diagnostics, etc.).
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token from the SSH hosted service run loop.</param>
    /// <returns>A task that completes when the CLI session ends.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lineReader = new InteractiveLineReader(
            _stream,
            WriteRawValueAsync,
            () => _linePromptOverride ?? GetPrompt(),
            (line, cursor) => _configurationEdit is { } edit
                ? _configurationProcessor!.Complete(edit, line, cursor, _terminal.Columns)
                : _discovery.Complete(line, cursor, ActiveIdentity, _terminal.Columns),
            (line, cursor) => _configurationEdit is { } edit
                ? _configurationProcessor!.Help(edit, line, cursor, _terminal.Columns)
                : _discovery.Help(line, cursor, ActiveIdentity, _terminal.Columns),
            () => _terminal.Columns);

        var cliRecord = new OperatorCliSessionRecord
        {
            OperatorSessionId = _operatorSessionId,
            Username = _operatorIdentity,
            LoginPrivilege = _originalPrivilege,
            ActivePrivilege = _activePrivilege,
            RemoteEndpoint = _remoteEndpoint,
            ConnectedAtUtc = _operator.AuthenticatedAtUtc,
            TermType = _terminal.Term,
            Columns = _terminal.Columns,
            Rows = _terminal.Rows
        };
        _operatorSessions.RegisterCliSession(cliRecord);

        await WriteTextAsync(TechConnectCliDispatcher.WelcomeBanner + "\r\n\r\n", cancellationToken)
            .ConfigureAwait(false);
        await WritePromptAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                LineReadResult read;
                try
                {
                    read = await _lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SshConnectionException ex)
                {
                    _logger.LogDebug(
                        ex,
                        "SSH CLI input ended during disconnect/shutdown for {Operator}",
                        _operatorIdentity);
                    break;
                }
                catch (SshChannelException ex)
                {
                    _logger.LogDebug(
                        ex,
                        "SSH CLI channel closed for {Operator}",
                        _operatorIdentity);
                    break;
                }

                if (read.Outcome == LineReadOutcome.Exit)
                {
                    break;
                }

                if (read.Outcome == LineReadOutcome.Cancelled)
                {
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                SyncCliPresence();
                if (_configurationEdit is { } activeEdit)
                {
                    await ExecuteConfigurationLineAsync(activeEdit, read.Text, cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var result = _dispatcher.Execute(
                    input: read.Text,
                    loginIdentity: _operator,
                    activePrivilege: _activePrivilege,
                    selfSession: GetSelfSessionSnapshot(),
                    terminalColumns: _terminal.Columns);
                if (result.Action == CliCommandAction.Connect &&
                    !string.IsNullOrWhiteSpace(result.ConnectSessionId) &&
                    result.SerialSettings is { } serialSettings)
                {
                    await ExecuteConnectAsync(result.ConnectSessionId, serialSettings, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Attach)
                {
                    await ExecuteAttachAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Enable && result.EnableTargetLevel is { } targetLevel)
                {
                    await ExecuteEnableAsync(targetLevel, cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Disable)
                {
                    await ExecuteDisableAsync(cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.ReleaseAttachment)
                {
                    await ExecuteReleaseAttachmentAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.AdminDisconnect &&
                    !string.IsNullOrWhiteSpace(result.DisconnectSessionId))
                {
                    await ExecuteAdminDisconnectAsync(result.DisconnectSessionId, cancellationToken)
                        .ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Ping && result.PingRequest is { } pingRequest)
                {
                    await ExecutePingAsync(pingRequest, cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Traceroute &&
                    result.TracerouteRequest is { } tracerouteRequest)
                {
                    await ExecuteTracerouteAsync(tracerouteRequest, cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.ClearScreen)
                {
                    await ExecuteClearScreenAsync(cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Nslookup &&
                    !string.IsNullOrWhiteSpace(result.NslookupHostname))
                {
                    await ExecuteNslookupAsync(result.NslookupHostname, cancellationToken)
                        .ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.Copy && result.CopyRequest is { } copyRequest)
                {
                    await ExecuteCopyAsync(copyRequest, cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (result.Action == CliCommandAction.EnterConfiguration)
                {
                    await EnterConfigurationAsync(cancellationToken).ConfigureAwait(false);
                    await WritePromptAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!string.IsNullOrEmpty(result.Output))
                {
                    await WriteTextAsync(result.Output + "\r\n", cancellationToken).ConfigureAwait(false);
                }

                if (result.Action == CliCommandAction.Exit)
                {
                    _logger.LogInformation("Operator {Operator} requested CLI exit", _operatorIdentity);
                    break;
                }

                await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_configurationEdit is { } abandoned)
            {
                _configurationEdit = null;
                try
                {
                    _configurationManagement!.Discard(abandoned, "session ended");
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Configuration discard on session end failed for {Operator}", _operatorIdentity);
                }
            }

            _operatorSessions.UnregisterCliSession(_operatorSessionId);
            _pendingBaudRequests.FailOperator(
                _operatorSessionId,
                "Operator SSH session ended.");
            if (_attachmentManager.TryDetachByOperatorSessionId(
                    _operatorSessionId,
                    AttachmentDetachReason.OperatorDisconnect,
                    out var leftover))
            {
                _logger.LogInformation(
                    "Operator disconnect detached session. Operator={Operator}, SessionId={SessionId}",
                    _operatorIdentity,
                    leftover!.SessionId);
                _audit.TryWrite(
                    AuditEventType.AttachmentReleasedSshDisconnect,
                    AuditEventOutcome.Informational,
                    "Attachment released after SSH disconnect",
                    new AuditEventContext
                    {
                        ActorUsername = _operatorIdentity,
                        SessionId = leftover.SessionId
                    });
                if (_connectionTracker.TryGet(leftover.ConnectionId, out var leftoverConnection) &&
                    leftoverConnection is not null)
                {
                    try
                    {
                        using var statusCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await PublishOperatorStatusAsync(
                                leftoverConnection,
                                attached: false,
                                terminalActive: false,
                                statusCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(
                            ex,
                            "OperatorStatus Waiting publish skipped after SSH disconnect. SessionId={SessionId}",
                            leftover.SessionId);
                    }
                }

                leftover.Dispose();
            }

            _writeGate.Dispose();
        }
    }

    /// <summary>
    /// Updates the operator presence tracker with privilege, attachment, terminal mode, and size.
    /// </summary>
    private void SyncCliPresence()
    {
        _operatorSessions.UpdateActivePrivilege(_operatorSessionId, _activePrivilege);
        _operatorSessions.UpdateTerminalSize(_operatorSessionId, _terminal.Columns, _terminal.Rows);
        if (_attachmentManager.TryGetByOperatorSessionId(_operatorSessionId, out var attachment) &&
            attachment is not null)
        {
            _operatorSessions.UpdateAttachment(_operatorSessionId, attachment.SessionId);
            _operatorSessions.UpdateTerminalMode(_operatorSessionId, attachment.IsTerminalActive);
        }
        else
        {
            _operatorSessions.UpdateAttachment(_operatorSessionId, null);
            _operatorSessions.UpdateTerminalMode(_operatorSessionId, false);
        }
    }

    /// <summary>
    /// Builds or refreshes the live OperatorCliSessionRecord used by show terminal.
    /// </summary>
    private OperatorCliSessionRecord GetSelfSessionSnapshot()
    {
        SyncCliPresence();
        if (_operatorSessions.TryGetCliSession(_operatorSessionId, out var record) && record is not null)
        {
            return record;
        }

        return new OperatorCliSessionRecord
        {
            OperatorSessionId = _operatorSessionId,
            Username = _operatorIdentity,
            LoginPrivilege = _originalPrivilege,
            ActivePrivilege = _activePrivilege,
            RemoteEndpoint = _remoteEndpoint,
            ConnectedAtUtc = _operator.AuthenticatedAtUtc,
            TermType = _terminal.Term,
            Columns = _terminal.Columns,
            Rows = _terminal.Rows
        };
    }

    /// <summary>
    /// Writes ANSI clear-screen sequences to the SSH channel.
    /// </summary>
    private Task ExecuteClearScreenAsync(CancellationToken cancellationToken) =>
        // ESC[2J clear screen; ESC[H cursor home — current SSH session only.
        WriteTextAsync("\u001b[2J\u001b[H", cancellationToken);

    /// <summary>
    /// Runs an authorized copy transfer to the attached serial session.
    /// </summary>
    private async Task ExecuteCopyAsync(CopyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_attachmentManager.TryGetByOperatorSessionId(_operatorSessionId, out var attachment) ||
            attachment is null)
        {
            await WriteTextAsync(
                    "% No serial attachment. Connect and remain attached before copy.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!string.Equals(attachment.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            await WriteTextAsync(
                    "% serial:<session-id> must match your currently attached connector session.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_connectionTracker.TryGet(attachment.ConnectionId, out var connection) || connection is null)
        {
            await WriteTextAsync("% Connector connection is no longer available.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_activeCopies.IsActive(attachment.SessionId))
        {
            await WriteTextAsync(
                    "% A copy operation is already active for this serial attachment.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CopySourceUri source;
        try
        {
            source = await CompleteCopySourceInteractiveAsync(request.Source, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException ex)
        {
            await WriteTextAsync(ex.Message + "\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(
                $"Source: {source.DisplayUri}\r\nDestination: serial:{attachment.SessionId}\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        await using ICopyFileSource? fileSource = await OpenCopySourceAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (fileSource is null)
        {
            return;
        }

        async Task ReportProgressAsync(long bytes, long? total, CancellationToken ct)
        {
            if (total is > 0)
            {
                var pct = Math.Min(100, (int)(bytes * 100 / total.Value));
                await WriteTextAsync($"\rTransferred {bytes}/{total} bytes ({pct}%)", ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await WriteTextAsync($"\rTransferred {bytes} bytes", ct).ConfigureAwait(false);
            }
        }

        var result = await _copyTransfer
            .TransferAsync(
                fileSource,
                attachment,
                connection,
                _operatorSessionId,
                ReportProgressAsync,
                cancellationToken)
            .ConfigureAwait(false);

        await WriteTextAsync("\r\n" + result.Message + "\r\n", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Copy finished. Operator={Operator} SessionId={SessionId} Success={Success} Bytes={Bytes} Cancelled={Cancelled}",
            _operatorIdentity,
            attachment.SessionId,
            result.Success,
            result.BytesWrittenToSerialTransport,
            result.Cancelled);
    }

    /// <summary>
    /// Opens a copy file source, prompting interactively when required.
    /// </summary>
    private async Task<ICopyFileSource?> OpenCopySourceAsync(
        CopySourceUri source,
        CancellationToken cancellationToken)
    {
        try
        {
            if (source.IsModem)
            {
                if (_lineReader is null)
                {
                    await WriteTextAsync("% Modem receive unavailable.\r\n", cancellationToken)
                        .ConfigureAwait(false);
                    return null;
                }

                return await ModemCopyFileSource.OpenAsync(
                        source.Kind,
                        ct => _lineReader.ReadRawByteAsync(ct),
                        async (data, ct) => await _lineReader.WriteRawBytesAsync(data, ct).ConfigureAwait(false),
                        (text, ct) => WriteTextAsync(text, ct),
                        _loggerFactory.CreateLogger("TechConnect.Copy.Modem"),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await _copySources
                .OpenAsync(
                    source,
                    async (prompt, ct) =>
                    {
                        await WriteTextAsync(prompt, ct).ConfigureAwait(false);
                        if (_lineReader is null)
                        {
                            return null;
                        }

                        var secret = await _lineReader.ReadSecretLineAsync(ct).ConfigureAwait(false);
                        return secret.Outcome == LineReadOutcome.Line ? secret.Text : null;
                    },
                    ConfirmScpHostKeyAsync,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await WriteTextAsync(ex.Message + "\r\n", cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open copy source {Source}", source.DisplayUri);
            await WriteTextAsync("% Unable to open copy source.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Interactively completes copy source URI fields (credentials, paths, etc.).
    /// </summary>
    private async Task<CopySourceUri> CompleteCopySourceInteractiveAsync(
        CopySourceUri source,
        CancellationToken cancellationToken)
    {
        if (source.IsModem)
        {
            return source;
        }

        var host = source.Host;
        var path = source.Path;
        var username = source.Username;
        var password = source.Password;

        if (string.IsNullOrWhiteSpace(host))
        {
            host = await PromptCopyFieldAsync("Address or name of remote host []? ", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new InvalidOperationException("% Remote host is required.");
            }
        }

        var needsFile = string.IsNullOrWhiteSpace(path) ||
                        (source.Kind != CopySourceKind.Scp && path == "/");
        if (needsFile)
        {
            var file = await PromptCopyFieldAsync("Source filename []? ", cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(file))
            {
                throw new InvalidOperationException("% Source filename is required.");
            }

            path = file;
        }

        if (source.Kind == CopySourceKind.Scp && string.IsNullOrWhiteSpace(username))
        {
            username = await PromptCopyFieldAsync("Username []? ", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(username))
            {
                throw new InvalidOperationException("% Username is required.");
            }
        }

        // SCP password is prompted only after host-key confirmation (inside ScpCopyFileSource).
        // HTTP(S) may still prompt here when a username is present without a password.
        if ((source.Kind is CopySourceKind.Http or CopySourceKind.Https) &&
            !string.IsNullOrWhiteSpace(username) &&
            password is null)
        {
            await WriteTextAsync("Password: ", cancellationToken).ConfigureAwait(false);
            if (_lineReader is null)
            {
                throw new InvalidOperationException("% Password prompt unavailable.");
            }

            var secret = await _lineReader.ReadSecretLineAsync(cancellationToken).ConfigureAwait(false);
            if (secret.Outcome != LineReadOutcome.Line)
            {
                throw new InvalidOperationException("% Copy cancelled.");
            }

            password = secret.Text;
        }

        // Rebuild so DisplayUri stays credential-free even after interactive completion.
        return CopySourceParser.CompleteHttpLike(
            source.Kind,
            host!,
            path!,
            username,
            password,
            source.Port);
    }

    /// <summary>
    /// Prompts for a single copy field line via the interactive reader.
    /// </summary>
    private async Task<string> PromptCopyFieldAsync(string prompt, CancellationToken cancellationToken)
    {
        if (_lineReader is null)
        {
            throw new InvalidOperationException("% Interactive prompt unavailable.");
        }

        _linePromptOverride = prompt;
        try
        {
            await WriteTextAsync(prompt, cancellationToken).ConfigureAwait(false);
            var read = await _lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (read.Outcome != LineReadOutcome.Line)
            {
                throw new InvalidOperationException("% Copy cancelled.");
            }

            return read.Text.Trim();
        }
        finally
        {
            _linePromptOverride = null;
        }
    }

    /// <summary>
    /// Per-connection SCP host-key confirmation. Response is never stored in command history.
    /// Only an explicit <c>yes</c> accepts; anything else rejects.
    /// </summary>
    private async Task<bool> ConfirmScpHostKeyAsync(
        ScpHostKeyInfo info,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (_lineReader is null)
        {
            return false;
        }

        await WriteTextAsync(
                $"\r\nConnecting to SCP server {info.Host}:{info.Port}...\r\n\r\n" +
                ScpHostKeyFingerprint.FormatPrompt(info),
                cancellationToken)
            .ConfigureAwait(false);

        LineReadResult read;
        try
        {
            read = await _lineReader.ReadTransientLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (read.Outcome != LineReadOutcome.Line ||
            !ScpHostKeyFingerprint.IsExplicitYes(read.Text))
        {
            await WriteTextAsync("SCP connection rejected by operator.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        await WriteTextAsync(
                "Host key accepted for this connection.\r\nAuthenticating...\r\n",
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Runs a TechConnect-configured DNS lookup for <paramref name="hostname"/> and writes formatted results.
    /// </summary>
    /// <param name="hostname">Name to resolve via <see cref="IDnsLookupService"/>.</param>
    /// <param name="cancellationToken">Cancels the lookup or CLI write; also linked to a 10s timeout.</param>
    private async Task ExecuteNslookupAsync(string hostname, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = await _dnsLookup
                .LookupAsync(new DnsLookupRequest { Hostname = hostname }, timeoutCts.Token)
                .ConfigureAwait(false);
            await WriteTextAsync(NocDisplayFormatter.FormatDnsLookup(result) + "\r\n", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            await WriteTextAsync("% DNS lookup timed out.\r\n", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nslookup failed for {Hostname}", hostname);
            await WriteTextAsync("% DNS lookup failed: " + ex.Message + "\r\n", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enters configuration mode with a private candidate copied from the effective configuration.
    /// </summary>
    private async Task EnterConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_configurationManagement is null)
        {
            await WriteTextAsync(TechConnectCliDispatcher.ConfigurationUnavailableMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var edit = _configurationManagement.BeginEdit(ActiveIdentity, out var notes);
        _configurationEdit = edit;
        var text = new StringBuilder(ConfigurationModeProcessor.EntryMessage).Append("\r\n");
        foreach (var note in notes)
        {
            text.Append(note).Append("\r\n");
        }

        await WriteTextAsync(text.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes one configuration-mode line (privilege is re-checked by the processor).
    /// </summary>
    private async Task ExecuteConfigurationLineAsync(
        ConfigurationEditSession edit,
        string text,
        CancellationToken cancellationToken)
    {
        var result = _configurationProcessor!.Execute(edit, ActiveIdentity, text);
        if (result.ForgetHistory)
        {
            _lineReader?.ForgetLastHistoryEntry();
        }

        if (!string.IsNullOrEmpty(result.Output))
        {
            await WriteTextAsync(result.Output + "\r\n", cancellationToken).ConfigureAwait(false);
        }

        switch (result.Action)
        {
            case ConfigurationLineAction.Leave:
                await LeaveConfigurationAsync(edit, "end", cancellationToken).ConfigureAwait(false);
                break;
            case ConfigurationLineAction.PromptSecret when result.Secret is { } request:
                await PromptConfigurationSecretAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case ConfigurationLineAction.Write:
                var written = await _configurationManagement!.WriteAsync(edit, cancellationToken).ConfigureAwait(false);
                await WriteTextAsync(ConfigurationModeProcessor.FormatWriteResult(written) + "\r\n", cancellationToken)
                    .ConfigureAwait(false);
                break;
            case ConfigurationLineAction.ExecuteExec when result.ExecCommand is { } command:
                await ExecuteDoAsync(command, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Runs a <c>do show ...</c> command through the EXEC dispatcher (normal authorization and audit).
    /// Only plain-output results are shown; anything that would act on the session is refused.
    /// </summary>
    private async Task ExecuteDoAsync(string command, CancellationToken cancellationToken)
    {
        var result = _dispatcher.Execute(
            input: command,
            loginIdentity: _operator,
            activePrivilege: _activePrivilege,
            selfSession: GetSelfSessionSnapshot(),
            terminalColumns: _terminal.Columns);
        var text = result.Action == CliCommandAction.Continue
            ? result.Output
            : "% This command cannot be run with 'do'.";
        if (!string.IsNullOrEmpty(text))
        {
            await WriteTextAsync(text + "\r\n", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Leaves configuration mode, discarding unsaved changes with a warning.
    /// </summary>
    private async Task LeaveConfigurationAsync(ConfigurationEditSession edit, string reason, CancellationToken cancellationToken)
    {
        _configurationEdit = null;
        var discarded = _configurationManagement!.Discard(edit, reason);
        if (discarded > 0)
        {
            await WriteTextAsync(ConfigurationModeProcessor.DiscardedMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads a secret twice with hidden input and applies it to the candidate.
    /// </summary>
    private async Task PromptConfigurationSecretAsync(ConfigurationSecretRequest request, CancellationToken cancellationToken)
    {
        await WriteTextAsync(request.Prompt, cancellationToken).ConfigureAwait(false);
        var first = await _lineReader!.ReadSecretLineAsync(cancellationToken).ConfigureAwait(false);
        if (first.Outcome != LineReadOutcome.Line)
        {
            await WriteTextAsync(request.CancelledMessage + "\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteTextAsync(request.ConfirmPrompt, cancellationToken).ConfigureAwait(false);
        var second = await _lineReader.ReadSecretLineAsync(cancellationToken).ConfigureAwait(false);
        if (second.Outcome != LineReadOutcome.Line)
        {
            await WriteTextAsync(request.CancelledMessage + "\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        var error = _configurationProcessor!.ApplySecret(request, first.Text, second.Text);
        if (error is not null)
        {
            await WriteTextAsync(error + "\r\n", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prompts for enable secret and raises active privilege on success.
    /// </summary>
    private async Task ExecuteEnableAsync(int targetLevelInt, CancellationToken cancellationToken)
    {
        var target = PrivilegeLevel.FromInt(targetLevelInt);

        // Already at or above the requested level: no authentication and no privilege reduction.
        if (target <= _activePrivilege)
        {
            return;
        }

        if (_lineReader is null)
        {
            await WriteTextAsync("% Enable authentication unavailable.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteTextAsync("Password: ", cancellationToken).ConfigureAwait(false);
        LineReadResult secretRead;
        try
        {
            secretRead = await _lineReader.ReadSecretLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (SshConnectionException)
        {
            return;
        }
        catch (SshChannelException)
        {
            return;
        }

        if (secretRead.Outcome is LineReadOutcome.Cancelled or LineReadOutcome.Exit)
        {
            await WriteTextAsync("% Enable cancelled.\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Use login identity (Provider/Method) with active privilege for audit context only.
        var result = await _enablePrivilege
            .AuthenticateEnableAsync(ActiveIdentity, secretRead.Text, target, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await WriteTextAsync(
                    (result.Description ?? "% Enable authentication failed.") + "\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Granted level is the requested level (must already be ≤ authorized).
        var granted = result.Privilege;
        var previousActive = _activePrivilege;
        _activePrivilege = granted;
        _operatorSessions.UpdateActivePrivilege(_operatorSessionId, _activePrivilege);
        if (previousActive != _activePrivilege)
        {
            AuditPrivilegeChanged(
                previousActive,
                _activePrivilege,
                requested: target,
                authorized: result.AuthorizedPrivilege);
        }

    }

    /// <summary>
    /// Restores active privilege toward login/disable semantics (policy Disable min 1).
    /// </summary>
    private Task ExecuteDisableAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var previous = _activePrivilege;
        var level1 = PrivilegeLevel.FromInt(1);
        _activePrivilege = level1;
        _operatorSessions.UpdateActivePrivilege(_operatorSessionId, _activePrivilege);
        if (previous != _activePrivilege)
        {
            _audit.TryWrite(
                AuditEventType.DisableToLevel1,
                AuditEventOutcome.Success,
                "Active privilege returned to level 1",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["originalLogin"] = _originalPrivilege.ToString(),
                        ["previous"] = previous.ToString(),
                        ["new"] = _activePrivilege.ToString(),
                        ["provider"] = _operator.Provider.ToString()
                    }
                });
        }

        // Privilege 1 retains terminal/serial access; do not exit terminal or detach on disable.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes an audit event for privilege change.
    /// </summary>
    private void AuditPrivilegeChanged(
        PrivilegeLevel previous,
        PrivilegeLevel next,
        PrivilegeLevel? requested = null,
        PrivilegeLevel? authorized = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["originalLogin"] = _originalPrivilege.ToString(),
            ["previous"] = previous.ToString(),
            ["new"] = next.ToString(),
            ["provider"] = _operator.Provider.ToString()
        };
        if (requested is { } req)
        {
            details["requestedLevel"] = req.ToString();
        }

        if (authorized is { } auth)
        {
            details["authorizedLevel"] = auth.ToString();
        }

        _audit.TryWrite(
            AuditEventType.PrivilegeChanged,
            AuditEventOutcome.Success,
            "Active privilege changed",
            new AuditEventContext
            {
                ActorUsername = _operatorIdentity,
                Details = details
            });
    }

    /// <summary>
    /// Interactive admin disconnect of a connector session (policy Disconnect min 7).
    /// </summary>
    private async Task ExecuteAdminDisconnectAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_authorization.TryAuthorize(ActiveIdentity, CliCommandId.Disconnect, out _))
        {
            await WriteTextAsync(OperatorAuthorizationService.PermissionDeniedMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Prompt against the registry snapshot; connection ownership is revalidated after confirm.
        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var live) || live is null)
        {
            await WriteTextAsync($"Session {sessionId} is not connected.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var expectedConnectionId = live.ConnectionId;
        var connectorIdentity = live.ConnectorIdentity.Length <= 12
            ? live.ConnectorIdentity
            : live.ConnectorIdentity[..12];
        var remote = live.RemoteEndpoint;

        await WriteTextAsync(
                $"\r\nDisconnect connector {live.SessionId}?\r\n" +
                "This will terminate the remote console connection.\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        var confirmed = await PromptDisconnectConfirmationAsync(cancellationToken).ConfigureAwait(false);
        if (confirmed is null)
        {
            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectCancelled,
                AuditEventOutcome.Informational,
                "Administrative connector disconnect cancelled",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = live.SessionId,
                    ConnectorIdentity = connectorIdentity,
                    RemoteEndpoint = remote
                });
            await WriteTextAsync("Disconnect cancelled.\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (confirmed == false)
        {
            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectDeclined,
                AuditEventOutcome.Informational,
                "Administrative connector disconnect declined",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = live.SessionId,
                    ConnectorIdentity = connectorIdentity,
                    RemoteEndpoint = remote
                });
            await WriteTextAsync("Disconnect cancelled.\r\n", cancellationToken).ConfigureAwait(false);
            return;
        }

        // Revalidate after confirmation — session may have left or been replaced.
        var stillLive = _connectorDisconnect.TryResolveLiveSession(live.SessionId);
        if (stillLive is null ||
            !string.Equals(stillLive.ConnectionId, expectedConnectionId, StringComparison.Ordinal))
        {
            await WriteTextAsync($"Session {live.SessionId} is not connected.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await WriteTextAsync($"Disconnecting connector {live.SessionId}...\r\n", cancellationToken)
            .ConfigureAwait(false);

        // After confirmation, teardown ignores SSH cancellation so cleanup is not abandoned.
        // Caller token still applies to pre-commit checks inside the service.
        ConnectorDisconnectResult outcome;
        try
        {
            outcome = await _connectorDisconnect
                .DisconnectAsync(ActiveIdentity, live.SessionId, expectedConnectionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Only reachable if cancellation occurred before teardown was committed.
            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectCancelled,
                AuditEventOutcome.Informational,
                "Administrative connector disconnect cancelled before teardown",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = live.SessionId,
                    ConnectorIdentity = connectorIdentity,
                    RemoteEndpoint = remote
                });
            try
            {
                await WriteTextAsync("Disconnect cancelled.\r\n", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // SSH may already be gone.
            }

            return;
        }

        var message = outcome.Message ?? outcome.Status switch
        {
            ConnectorDisconnectStatus.Completed =>
                $"Connector {live.SessionId} disconnected successfully.",
            ConnectorDisconnectStatus.AlreadyDisconnecting =>
                $"Session {live.SessionId} is already disconnecting.",
            ConnectorDisconnectStatus.NotConnected =>
                $"Session {live.SessionId} is not connected.",
            ConnectorDisconnectStatus.CleanupIncomplete =>
                $"Disconnect of connector {live.SessionId} was requested; cleanup has not finished yet.",
            ConnectorDisconnectStatus.Unauthorized =>
                OperatorAuthorizationService.PermissionDeniedMessage,
            _ => $"Disconnect of connector {live.SessionId} failed."
        };

        try
        {
            await WriteTextAsync(message + "\r\n", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Teardown already finished or was committed; reporting to SSH is best-effort.
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Dispatches ICMP or session ping with streamed CLI output.
    /// </summary>
    private async Task ExecutePingAsync(PingRequest request, CancellationToken cancellationToken)
    {
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var abortWatch = WatchForDiagnosticAbortAsync(pingCts);

        try
        {
            if (request.Kind == PingTargetKind.Session)
            {
                await ExecuteSessionPingAsync(request, pingCts.Token).ConfigureAwait(false);
                return;
            }

            var resolved = await ResolvePingDestinationAsync(request, pingCts.Token).ConfigureAwait(false);
            if (resolved is null)
            {
                return;
            }

            if (resolved.Sweep)
            {
                await ExecuteIcmpSweepAsync(resolved, pingCts.Token).ConfigureAwait(false);
            }
            else
            {
                await ExecuteIcmpPingAsync(resolved, pingCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (pingCts.IsCancellationRequested)
        {
            // Summary is written by the ping helpers when they observe cancellation mid-run.
        }
        finally
        {
            pingCts.Cancel();
            try
            {
                await abortWatch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>
    /// Resolves ping destination hostnames and validates family/source.
    /// </summary>
    private async Task<PingRequest?> ResolvePingDestinationAsync(
        PingRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.RequiresResolution && request.Destination is not null)
        {
            return request;
        }

        var input = request.DestinationInput ?? string.Empty;
        await WriteTextAsync($"Resolving {input}...\r\n", cancellationToken).ConfigureAwait(false);
        var resolution = await _destinationResolver.ResolveAsync(input, cancellationToken)
            .ConfigureAwait(false);
        if (!resolution.Success || resolution.Address is null || resolution.Family is null)
        {
            await WriteTextAsync((resolution.Error ?? "% Unable to resolve hostname.") + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var kind = resolution.Family == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? PingTargetKind.Ipv6
            : PingTargetKind.Ipv4;

        if (request.DontFragment && kind == PingTargetKind.Ipv6)
        {
            await WriteTextAsync("% Don't Fragment (df) is not supported for IPv6.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        if (request.Source is not null && request.Source.AddressFamily != resolution.Family)
        {
            await WriteTextAsync(
                    "% Source address family does not match the resolved destination address family.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var familyLabel = kind == PingTargetKind.Ipv6 ? "IPv6" : "IPv4";
        await WriteTextAsync(
                $"Using {familyLabel} address {resolution.Address}\r\n\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        return new PingRequest
        {
            Kind = kind,
            DestinationInput = resolution.Input,
            Destination = resolution.Address,
            SessionId = request.SessionId,
            Repeat = request.Repeat,
            TimeoutSeconds = request.TimeoutSeconds,
            PayloadBytes = request.PayloadBytes,
            Source = request.Source,
            DontFragment = request.DontFragment,
            Sweep = request.Sweep,
            SweepMinBytes = request.SweepMinBytes,
            SweepMaxBytes = request.SweepMaxBytes,
            SweepIntervalBytes = request.SweepIntervalBytes
        };
    }

    /// <summary>
    /// Runs ICMP traceroute with streamed hop rows and optional reverse lookup.
    /// </summary>
    private async Task ExecuteTracerouteAsync(TracerouteRequest request, CancellationToken cancellationToken)
    {
        using var traceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var abortWatch = WatchForDiagnosticAbortAsync(traceCts);

        var cancelled = false;
        var destinationReached = false;
        var terminalFailure = false;
        var hopProbes = new List<TracerouteProbeResult>();
        var currentTtl = 0;
        var probesPerHop = request.ProbesPerHop;
        var ptrCache = new Dictionary<System.Net.IPAddress, string?>(new IpAddressComparer());
        var resolvedRequest = request;

        try
        {
            if (request.RequiresResolution)
            {
                await WriteTextAsync(
                        $"Resolving {request.DestinationInput}...\r\n",
                        traceCts.Token)
                    .ConfigureAwait(false);
                var resolution = await _destinationResolver
                    .ResolveAsync(request.DestinationInput, traceCts.Token)
                    .ConfigureAwait(false);
                if (!resolution.Success || resolution.Address is null || resolution.Family is null)
                {
                    await WriteTextAsync(
                            (resolution.Error ?? "% Unable to resolve hostname.") + "\r\n",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (request.Source is not null &&
                    request.Source.AddressFamily != resolution.Family)
                {
                    await WriteTextAsync(
                            "% Source address family does not match the resolved destination address family.\r\n",
                            cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                var family = resolution.Family == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? TracerouteAddressFamily.Ipv6
                    : TracerouteAddressFamily.Ipv4;
                var familyLabel = family == TracerouteAddressFamily.Ipv6 ? "IPv6" : "IPv4";
                await WriteTextAsync(
                        $"Using {familyLabel} address {resolution.Address}\r\n\r\n",
                        traceCts.Token)
                    .ConfigureAwait(false);

                resolvedRequest = new TracerouteRequest
                {
                    Family = family,
                    DestinationInput = resolution.Input,
                    Destination = resolution.Address,
                    RequiresResolution = false,
                    Source = request.Source,
                    TimeoutSeconds = request.TimeoutSeconds,
                    ProbesPerHop = request.ProbesPerHop,
                    MaxTtl = request.MaxTtl,
                    InitialPort = request.InitialPort,
                    PayloadBytes = request.PayloadBytes
                };
            }

            await WriteTextAsync(TracerouteOutputFormatter.FormatHeader(resolvedRequest), traceCts.Token)
                .ConfigureAwait(false);

            await foreach (var probe in _traceroute.TraceAsync(resolvedRequest, traceCts.Token)
                               .ConfigureAwait(false))
            {
                if (probe.Ttl != currentTtl)
                {
                    if (hopProbes.Count > 0)
                    {
                        await WriteHopRowAsync(
                                currentTtl,
                                hopProbes,
                                probesPerHop,
                                ptrCache,
                                traceCts.Token)
                            .ConfigureAwait(false);
                        hopProbes.Clear();
                    }

                    currentTtl = probe.Ttl;
                }

                hopProbes.Add(probe);

                if (probe.DestinationReached)
                {
                    destinationReached = true;
                }

                if (probe.StopTracing && !probe.DestinationReached)
                {
                    terminalFailure = true;
                }

                if (probe.Status == TracerouteProbeStatus.Error &&
                    !string.IsNullOrWhiteSpace(probe.Detail) &&
                    probe.Ttl == 1 &&
                    probe.ProbeIndex == 1)
                {
                    await WriteTextAsync("% " + probe.Detail + "\r\n", traceCts.Token)
                        .ConfigureAwait(false);
                }
            }

            if (hopProbes.Count > 0)
            {
                await WriteHopRowAsync(currentTtl, hopProbes, probesPerHop, ptrCache, traceCts.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (traceCts.IsCancellationRequested)
        {
            cancelled = true;
            try
            {
                // Complete the in-flight hop (pad unfinished probes as '*').
                // If cancel hits before the first probe result, still emit hop 1 as all timeouts.
                var ttl = currentTtl > 0 ? currentTtl : 1;
                await WriteHopRowAsync(ttl, hopProbes, probesPerHop, ptrCache, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or IOException)
        {
            terminalFailure = true;
            _logger.LogWarning(
                ex,
                "Traceroute failed for operator {Operator} destination {Destination}",
                _operatorIdentity,
                request.DestinationInput);
            try
            {
                if (hopProbes.Count > 0)
                {
                    await WriteHopRowAsync(currentTtl, hopProbes, probesPerHop, ptrCache, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WriteTextAsync("% " + ex.Message + "\r\n", cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
        catch (Exception ex)
        {
            terminalFailure = true;
            _logger.LogError(
                ex,
                "Unexpected traceroute failure for operator {Operator} destination {Destination}",
                _operatorIdentity,
                request.DestinationInput);
            try
            {
                if (hopProbes.Count > 0)
                {
                    await WriteHopRowAsync(currentTtl, hopProbes, probesPerHop, ptrCache, cancellationToken)
                        .ConfigureAwait(false);
                }

                await WriteTextAsync("% Traceroute failed unexpectedly.\r\n", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            traceCts.Cancel();
            try
            {
                await abortWatch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await WriteTextAsync(
                    TracerouteOutputFormatter.FormatFooter(destinationReached, cancelled, terminalFailure),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Formats and writes one traceroute hop row to the CLI channel.
    /// </summary>
    private async Task WriteHopRowAsync(
        int ttl,
        IReadOnlyList<TracerouteProbeResult> hopProbes,
        int probesPerHop,
        Dictionary<System.Net.IPAddress, string?> ptrCache,
        CancellationToken cancellationToken)
    {
        string? hostname = null;
        if (_dnsResolver.IsEnabled)
        {
            System.Net.IPAddress? responder = null;
            foreach (var probe in hopProbes)
            {
                if (probe.Responder is not null &&
                    probe.Status is not TracerouteProbeStatus.TimedOut and not TracerouteProbeStatus.Error)
                {
                    responder = probe.Responder;
                    break;
                }
            }

            if (responder is not null)
            {
                if (!ptrCache.TryGetValue(responder, out hostname))
                {
                    hostname = await ReverseLookupBoundedAsync(responder, cancellationToken)
                        .ConfigureAwait(false);
                    ptrCache[responder] = hostname;
                }
            }
        }

        await WriteTextAsync(
                TracerouteOutputFormatter.FormatHopLine(ttl, hopProbes, probesPerHop, hostname),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded reverse DNS lookup for traceroute hop addresses.
    /// </summary>
    private async Task<string?> ReverseLookupBoundedAsync(
        System.Net.IPAddress address,
        CancellationToken cancellationToken)
    {
        // Bound PTR time so reverse DNS cannot stall hop rendering.
        var bound = TimeSpan.FromMilliseconds(
            Math.Min(1_000, Math.Max(200, _dnsResolver.QueryTimeout.TotalMilliseconds)));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(bound);
        try
        {
            return await _dnsResolver.ReverseResolveAsync(address, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PTR lookup failed for {Address}", address);
            return null;
        }
    }
    /// <summary>
    /// Equality comparer for <see cref="System.Net.IPAddress"/> using address equality (not reference).
    /// </summary>
    private sealed class IpAddressComparer : IEqualityComparer<System.Net.IPAddress>
    {
        /// <summary>
        /// Returns whether two addresses are equal, treating nulls as equal only to null.
        /// </summary>
        public bool Equals(System.Net.IPAddress? x, System.Net.IPAddress? y) =>
            x is null ? y is null : y is not null && x.Equals(y);

        /// <summary>
        /// Returns the hash code of <paramref name="obj"/>'s address bytes.
        /// </summary>
        public int GetHashCode(System.Net.IPAddress obj) => obj.GetHashCode();
    }

    /// <summary>
    /// Runs standard ICMP echo ping probes with CLI output.
    /// </summary>
    private async Task ExecuteIcmpPingAsync(PingRequest request, CancellationToken cancellationToken)
    {
        var stats = new PingProbeStats();
        await WriteTextAsync(PingOutputFormatter.FormatIcmpHeader(request), cancellationToken)
            .ConfigureAwait(false);

        var identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var cancelled = false;
        for (ushort seq = 1; seq <= request.Repeat; seq++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            IcmpEchoReply reply;
            try
            {
                reply = await _icmp.SendAsync(
                        new IcmpEchoRequest
                        {
                            Destination = request.Destination!,
                            Source = request.Source,
                            PayloadBytes = request.PayloadBytes,
                            Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds),
                            DontFragment = request.DontFragment,
                            Identifier = identifier,
                            Sequence = seq
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            stats.Record(reply.Status, reply.RoundTripTime);
            await WriteTextAsync(
                    PingOutputFormatter.Indicator(reply.Status).ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (reply.Status == IcmpEchoStatus.Error &&
                !string.IsNullOrWhiteSpace(reply.Error) &&
                stats.Sent == 1)
            {
                await WriteTextAsync("\r\n% " + reply.Error + "\r\n", cancellationToken)
                    .ConfigureAwait(false);
                break;
            }
        }

        await WriteTextAsync(PingOutputFormatter.FormatSummary(stats, cancelled), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs ICMP payload-size sweep ping with CLI output.
    /// </summary>
    private async Task ExecuteIcmpSweepAsync(PingRequest request, CancellationToken cancellationToken)
    {
        var stats = new PingProbeStats();
        await WriteTextAsync(PingOutputFormatter.FormatIcmpHeader(request), cancellationToken)
            .ConfigureAwait(false);

        var identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        ushort seq = 1;
        int? largestSuccess = null;
        var cancelled = false;
        var overhead = request.Kind == PingTargetKind.Ipv6
            ? PingLimits.Ipv6IcmpOverheadBytes
            : PingLimits.Ipv4IcmpOverheadBytes;

        for (var size = request.SweepMinBytes;
             size <= request.SweepMaxBytes;
             size += request.SweepIntervalBytes)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var sizeSuccess = false;
            char lastIndicator = '.';
            for (var r = 0; r < request.Repeat; r++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                IcmpEchoReply reply;
                try
                {
                    reply = await _icmp.SendAsync(
                            new IcmpEchoRequest
                            {
                                Destination = request.Destination!,
                                Source = request.Source,
                                PayloadBytes = size,
                                Timeout = TimeSpan.FromSeconds(request.TimeoutSeconds),
                                DontFragment = request.DontFragment || request.Kind == PingTargetKind.Ipv4,
                                Identifier = identifier,
                                Sequence = seq++
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                stats.Record(reply.Status, reply.RoundTripTime);
                lastIndicator = PingOutputFormatter.Indicator(reply.Status);
                if (reply.Status == IcmpEchoStatus.Success)
                {
                    sizeSuccess = true;
                }
            }

            if (cancelled)
            {
                break;
            }

            if (sizeSuccess)
            {
                largestSuccess = size;
            }

            await WriteTextAsync(
                    PingOutputFormatter.FormatSweepSizeResult(size, size + overhead, lastIndicator),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await WriteTextAsync(PingOutputFormatter.FormatSummary(stats, cancelled), cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                PingOutputFormatter.FormatLargestSuccessfulPayload(largestSuccess),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs connector session health probes with CLI output.
    /// </summary>
    private async Task ExecuteSessionPingAsync(PingRequest request, CancellationToken cancellationToken)
    {
        var sessionId = request.SessionId!;
        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            await WriteTextAsync($"% Session {sessionId} is not connected.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!_connectionTracker.TryGet(session.ConnectionId, out var connection) || connection is null)
        {
            await WriteTextAsync($"% Session {sessionId} is not connected.\r\n", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var hostname = session.Hostname;
        await WriteTextAsync(
                PingOutputFormatter.FormatSessionHeader(session.SessionId, hostname),
                cancellationToken)
            .ConfigureAwait(false);

        var stats = new PingProbeStats();
        var cancelled = false;
        var timeout = TimeSpan.FromSeconds(request.TimeoutSeconds);

        for (var i = 0; i < request.Repeat; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (!_sessionRegistry.TryGetBySessionId(sessionId, out session) || session is null ||
                !_connectionTracker.TryGet(session.ConnectionId, out connection) || connection is null)
            {
                stats.RecordSession(success: false, TimeSpan.Zero);
                await WriteTextAsync(".", cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!_sessionPingTracker.TryRegister(session.SessionId, timeout, out var pending, out var error) ||
                pending is null)
            {
                await WriteTextAsync("\r\n" + (error ?? "% Session ping failed.") + "\r\n", cancellationToken)
                    .ConfigureAwait(false);
                break;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await connection.Protocol.WriteControlAsync(
                        MessageType.SessionPing,
                        new SessionPingMessage { CorrelationId = pending.CorrelationId },
                        cancellationToken)
                    .ConfigureAwait(false);

                var outcome = await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                sw.Stop();
                var ok = outcome.Success && !outcome.TimedOut;
                stats.RecordSession(ok, sw.Elapsed);
                await WriteTextAsync(ok ? "!" : ".", cancellationToken).ConfigureAwait(false);
                if (ok && !string.IsNullOrWhiteSpace(outcome.Hostname))
                {
                    hostname = outcome.Hostname;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _sessionPingTracker.Cancel(pending.CorrelationId);
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _sessionPingTracker.Cancel(pending.CorrelationId);
                stats.RecordSession(success: false, TimeSpan.Zero);
                await WriteTextAsync(".", cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(ex, "Session ping probe failed. SessionId={SessionId}", sessionId);
            }
        }

        var state = _sessionRegistry.TryGetBySessionId(sessionId, out _)
            ? "Connected"
            : "Disconnected";
        await WriteTextAsync(
                PingOutputFormatter.FormatSessionSummary(stats, state, cancelled),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Watches for Ctrl+C / abort during long-running diagnostics.
    /// </summary>
    private async Task WatchForDiagnosticAbortAsync(CancellationTokenSource diagnosticCts)
    {
        var buffer = new byte[1];
        try
        {
            while (!diagnosticCts.IsCancellationRequested)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(0, 1), diagnosticCts.Token)
                    .ConfigureAwait(false);
                if (read <= 0)
                {
                    diagnosticCts.Cancel();
                    return;
                }

                if (buffer[0] == 0x03)
                {
                    diagnosticCts.Cancel();
                    return;
                }

                // Discard other input during ping/traceroute (Cisco-style).
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            diagnosticCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            diagnosticCts.Cancel();
        }
    }

    /// <summary>
    /// Returns true for explicit yes, false for decline, null for cancel (Ctrl+C / SSH end).
    /// </summary>
    private async Task<bool?> PromptDisconnectConfirmationAsync(CancellationToken cancellationToken)
    {
        if (_lineReader is null)
        {
            return null;
        }

        const string confirmPrompt = "Confirm [y/N]:";
        _linePromptOverride = confirmPrompt;
        try
        {
            await WriteTextAsync(confirmPrompt + " ", cancellationToken).ConfigureAwait(false);
            LineReadResult read;
            try
            {
                read = await _lineReader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SshConnectionException)
            {
                return null;
            }
            catch (SshChannelException)
            {
                return null;
            }

            if (read.Outcome is LineReadOutcome.Cancelled or LineReadOutcome.Exit)
            {
                return null;
            }

            var answer = (read.Text ?? string.Empty).Trim();
            if (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
        finally
        {
            _linePromptOverride = null;
        }
    }

    /// <summary>
    /// Releases the operator attachment without terminating the connector (disconnect self).
    /// </summary>
    private async Task ExecuteReleaseAttachmentAsync(CancellationToken cancellationToken)
    {
        if (!_authorization.TryAuthorize(ActiveIdentity, CliCommandId.DisconnectSelf, out _))
        {
            await WriteTextAsync(OperatorAuthorizationService.PermissionDeniedMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _pendingBaudRequests.FailOperator(_operatorSessionId, "Operator disconnected.");
        if (_attachmentManager.TryDetachByOperatorSessionId(
                _operatorSessionId,
                AttachmentDetachReason.OperatorRequest,
                out var detached))
        {
            var name = detached!.Hostname ?? detached.SessionId;
            _audit.TryWrite(
                AuditEventType.OperatorDetached,
                AuditEventOutcome.Success,
                "Operator disconnected from connector attachment",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = detached.SessionId
                });
            if (_connectionTracker.TryGet(detached.ConnectionId, out var connection) &&
                connection is not null)
            {
                await PublishOperatorStatusAsync(
                        connection,
                        attached: false,
                        terminalActive: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            detached.Dispose();
            _operatorSessions.UpdateAttachment(_operatorSessionId, null);
            _operatorSessions.UpdateTerminalMode(_operatorSessionId, false);
            await WriteTextAsync($"Disconnected from {name}\r\n", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteTextAsync("% Not attached to a connector.\r\n", cancellationToken)
                .ConfigureAwait(false);
        }

        await WritePromptAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attach, apply complete serial configuration, then enter terminal on success.
    /// Configuration failure releases the attachment created by this attempt.
    /// </summary>
    private async Task ExecuteConnectAsync(
        string sessionId,
        ConnectSerialSettings settings,
        CancellationToken cancellationToken)
    {
        if (!_authorization.TryAuthorize(ActiveIdentity, CliCommandId.Connect, out _))
        {
            await WriteTextAsync(OperatorAuthorizationService.PermissionDeniedMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            await WriteTextAsync($"% Session '{sessionId}' not found.\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_connectionTracker.TryGet(session.ConnectionId, out var connection) || connection is null)
        {
            await WriteTextAsync("% Connector connection is no longer available.\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_attachmentManager.TryGetByOperatorSessionId(_operatorSessionId, out var existing) &&
            existing is not null)
        {
            await WriteTextAsync(
                    "% Already attached. Use 'disconnect' to release, then 'connect' again.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var attach = _attachmentManager.TryAttach(
            _operatorSessionId,
            session.SessionId,
            connection.ConnectionId,
            _operator);

        if (!attach.Succeeded)
        {
            _logger.LogWarning(
                "Attachment rejected. Operator={Operator}, SessionId={SessionId}, Reason={Reason}",
                _operatorIdentity,
                session.SessionId,
                attach.Error);
            await WriteTextAsync((attach.Error ?? "% Unable to attach.") + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var attachment = attach.Attachment!;
        _operatorSessions.UpdateAttachment(_operatorSessionId, attachment.SessionId);
        _logger.LogInformation(
            "Operator attached. Operator={Operator}, SessionId={SessionId}, AttachmentId={AttachmentId}",
            _operatorIdentity,
            attachment.SessionId,
            attachment.AttachmentId);
        _audit.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "Operator attached to connector",
            new AuditEventContext
            {
                ActorUsername = _operatorIdentity,
                SessionId = attachment.SessionId
            });
        await PublishOperatorStatusAsync(
                connection,
                attached: true,
                terminalActive: false,
                cancellationToken)
            .ConfigureAwait(false);

        var configOk = await ApplySerialConfigurationAsync(
                attachment,
                connection,
                settings,
                cancellationToken)
            .ConfigureAwait(false);
        if (!configOk)
        {
            await ReleaseConnectAttemptAsync(attachment, connection, cancellationToken).ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var displayName = attachment.Hostname ?? attachment.SessionId;
        await WriteTextAsync(
                $"Connected to {displayName} ({settings.FormatSummary()})\r\n" +
                "Press Ctrl+] to return to CLI (attachment kept; use 'attach' to resume, 'disconnect' to release).\r\n\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        await RunTerminalModeAsync(attachment, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resume serial terminal I/O for an existing attachment (after Ctrl+]). Does not reconfigure serial.
    /// </summary>
    private async Task ExecuteAttachAsync(CancellationToken cancellationToken)
    {
        if (!_authorization.TryAuthorize(ActiveIdentity, CliCommandId.Attach, out _))
        {
            await WriteTextAsync(OperatorAuthorizationService.PermissionDeniedMessage + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_attachmentManager.TryGetByOperatorSessionId(_operatorSessionId, out var attachment) ||
            attachment is null)
        {
            await WriteTextAsync(
                    "% Not attached to a connector. Use 'connect <session-id>' first.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_connectionTracker.TryGet(attachment.ConnectionId, out var connection) || connection is null)
        {
            await WriteTextAsync("% Connector connection is no longer available.\r\n", cancellationToken)
                .ConfigureAwait(false);
            // Stale attachment: release cleanly.
            if (_attachmentManager.TryDetachByOperatorSessionId(
                    _operatorSessionId,
                    AttachmentDetachReason.ConnectorDisconnect,
                    out var stale) &&
                stale is not null)
            {
                stale.Dispose();
            }

            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var displayName = attachment.Hostname ?? attachment.SessionId;
        await WriteTextAsync(
                $"Resuming terminal for {displayName}\r\nPress Ctrl+] to return to CLI (attachment kept; use 'disconnect' to release).\r\n\r\n",
                cancellationToken)
            .ConfigureAwait(false);

        await RunTerminalModeAsync(attachment, connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends SetBaud/serial configuration and waits for connector completion.
    /// </summary>
    private async Task<bool> ApplySerialConfigurationAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        ConnectSerialSettings settings,
        CancellationToken cancellationToken)
    {
        if (!_pendingBaudRequests.TryRegister(
                _operatorSessionId,
                attachment.SessionId,
                settings.BaudRate,
                out var pending,
                out var error) ||
            pending is null)
        {
            await WriteTextAsync((error ?? "% Unable to start serial configuration.") + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["baud"] = settings.BaudRate.ToString(CultureInfo.InvariantCulture),
            ["databits"] = settings.DataBits.ToString(CultureInfo.InvariantCulture),
            ["parity"] = settings.Parity,
            ["stopbits"] = settings.StopBits
        };

        _audit.TryWrite(
            AuditEventType.BaudRequestStarted,
            AuditEventOutcome.Informational,
            "Remote serial configuration request initiated",
            new AuditEventContext
            {
                ActorUsername = _operatorIdentity,
                SessionId = attachment.SessionId,
                Details = details
            });

        try
        {
            await connection.Protocol.WriteControlAsync(
                    MessageType.SetBaud,
                    new SetBaudMessage
                    {
                        CorrelationId = pending.CorrelationId,
                        BaudRate = settings.BaudRate,
                        DataBits = settings.DataBits,
                        Parity = settings.Parity,
                        StopBits = settings.StopBits,
                        Handshake = settings.Handshake
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pendingBaudRequests.FailOperator(_operatorSessionId, "Failed to send serial configuration request.");
            _logger.LogWarning(ex, "Failed to send SetBaud. SessionId={SessionId}", attachment.SessionId);
            _audit.TryWrite(
                AuditEventType.BaudRequestFailed,
                AuditEventOutcome.Failure,
                "Remote serial configuration request failed to send",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = attachment.SessionId,
                    Details = details
                });
            await WriteTextAsync(
                    "Serial configuration failed: Unable to send request to connector.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
            return false;
        }

        PendingBaudResult outcome;
        try
        {
            outcome = await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _pendingBaudRequests.FailOperator(_operatorSessionId, "Operator SSH session ended.");
            throw;
        }

        if (outcome.Success)
        {
            _audit.TryWrite(
                AuditEventType.BaudRequestSucceeded,
                AuditEventOutcome.Success,
                "Remote serial configuration succeeded",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = attachment.SessionId,
                    Details = details
                });
            return true;
        }

        var eventType = outcome.TimedOut
            ? AuditEventType.BaudRequestTimedOut
            : AuditEventType.BaudRequestFailed;
        _audit.TryWrite(
            eventType,
            AuditEventOutcome.Failure,
            outcome.TimedOut
                ? "Remote serial configuration timed out"
                : "Remote serial configuration failed",
            new AuditEventContext
            {
                ActorUsername = _operatorIdentity,
                SessionId = attachment.SessionId,
                Details = details
            });
        await WriteTextAsync(
                $"Serial configuration failed: {outcome.Error ?? "Unknown error."}\r\n",
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Cleans up a failed or cancelled connect attempt.
    /// </summary>
    private async Task ReleaseConnectAttemptAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        CancellationToken cancellationToken)
    {
        _pendingBaudRequests.FailOperator(_operatorSessionId, "Connect serial configuration failed.");
        if (_attachmentManager.TryDetachByOperatorSessionId(
                _operatorSessionId,
                AttachmentDetachReason.OperatorRequest,
                out var detached) &&
            detached is not null)
        {
            _audit.TryWrite(
                AuditEventType.OperatorDetached,
                AuditEventOutcome.Informational,
                "Operator attachment released after failed connect configuration",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = detached.SessionId
                });
            try
            {
                await PublishOperatorStatusAsync(
                        connection,
                        attached: false,
                        terminalActive: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "OperatorStatus publish skipped during connect failure cleanup");
            }

            detached.Dispose();
        }
        else
        {
            attachment.Dispose();
        }
    }

    /// <summary>
    /// Pumps operator input and connector serial/protocol data until Ctrl+] or detach.
    /// </summary>
    private async Task RunTerminalModeAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        CancellationToken cancellationToken)
    {
        var displayName = attachment.Hostname ?? attachment.SessionId;
        attachment.IsTerminalActive = true;
        _operatorSessions.UpdateAttachment(_operatorSessionId, attachment.SessionId);
        _operatorSessions.UpdateTerminalMode(_operatorSessionId, true);
        await PublishOperatorStatusAsync(
                connection,
                attached: true,
                terminalActive: true,
                cancellationToken)
            .ConfigureAwait(false);
        _audit.TryWrite(
            AuditEventType.TerminalModeEntered,
            AuditEventOutcome.Informational,
            "Terminal mode entered",
            new AuditEventContext
            {
                ActorUsername = _operatorIdentity,
                SessionId = attachment.SessionId
            });
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            attachment.DetachCts.Token);

        var pumpTask = PumpOperatorOutboundAsync(attachment, linked.Token);
        var leaveReason = AttachmentDetachReason.OperatorRequest;
        try
        {
            leaveReason = await ForwardOperatorInputAsync(attachment, connection, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            leaveReason = attachment.DetachReason ?? AttachmentDetachReason.ConnectorDisconnect;
        }
        catch (IOException)
        {
            leaveReason = AttachmentDetachReason.OperatorDisconnect;
        }
        catch (ObjectDisposedException)
        {
            leaveReason = AttachmentDetachReason.OperatorDisconnect;
        }
        finally
        {
            // Stop terminal RX delivery before cancelling the pump so the queue cannot overflow.
            attachment.IsTerminalActive = false;
            _operatorSessions.UpdateTerminalMode(_operatorSessionId, false);
            attachment.ToOperator.DropAllQueued();
            _audit.TryWrite(
                AuditEventType.TerminalModeExited,
                AuditEventOutcome.Informational,
                "Terminal mode exited",
                new AuditEventContext
                {
                    ActorUsername = _operatorIdentity,
                    SessionId = attachment.SessionId
                });

            try
            {
                linked.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                await pumpTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Operator outbound pump ended with error");
            }
        }

        // Ctrl+] / privilege drop leaves terminal but keeps attachment. Forced detach clears ownership.
        if (leaveReason is AttachmentDetachReason.OperatorRequest)
        {
            await PublishOperatorStatusAsync(
                    connection,
                    attached: true,
                    terminalActive: false,
                    cancellationToken)
                .ConfigureAwait(false);
            await WriteTextAsync(
                    $"\r\nReturned to CLI (still attached to {displayName}). Use 'attach' to resume or 'disconnect' to release.\r\n",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            if (_attachmentManager.TryDetachByOperatorSessionId(
                    _operatorSessionId,
                    leaveReason,
                    out var detached))
            {
                if (leaveReason is AttachmentDetachReason.OperatorDisconnect)
                {
                    _audit.TryWrite(
                        AuditEventType.AttachmentReleasedSshDisconnect,
                        AuditEventOutcome.Informational,
                        "Attachment released after SSH disconnect",
                        new AuditEventContext
                        {
                            ActorUsername = _operatorIdentity,
                            SessionId = detached!.SessionId
                        });
                }
                else if (leaveReason is AttachmentDetachReason.ConnectorDisconnect)
                {
                    _audit.TryWrite(
                        AuditEventType.AttachmentReleasedConnectorDisconnect,
                        AuditEventOutcome.Informational,
                        "Attachment released after connector disconnect",
                        new AuditEventContext
                        {
                            ActorUsername = _operatorIdentity,
                            SessionId = detached!.SessionId
                        });
                }

                detached!.Dispose();
                await PublishOperatorStatusAsync(
                        connection,
                        attached: false,
                        terminalActive: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Already removed by connector disconnect path (audited there).
            }

            var message = leaveReason switch
            {
                AttachmentDetachReason.ConnectorDisconnect =>
                    $"\r\nDetached from {displayName} (connector disconnected)\r\n",
                AttachmentDetachReason.OperatorBackpressure =>
                    $"\r\nDetached from {displayName} (terminal backpressure)\r\n",
                AttachmentDetachReason.FatalForwardingFailure =>
                    $"\r\nDetached from {displayName} (forwarding failure)\r\n",
                AttachmentDetachReason.Shutdown =>
                    $"\r\nDetached from {displayName} (server shutdown)\r\n",
                _ => $"\r\nDetached from {displayName}\r\n"
            };
            try
            {
                await WriteTextAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await WritePromptAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Forwards operator keystrokes to the connector; treats 0x1D as detach (not forwarded).
    /// </summary>
    private async Task<AttachmentDetachReason> ForwardOperatorInputAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return attachment.DetachReason ?? AttachmentDetachReason.ConnectorDisconnect;
            }

            if (read == 0)
            {
                return AttachmentDetachReason.OperatorDisconnect;
            }

            var span = buffer.AsSpan(0, read);
            var detachIndex = span.IndexOf(TechConnectCliDispatcher.DetachControlByte);
            if (detachIndex < 0)
            {
                await ForwardTxAsync(attachment, connection, buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (detachIndex > 0)
            {
                await ForwardTxAsync(
                        attachment,
                        connection,
                        buffer.AsMemory(0, detachIndex),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Ctrl+]: leave terminal; keep attachment for show serial / attach.
            return AttachmentDetachReason.OperatorRequest;
        }

        return attachment.DetachReason ?? AttachmentDetachReason.OperatorDisconnect;
    }

    /// <summary>
    /// Forwards operator TX to the connector. Privilege for terminal entry is enforced before
    /// entering this mode; basic serial console access is allowed at privilege 1+.
    /// </summary>
    private async Task ForwardTxAsync(
        OperatorAttachment attachment,
        ConnectorConnection connection,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (data.IsEmpty)
        {
            return;
        }

        attachment.AddBytesFromOperator(data.Length);
        try
        {
            await connection.Protocol.WriteSerialDataAsync(data, cancellationToken).ConfigureAwait(false);
            attachment.AddBytesToDevice(data.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Failed to forward operator TX to connector. SessionId={SessionId}",
                attachment.SessionId);
            if (_attachmentManager.TryDetachByOperatorSessionId(
                    _operatorSessionId,
                    AttachmentDetachReason.FatalForwardingFailure,
                    out _))
            {
                await PublishOperatorStatusAsync(
                        connection,
                        attached: false,
                        terminalActive: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Pumps outbound operator-status/protocol notifications during terminal mode.
    /// </summary>
    private async Task PumpOperatorOutboundAsync(
        OperatorAttachment attachment,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var chunk = await attachment.ToOperator.DequeueAsync(cancellationToken).ConfigureAwait(false);
                if (chunk is null)
                {
                    break;
                }

                await WriteRawAsync(chunk, cancellationToken).ConfigureAwait(false);
                attachment.AddBytesToOperator(chunk.Length);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            _attachmentManager.TryDetachByOperatorSessionId(
                _operatorSessionId,
                AttachmentDetachReason.OperatorDisconnect,
                out _);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Writes the current CLI prompt to the SSH channel.
    /// </summary>
    private async Task WritePromptAsync(CancellationToken cancellationToken) =>
        await WriteTextAsync(GetPrompt() + " ", cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Writes UTF-8 text to the SSH channel under the write gate.
    /// </summary>
    private async Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await WriteRawAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes raw bytes to the SSH channel under the write gate.
    /// </summary>
    /// <remarks>
    /// Production uses <c>Microsoft.DevTunnels.Ssh.SshStream</c>: <c>WriteAsync</c> already
    /// calls <c>Channel.SendAsync</c> (data is sent immediately), and <c>Flush</c> is a no-op
    /// (<c>CheckDisposed</c> only). Awaiting <c>FlushAsync</c> after every write added measurable
    /// Task/await overhead without changing on-wire behaviour (PERF-SRV-03).
    /// </remarks>
    private async Task WriteRawAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Write callback used by InteractiveLineReader (gated raw write).
    /// </summary>
    private ValueTask WriteRawValueAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        new(WriteRawAsync(bytes, cancellationToken));

    /// <summary>
    /// Publishes OperatorStatus protocol frames on the connector path for attach/terminal transitions.
    /// </summary>
    private async Task PublishOperatorStatusAsync(
        ConnectorConnection connection,
        bool attached,
        bool terminalActive,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.OperatorStatus.PublishAsync(attached, terminalActive, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "OperatorStatus publish failed. ConnectionId={ConnectionId}, Attached={Attached}, TerminalActive={TerminalActive}",
                connection.ConnectionId,
                attached,
                terminalActive);
        }
    }
}
