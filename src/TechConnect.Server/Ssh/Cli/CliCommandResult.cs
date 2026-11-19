// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandResult.cs
//
// Purpose:
// Defines CLI command actions and the result payload returned by the dispatcher
// for execution by TechConnectCliSession (connect, enable, ping, copy, etc.).
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Copy;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Post-dispatch action that <see cref="TechConnectCliSession"/> should perform.
/// </summary>
/// <remarks>
/// Privilege gating happens before these actions are produced (via
/// <c>CliCommandPolicy</c> / <see cref="Authorization.OperatorAuthorizationService"/>).
/// Minimum privileges from policy (session privilege ≥ minimum): Help/Exit/ClearScreen/
/// ShowVersion/ShowPrivilege/ShowClock/ShowTerminal/ShowUsers/ShowSessions/
/// ShowSessionsDetail/ShowSession/ShowHistory/ShowDns/ShowIpInterfaces/ShowIpRoute/
/// Nslookup/Connect/Attach/DisconnectSelf/Enable/Disable/Ping/Traceroute/Copy = 1;
/// ShowSerial/Disconnect = 7; ShowHistorySession = 5; ShowHistoryUser = 11.
/// </remarks>
internal enum CliCommandAction
{
    /// <summary>Remain at the CLI prompt (optionally after printing <see cref="CliCommandResult.Output"/>).</summary>
    Continue,

    /// <summary>End the SSH CLI session.</summary>
    Exit,

    /// <summary>Attach, apply serial configuration, then enter terminal I/O.</summary>
    Connect,

    /// <summary>Resume serial terminal I/O on an existing attachment (after Ctrl+]).</summary>
    Attach,

    /// <summary>Release the operator's attachment without closing the connector.</summary>
    ReleaseAttachment,

    /// <summary>Administrator interactive disconnect of a connector session.</summary>
    AdminDisconnect,

    /// <summary>Raise active privilege after enable authentication (see <see cref="CliCommandResult.EnableTargetLevel"/>).</summary>
    Enable,

    /// <summary>Restore active privilege to privilege level 1.</summary>
    Disable,

    /// <summary>Run ICMP / session diagnostics with streamed output.</summary>
    Ping,

    /// <summary>Run ICMP Echo traceroute with streamed hop output.</summary>
    Traceroute,

    /// <summary>Clear the current SSH terminal display.</summary>
    ClearScreen,

    /// <summary>Perform an asynchronous DNS lookup.</summary>
    Nslookup,

    /// <summary>Stream a file to an attached serial console.</summary>
    Copy
}

/// <summary>
/// Immutable result of parsing/authorizing a CLI command line.
/// </summary>
/// <param name="Action">Action the session should execute.</param>
/// <param name="Output">Optional text to write before/while handling the action.</param>
/// <param name="ConnectSessionId">Connector session id for <see cref="CliCommandAction.Connect"/>.</param>
/// <param name="SerialSettings">Serial settings for connect (defaults 9600 8N1 when unspecified).</param>
/// <param name="DisconnectSessionId">Connector session id for <see cref="CliCommandAction.AdminDisconnect"/>.</param>
/// <param name="EnableTargetLevel">Target privilege for <see cref="CliCommandAction.Enable"/>.</param>
/// <param name="PingRequest">Parsed ping request for <see cref="CliCommandAction.Ping"/>.</param>
/// <param name="TracerouteRequest">Parsed traceroute request for <see cref="CliCommandAction.Traceroute"/>.</param>
/// <param name="NslookupHostname">Hostname for <see cref="CliCommandAction.Nslookup"/>.</param>
/// <param name="CopyRequest">Parsed copy request for <see cref="CliCommandAction.Copy"/>.</param>
internal sealed record CliCommandResult(
    CliCommandAction Action,
    string? Output = null,
    string? ConnectSessionId = null,
    ConnectSerialSettings? SerialSettings = null,
    string? DisconnectSessionId = null,
    int? EnableTargetLevel = null,
    PingRequest? PingRequest = null,
    TracerouteRequest? TracerouteRequest = null,
    string? NslookupHostname = null,
    CopyRequest? CopyRequest = null);
