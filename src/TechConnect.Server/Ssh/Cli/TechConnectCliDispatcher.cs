// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TechConnectCliDispatcher.cs
//
// Purpose:
// Parses and authorizes CLI lines into CliCommandResult actions without
// performing attachment or terminal I/O.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Text;
using TechConnect.Protocol;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Copy;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Inspection;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// In-process TechConnect CLI command parser with centralized authorization.
/// </summary>
/// <remarks>
/// Attachment / baud / terminal I/O execution happens in
/// <see cref="TechConnectCliSession"/>. Authorization uses
/// <see cref="OperatorAuthorizationService"/> which consults
/// <c>CliCommandPolicy</c> (default-deny; cumulative privilege ≥ minimum).
/// Notable minima: most show/connect/attach/ping/traceroute/copy/enable/disable = 1;
/// <c>show serial</c> and administrative <c>disconnect &lt;session&gt;</c> = 7;
/// <c>show history session</c> = 5; <c>show history user</c> = 11.
/// Ctrl+] (<see cref="DetachControlByte"/>) is handled in terminal mode, not here.
/// </remarks>
internal sealed class TechConnectCliDispatcher
{
    /// <summary>Banner written once when an SSH CLI session starts.</summary>
    public const string WelcomeBanner = "Connected to NMS TechConnect Server";

    /// <summary>CLI prompt when active privilege is not privileged-exec.</summary>
    public const string OperatorPrompt = "TechConnect>";

    /// <summary>CLI prompt when active privilege is privileged-exec (enable).</summary>
    public const string AdministratorPrompt = "TechConnect#";

    /// <summary>Legacy alias for operator/read-only prompt.</summary>
    public const string Prompt = OperatorPrompt;

    /// <summary>
    /// ASCII GS (0x1D) / Ctrl+] — leave serial terminal mode and return to CLI.
    /// Does not release attachment and is never forwarded to the device.
    /// </summary>
    public const byte DetachControlByte = 0x1D;

    /// <summary>
    /// Selects the CLI prompt for the effective privilege level.
    /// </summary>
    /// <param name="privilege">Active privilege.</param>
    /// <returns><see cref="AdministratorPrompt"/> or <see cref="OperatorPrompt"/>.</returns>
    public static string GetPrompt(PrivilegeLevel privilege) =>
        privilege.IsPrivilegedExec ? AdministratorPrompt : OperatorPrompt;

    /// <summary>
    /// Formats a Cisco-style caret + <c>% Invalid command</c> under the prompt and input.
    /// </summary>
    /// <param name="prompt">Current prompt text.</param>
    /// <param name="input">Rejected input line.</param>
    /// <returns>Two-line error display string.</returns>
    public static string FormatInvalidCommand(string prompt, string input)
    {
        var text = input ?? string.Empty;
        var errorIndex = 0;
        while (errorIndex < text.Length && char.IsWhiteSpace(text[errorIndex]))
        {
            errorIndex++;
        }

        var caretColumn = prompt.Length + 1 + errorIndex;
        return new string(' ', caretColumn) + "^\r\n% Invalid command";
    }

    /// <summary>Connector session registry for show/connect lookups.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Attachment manager for serial/show serial context.</summary>
    private readonly OperatorAttachmentManager _attachmentManager;

    /// <summary>Serial configuration cache.</summary>
    private readonly ConnectorSerialConfigurationCache _serialConfigCache;

    /// <summary>Pending baud request tracker (connect path metadata).</summary>
    private readonly PendingBaudRequestTracker _pendingBaudRequests;

    /// <summary>Centralized command authorization.</summary>
    private readonly OperatorAuthorizationService _authorization;

    /// <summary>Audit reader for show history.</summary>
    private readonly IAuditEventReader _auditReader;

    /// <summary>Clock for show clock / history timestamps.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Local address availability for diagnostics display helpers.</summary>
    private readonly IIPv6NetworkAvailability _ipv6Availability;

    /// <summary>Application version/info provider.</summary>
    private readonly IApplicationInfoProvider _applicationInfo;

    /// <summary>DNS configuration provider for show dns.</summary>
    private readonly IDnsConfigurationProvider _dnsConfiguration;

    /// <summary>Network interface inspector for show ip interfaces.</summary>
    private readonly INetworkInterfaceInspector _interfaces;

    /// <summary>Routing table inspector for show ip route.</summary>
    private readonly IRoutingTableInspector _routes;

    /// <summary>Operator CLI presence tracker for show users/terminal.</summary>
    private readonly SshOperatorSessionTracker _operatorSessions;

    /// <summary>Configuration views for show running-config/startup-config (null when unavailable).</summary>
    private readonly ConfigurationManagementService? _configuration;

    /// <summary>
    /// Initializes the dispatcher with required registries and optional test overrides.
    /// </summary>
    /// <param name="sessionRegistry">Connector session registry.</param>
    /// <param name="attachmentManager">Attachment manager.</param>
    /// <param name="serialConfigCache">Serial configuration cache.</param>
    /// <param name="authorization">Authorization service.</param>
    /// <param name="pendingBaudRequests">Optional pending baud tracker.</param>
    /// <param name="timeProvider">Optional time provider.</param>
    /// <param name="auditReader">Optional audit reader.</param>
    /// <param name="ipv6Availability">Optional IPv6 availability.</param>
    /// <param name="applicationInfo">Optional application info provider.</param>
    /// <param name="dnsConfiguration">Optional DNS configuration provider.</param>
    /// <param name="interfaces">Optional interface inspector.</param>
    /// <param name="routes">Optional routing inspector.</param>
    /// <param name="operatorSessions">Optional operator session tracker.</param>
    /// <param name="configuration">Optional configuration management service.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public TechConnectCliDispatcher(
        ConnectorSessionRegistry sessionRegistry,
        OperatorAttachmentManager attachmentManager,
        ConnectorSerialConfigurationCache serialConfigCache,
        OperatorAuthorizationService authorization,
        PendingBaudRequestTracker? pendingBaudRequests = null,
        TimeProvider? timeProvider = null,
        IAuditEventReader? auditReader = null,
        IIPv6NetworkAvailability? ipv6Availability = null,
        IApplicationInfoProvider? applicationInfo = null,
        IDnsConfigurationProvider? dnsConfiguration = null,
        INetworkInterfaceInspector? interfaces = null,
        IRoutingTableInspector? routes = null,
        SshOperatorSessionTracker? operatorSessions = null,
        ConfigurationManagementService? configuration = null)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _attachmentManager = attachmentManager ?? throw new ArgumentNullException(nameof(attachmentManager));
        _serialConfigCache = serialConfigCache ?? throw new ArgumentNullException(nameof(serialConfigCache));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _pendingBaudRequests = pendingBaudRequests ?? new PendingBaudRequestTracker();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _auditReader = auditReader ?? new InMemoryAuditEventStore(
            enabled: true,
            maximumEvents: InMemoryAuditEventStore.DefaultMaximumEvents,
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InMemoryAuditEventStore>.Instance);
        _ipv6Availability = ipv6Availability ?? new SystemIPv6NetworkAvailability();
        _applicationInfo = applicationInfo ?? new AssemblyApplicationInfoProvider(_timeProvider);
        _dnsConfiguration = dnsConfiguration ?? new EmptyDnsConfigurationProvider();
        _interfaces = interfaces ?? new SystemNetworkInterfaceInspector();
        _routes = routes ?? new UnsupportedRoutingTableInspector();
        _operatorSessions = operatorSessions ?? new SshOperatorSessionTracker();
        _configuration = configuration;
    }

    /// <summary>
    /// Parses, authorizes, and maps a CLI input line to a <see cref="CliCommandResult"/>.
    /// </summary>
    /// <param name="input">Raw input line; blank returns <see cref="CliCommandAction.Continue"/>.</param>
    /// <param name="loginIdentity">Immutable login identity (username + original privilege).</param>
    /// <param name="activePrivilege">Effective privilege for authorization and prompt.</param>
    /// <param name="selfSession">Optional live CLI session record for <c>show terminal</c>.</param>
    /// <param name="terminalColumns">Terminal width for help/list formatting.</param>
    /// <returns>Action + optional payload for <see cref="TechConnectCliSession"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loginIdentity"/> is <see langword="null"/>.</exception>
    public CliCommandResult Execute(
        string? input,
        AuthenticatedOperator loginIdentity,
        PrivilegeLevel? activePrivilege = null,
        OperatorCliSessionRecord? selfSession = null,
        int terminalColumns = ConnectorSessionDisplayFormatter.DefaultColumns)
    {
        ArgumentNullException.ThrowIfNull(loginIdentity);
        var active = activePrivilege ?? loginIdentity.Privilege;
        var identity = loginIdentity with { Privilege = active };

        if (string.IsNullOrWhiteSpace(input))
        {
            return new CliCommandResult(CliCommandAction.Continue);
        }

        var parsed = CliCommandParser.Parse(input);
        if (parsed.Status == CliParseStatus.Invalid)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                FormatInvalidCommand(GetPrompt(active), input));
        }

        if (parsed.Status == CliParseStatus.Incomplete)
        {
            if (parsed.Node?.CommandId == CliCommandId.Copy)
            {
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    "% Usage: copy {http:|https:|tftp:|scp:|xmodem:|ymodem:} serial:<session-id>");
            }

            return new CliCommandResult(CliCommandAction.Continue, "% Incomplete command");
        }

        var commandId = parsed.CommandId;
        var tokens = parsed.Tokens;

        if (!_authorization.TryAuthorize(identity, commandId, out _))
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                OperatorAuthorizationService.PermissionDeniedMessage);
        }

        return commandId switch
        {
            CliCommandId.Help => new CliCommandResult(
                CliCommandAction.Continue,
                CliCommandPolicy.BuildHelpText(active, terminalColumns)),
            CliCommandId.Exit => new CliCommandResult(CliCommandAction.Exit, "Goodbye."),
            CliCommandId.ClearScreen => new CliCommandResult(CliCommandAction.ClearScreen),
            CliCommandId.DisconnectSelf => new CliCommandResult(CliCommandAction.ReleaseAttachment),
            CliCommandId.Attach => new CliCommandResult(CliCommandAction.Attach),
            CliCommandId.Enable => ExecuteEnable(tokens),
            CliCommandId.Disable => new CliCommandResult(CliCommandAction.Disable),
            CliCommandId.ShowVersion => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatVersion(_applicationInfo.GetInfo())),
            CliCommandId.ShowPrivilege => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatPrivilege(loginIdentity, active)),
            CliCommandId.ShowClock => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatClock(_timeProvider.GetUtcNow(), TimeZoneInfo.Local)),
            CliCommandId.ShowTerminal => ExecuteShowTerminal(loginIdentity, active, selfSession),
            CliCommandId.ShowUsers => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatUsers(_operatorSessions.ListCliSessions())),
            CliCommandId.ShowSessions or CliCommandId.ShowSessionsDetail or CliCommandId.ShowSession
                or CliCommandId.ShowSerial =>
                ExecuteShow(commandId, tokens, loginIdentity.OperatorSessionId, terminalColumns),
            CliCommandId.ShowHistory or CliCommandId.ShowHistorySession or CliCommandId.ShowHistoryUser =>
                ExecuteShowHistory(commandId, tokens, loginIdentity, terminalColumns),
            CliCommandId.ShowDns => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatDnsConfiguration(_dnsConfiguration.GetConfiguration())),
            CliCommandId.ShowIpInterfaces => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatInterfaces(_interfaces.GetInterfaces())),
            CliCommandId.ShowIpRoute => new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatRoutes(_routes.GetRoutes())),
            CliCommandId.Nslookup => ExecuteNslookup(tokens),
            CliCommandId.Connect => ExecuteConnect(tokens),
            CliCommandId.Disconnect => ExecuteAdminDisconnect(tokens),
            CliCommandId.Ping => ExecutePing(tokens),
            CliCommandId.Traceroute => ExecuteTraceroute(tokens),
            CliCommandId.Copy => ExecuteCopy(tokens, loginIdentity.OperatorSessionId),
            CliCommandId.ConfigureTerminal => ExecuteConfigure(tokens),
            CliCommandId.Write => ExecuteWriteOutsideConfiguration(tokens),
            CliCommandId.ShowRunningConfig => new CliCommandResult(
                CliCommandAction.Continue,
                _configuration?.RenderRunning() ?? ConfigurationUnavailableMessage),
            CliCommandId.ShowStartupConfig => new CliCommandResult(
                CliCommandAction.Continue,
                _configuration?.RenderStartup() ?? ConfigurationUnavailableMessage),
            _ => new CliCommandResult(
                CliCommandAction.Continue,
                OperatorAuthorizationService.PermissionDeniedMessage)
        };
    }

    /// <summary>Shown when the host did not provide configuration management.</summary>
    internal const string ConfigurationUnavailableMessage = "% Configuration management is not available.";

    /// <summary>
    /// <c>configure [terminal]</c>: any prefix of <c>terminal</c> is accepted; other sources are rejected.
    /// </summary>
    private static CliCommandResult ExecuteConfigure(string[] tokens)
    {
        if (tokens.Length > 1 &&
            !"terminal".StartsWith(tokens[1], StringComparison.OrdinalIgnoreCase))
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Invalid input detected: configuration source is not supported. Use 'configure terminal'.");
        }

        return new CliCommandResult(CliCommandAction.EnterConfiguration);
    }

    /// <summary>
    /// <c>write [memory]</c> at the exec prompt: changes exist only inside a configuration session.
    /// </summary>
    private static CliCommandResult ExecuteWriteOutsideConfiguration(string[] tokens)
    {
        if (tokens.Length > 1 &&
            !"memory".StartsWith(tokens[1], StringComparison.OrdinalIgnoreCase))
        {
            return new CliCommandResult(CliCommandAction.Continue, "% Invalid input detected: use 'write' or 'write memory'.");
        }

        return new CliCommandResult(
            CliCommandAction.Continue,
            "% No unsaved configuration changes. Use 'configure terminal', make changes, then 'write' before 'end'.");
    }

    /// <summary>
    /// Validates copy source/destination tokens and returns <see cref="CliCommandAction.Copy"/>.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <param name="operatorSessionId">Calling operator session id (must be attached).</param>
    /// <returns>Copy action or continue with an error message.</returns>
    private CliCommandResult ExecuteCopy(string[] tokens, string operatorSessionId)
    {
        if (tokens.Length < 3)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Usage: copy {http:|https:|tftp:|scp:|xmodem:|ymodem:} serial:<session-id>");
        }

        // tokens[0]=copy, tokens[1]=source, tokens[2]=destination (URLs may be one token)
        var sourceToken = tokens[1];
        var destToken = tokens[^1];
        if (tokens.Length > 3)
        {
            // Source URL must be a single token; extra middle tokens are invalid.
            // Allow: copy <source> serial:<id>
            if (tokens.Length != 3)
            {
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    "% Usage: copy <source> serial:<session-id>");
            }
        }

        if (!CopySourceParser.TryParseDestination(destToken, out var sessionId, out var destError))
        {
            return new CliCommandResult(CliCommandAction.Continue, destError);
        }

        if (!CopySourceParser.TryParse(sourceToken, out var source, out var sourceError, out var needsInteractive) ||
            source is null)
        {
            return new CliCommandResult(CliCommandAction.Continue, sourceError ?? "% Invalid copy source.");
        }

        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            return new CliCommandResult(CliCommandAction.Continue, $"% Session '{sessionId}' not found.");
        }

        if (!_attachmentManager.TryGetByOperatorSessionId(operatorSessionId, out var attachment) ||
            attachment is null)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% No serial attachment. Connect and remain attached (Ctrl+] keeps attachment) before copy.");
        }

        if (!string.Equals(attachment.SessionId, session.SessionId, StringComparison.Ordinal))
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% serial:<session-id> must match your currently attached connector session.");
        }

        // Destination validated; interactive completion / transfer runs in the session.
        _ = needsInteractive;
        return new CliCommandResult(
            CliCommandAction.Copy,
            CopyRequest: new CopyRequest
            {
                Source = source,
                SessionId = session.SessionId
            });
    }

    /// <summary>
    /// Formats <c>show terminal</c> using the live self session record when provided.
    /// </summary>
    /// <param name="login">Login identity.</param>
    /// <param name="active">Active privilege.</param>
    /// <param name="selfSession">Optional live presence record.</param>
    /// <returns>Continue with formatted terminal output.</returns>
    private CliCommandResult ExecuteShowTerminal(
        AuthenticatedOperator login,
        PrivilegeLevel active,
        OperatorCliSessionRecord? selfSession)
    {
        var record = selfSession ?? new OperatorCliSessionRecord
        {
            OperatorSessionId = login.OperatorSessionId,
            Username = login.Username,
            LoginPrivilege = login.Privilege,
            ActivePrivilege = active,
            ConnectedAtUtc = login.AuthenticatedAtUtc,
            Columns = ConnectorSessionDisplayFormatter.DefaultColumns,
            Rows = SshTerminalInfo.DefaultRows
        };
        return new CliCommandResult(CliCommandAction.Continue, NocDisplayFormatter.FormatTerminal(record));
    }

    /// <summary>
    /// Validates <c>nslookup</c> usage and returns <see cref="CliCommandAction.Nslookup"/>.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>Nslookup action or continue with usage error.</returns>
    private static CliCommandResult ExecuteNslookup(string[] tokens)
    {
        if (tokens.Length != 2 || string.IsNullOrWhiteSpace(tokens[1]))
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Usage: nslookup <hostname|ip-address>");
        }

        return new CliCommandResult(CliCommandAction.Nslookup, NslookupHostname: tokens[1].Trim());
    }

    /// <summary>
    /// Parses ping tokens and validates IPv6/source availability before returning <see cref="CliCommandAction.Ping"/>.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>Ping action or continue with an error message.</returns>
    private CliCommandResult ExecutePing(string[] tokens)
    {
        if (!PingCommandParser.TryParse(tokens, out var request, out var error) ||
            request is null)
        {
            return new CliCommandResult(CliCommandAction.Continue, error ?? "% Invalid ping command");
        }

        if (request.Kind == PingTargetKind.Ipv6 && !_ipv6Availability.IsIpv6PingAvailable)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% IPv6 ping is unavailable: no usable global IPv6 address with an IPv6 default gateway.");
        }

        if (request.Source is not null)
        {
            if (request.Kind == PingTargetKind.Hostname)
            {
                var local = request.Source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? _ipv6Availability.GetLocalGlobalIpv6Addresses()
                    : _ipv6Availability.GetLocalIpv4Addresses();
                if (!local.Any(a => a.Equals(request.Source)))
                {
                    return new CliCommandResult(
                        CliCommandAction.Continue,
                        "% Source address is not assigned to a local interface.");
                }
            }
            else
            {
                var local = request.Kind == PingTargetKind.Ipv6
                    ? _ipv6Availability.GetLocalGlobalIpv6Addresses()
                    : _ipv6Availability.GetLocalIpv4Addresses();
                if (!local.Any(a => a.Equals(request.Source)))
                {
                    return new CliCommandResult(
                        CliCommandAction.Continue,
                        "% Source address is not assigned to a local interface.");
                }
            }
        }

        return new CliCommandResult(CliCommandAction.Ping, PingRequest: request);
    }

    /// <summary>
    /// Parses traceroute tokens and validates IPv6/source availability before returning the action.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>Traceroute action or continue with an error message.</returns>
    private CliCommandResult ExecuteTraceroute(string[] tokens)
    {
        if (!TracerouteCommandParser.TryParse(tokens, out var request, out var error) ||
            request is null)
        {
            return new CliCommandResult(CliCommandAction.Continue, error ?? "% Invalid traceroute command");
        }

        if (!request.RequiresResolution &&
            request.Family == TracerouteAddressFamily.Ipv6 &&
            !_ipv6Availability.IsIpv6PingAvailable)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% IPv6 traceroute is unavailable: no usable global IPv6 address with an IPv6 default gateway.");
        }

        if (request.Source is not null && !request.RequiresResolution)
        {
            var local = request.Family == TracerouteAddressFamily.Ipv6
                ? _ipv6Availability.GetLocalGlobalIpv6Addresses()
                : _ipv6Availability.GetLocalIpv4Addresses();
            if (!local.Any(a => a.Equals(request.Source)))
            {
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    "% Source address is not assigned to a local interface.");
            }
        }
        else if (request.Source is not null)
        {
            var local = request.Source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? _ipv6Availability.GetLocalGlobalIpv6Addresses()
                : _ipv6Availability.GetLocalIpv4Addresses();
            if (!local.Any(a => a.Equals(request.Source)))
            {
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    "% Source address is not assigned to a local interface.");
            }
        }

        return new CliCommandResult(CliCommandAction.Traceroute, TracerouteRequest: request);
    }

    /// <summary>
    /// Parses optional enable level (default maximum) into <see cref="CliCommandAction.Enable"/>.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>Enable action or continue with a validation error.</returns>
    private static CliCommandResult ExecuteEnable(string[] tokens)
    {
        var level = PrivilegeLevel.Maximum;
        if (tokens.Length == 2)
        {
            if (!int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out level) ||
                !PrivilegeLevel.IsValid(level) ||
                !string.Equals(
                    tokens[1],
                    level.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal))
            {
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    $"% Invalid privilege level. Use an integer {PrivilegeLevel.Minimum}-{PrivilegeLevel.Maximum}.");
            }
        }
        else if (tokens.Length != 1)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Usage: enable [level]");
        }

        return new CliCommandResult(CliCommandAction.Enable, EnableTargetLevel: level);
    }

    /// <summary>
    /// Validates administrative <c>disconnect &lt;session-id&gt;</c> (policy minimum privilege 7).
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>AdminDisconnect action or continue with an error.</returns>
    private CliCommandResult ExecuteAdminDisconnect(string[] tokens)
    {
        if (tokens.Length != 2)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Usage: disconnect <session-id>");
        }

        var sessionId = tokens[1];
        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                $"Session {sessionId} is not connected.");
        }

        return new CliCommandResult(
            CliCommandAction.AdminDisconnect,
            DisconnectSessionId: session.SessionId);
    }

    /// <summary>
    /// Executes show history variants with privilege-specific scopes already authorized upstream.
    /// </summary>
    /// <remarks>
    /// Policy minima: <c>show history</c> = 1 (own user); <c>show history session</c> = 5;
    /// <c>show history user</c> = 11.
    /// </remarks>
    /// <param name="commandId">History command id.</param>
    /// <param name="tokens">Parsed tokens.</param>
    /// <param name="loginIdentity">Login identity (own-history filter).</param>
    /// <param name="terminalColumns">Terminal width for formatting.</param>
    /// <returns>Continue with formatted history or an error.</returns>
    private CliCommandResult ExecuteShowHistory(
        CliCommandId commandId,
        string[] tokens,
        AuthenticatedOperator loginIdentity,
        int terminalColumns)
    {
        if (!_auditReader.IsEnabled)
        {
            return new CliCommandResult(CliCommandAction.Continue, AuditHistoryFormatter.FormatDisabled());
        }

        IReadOnlyList<AuditEvent> events;
        string? emptyFilter = null;

        switch (commandId)
        {
            case CliCommandId.ShowHistory:
                // Privilege 1+: only the invoking operator's own history.
                emptyFilter = $"user '{loginIdentity.Username}'";
                events = _auditReader.GetByUsername(loginIdentity.Username, AuditHistoryFormatter.DefaultLimit);
                break;

            case CliCommandId.ShowHistorySession:
                if (tokens.Length != 4)
                {
                    return new CliCommandResult(
                        CliCommandAction.Continue,
                        "% Usage: show history session <session-id>");
                }

                emptyFilter = $"session '{tokens[3]}'";
                events = _auditReader.GetBySessionId(tokens[3], AuditHistoryFormatter.DefaultLimit);
                break;

            case CliCommandId.ShowHistoryUser:
                if (tokens.Length != 4)
                {
                    return new CliCommandResult(
                        CliCommandAction.Continue,
                        "% Usage: show history user <username>");
                }

                emptyFilter = $"user '{tokens[3]}'";
                events = _auditReader.GetByUsername(tokens[3], AuditHistoryFormatter.DefaultLimit);
                break;

            default:
                return new CliCommandResult(
                    CliCommandAction.Continue,
                    OperatorAuthorizationService.PermissionDeniedMessage);
        }

        var output = AuditHistoryFormatter.Format(
            events,
            _auditReader.HasEvictedEvents,
            _auditReader.MaximumEvents,
            terminalColumns,
            emptyFilter);

        return new CliCommandResult(CliCommandAction.Continue, output);
    }

    /// <summary>
    /// Parses <c>connect</c> session id and optional serial settings into <see cref="CliCommandAction.Connect"/>.
    /// </summary>
    /// <param name="tokens">Parsed command tokens.</param>
    /// <returns>Connect action or continue with an error.</returns>
    private CliCommandResult ExecuteConnect(string[] tokens)
    {
        if (tokens.Length < 2)
        {
            return new CliCommandResult(
                CliCommandAction.Continue,
                "% Usage: connect <session-id> [baud <rate>] [databits <n>] [parity <x>] [stopbits <x>]");
        }

        var sessionId = tokens[1];
        var serialTokens = tokens.Length > 2 ? tokens.AsSpan(2).ToArray() : [];
        if (!ConnectSerialSettings.TryParse(serialTokens, out var settings, out var serialError))
        {
            return new CliCommandResult(CliCommandAction.Continue, serialError);
        }

        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            return new CliCommandResult(CliCommandAction.Continue, $"% Session '{sessionId}' not found.");
        }

        return new CliCommandResult(
            CliCommandAction.Connect,
            ConnectSessionId: session.SessionId,
            SerialSettings: settings);
    }

    /// <summary>
    /// Executes show sessions / show session / show serial (ShowSerial policy minimum privilege 7).
    /// </summary>
    /// <param name="commandId">Show command id.</param>
    /// <param name="tokens">Parsed tokens.</param>
    /// <param name="operatorSessionId">Calling operator session id.</param>
    /// <param name="terminalColumns">Terminal width.</param>
    /// <returns>Continue with formatted output or an error.</returns>
    private CliCommandResult ExecuteShow(
        CliCommandId commandId,
        string[] tokens,
        string operatorSessionId,
        int terminalColumns)
    {
        if (commandId == CliCommandId.ShowSessions)
        {
            if (tokens.Length != 2)
            {
                return new CliCommandResult(CliCommandAction.Continue, "% Usage: show sessions");
            }

            var snapshots = ConnectorSessionSnapshotAssembler.BuildAll(
                _sessionRegistry,
                _attachmentManager,
                _serialConfigCache,
                _pendingBaudRequests,
                _timeProvider.GetUtcNow());
            return new CliCommandResult(
                CliCommandAction.Continue,
                ConnectorSessionDisplayFormatter.FormatSessionList(snapshots, terminalColumns));
        }

        if (commandId == CliCommandId.ShowSessionsDetail)
        {
            if (tokens.Length != 3)
            {
                return new CliCommandResult(CliCommandAction.Continue, "% Usage: show sessions detail");
            }

            var snapshots = ConnectorSessionSnapshotAssembler.BuildAll(
                _sessionRegistry,
                _attachmentManager,
                _serialConfigCache,
                _pendingBaudRequests,
                _timeProvider.GetUtcNow());
            return new CliCommandResult(
                CliCommandAction.Continue,
                NocDisplayFormatter.FormatSessionsDetail(snapshots));
        }

        if (commandId == CliCommandId.ShowSession)
        {
            if (tokens.Length != 3)
            {
                return new CliCommandResult(CliCommandAction.Continue, "% Usage: show session <session-id>");
            }

            return new CliCommandResult(CliCommandAction.Continue, FormatSessionDetail(tokens[2]));
        }

        if (commandId == CliCommandId.ShowSerial)
        {
            if (tokens.Length != 2)
            {
                return new CliCommandResult(CliCommandAction.Continue, "% Usage: show serial");
            }

            return new CliCommandResult(CliCommandAction.Continue, FormatSerial(operatorSessionId));
        }

        return new CliCommandResult(
            CliCommandAction.Continue,
            "% Incomplete show command");
    }

    /// <summary>
    /// Formats <c>show serial</c> for the operator's current attachment.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <returns>Formatted serial text or not-attached message.</returns>
    private string FormatSerial(string operatorSessionId)
    {
        if (!_attachmentManager.TryGetByOperatorSessionId(operatorSessionId, out var attachment) ||
            attachment is null)
        {
            return ConnectorSessionDisplayFormatter.FormatSerial(null, hasAttachment: false);
        }

        if (!_sessionRegistry.TryGetBySessionId(attachment.SessionId, out var session) || session is null)
        {
            return ConnectorSessionDisplayFormatter.FormatSerial(null, hasAttachment: false);
        }

        var snapshot = ConnectorSessionSnapshotAssembler.Build(
            session,
            _attachmentManager,
            _serialConfigCache,
            _pendingBaudRequests,
            _timeProvider.GetUtcNow());
        return ConnectorSessionDisplayFormatter.FormatSerial(snapshot, hasAttachment: true);
    }

    /// <summary>
    /// Formats <c>show session &lt;id&gt;</c> detail for a connector session.
    /// </summary>
    /// <param name="sessionId">Connector session id.</param>
    /// <returns>Formatted detail or not-found message.</returns>
    private string FormatSessionDetail(string sessionId)
    {
        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            return $"% Session '{sessionId}' not found.";
        }

        var snapshot = ConnectorSessionSnapshotAssembler.Build(
            session,
            _attachmentManager,
            _serialConfigCache,
            _pendingBaudRequests,
            _timeProvider.GetUtcNow());
        return ConnectorSessionDisplayFormatter.FormatSessionDetail(snapshot);
    }
}
