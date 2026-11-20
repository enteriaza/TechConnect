// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandId.cs
//
// Purpose:
// Stable identifiers for catalogued CLI commands used by parse and
// authorization paths (distinct from raw input tokens).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// Normalized CLI command identity used for authorization. Distinct from raw input tokens.
/// </summary>
internal enum CliCommandId
{
    /// <summary>Display CLI help.</summary>
    Help = 1,

    /// <summary>End the SSH/CLI session.</summary>
    Exit = 2,

    /// <summary>List connector sessions (summary).</summary>
    ShowSessions = 3,

    /// <summary>Show a single session by id.</summary>
    ShowSession = 4,

    /// <summary>Show serial/port details for a session.</summary>
    ShowSerial = 5,

    /// <summary>Connect/attach to a session for interactive terminal use.</summary>
    Connect = 6,

    /// <summary>Resume interactive terminal on an existing attachment (after Ctrl+]).</summary>
    Attach = 7,

    /// <summary>Show audit history (default scope).</summary>
    ShowHistory = 10,

    /// <summary>Show audit history filtered by session.</summary>
    ShowHistorySession = 11,

    /// <summary>Show audit history filtered by user.</summary>
    ShowHistoryUser = 12,

    /// <summary>Privilege 7+: administratively disconnect a connector session by id.</summary>
    Disconnect = 13,

    /// <summary>Raise active privilege (optional target level, default 15).</summary>
    Enable = 14,

    /// <summary>Restore active privilege to privilege level 1.</summary>
    Disable = 15,

    /// <summary>Privilege 1+: release the invoking operator's current attachment (no session id).</summary>
    DisconnectSelf = 16,

    /// <summary>Privilege 1+: ICMP / ICMPv6 / connector session diagnostics.</summary>
    Ping = 17,

    /// <summary>Privilege 1+: ICMP Echo traceroute (IPv4 / IPv6).</summary>
    Traceroute = 18,

    /// <summary>Clear the terminal screen.</summary>
    ClearScreen = 19,

    /// <summary>Show TechConnect server version information.</summary>
    ShowVersion = 20,

    /// <summary>Show the operator's current privilege level.</summary>
    ShowPrivilege = 21,

    /// <summary>Show the current clock.</summary>
    ShowClock = 22,

    /// <summary>Show terminal settings.</summary>
    ShowTerminal = 23,

    /// <summary>Show connected operators/users.</summary>
    ShowUsers = 24,

    /// <summary>List connector sessions with detail.</summary>
    ShowSessionsDetail = 25,

    /// <summary>DNS lookup (<c>nslookup</c>).</summary>
    Nslookup = 26,

    /// <summary>Show DNS configuration/status.</summary>
    ShowDns = 27,

    /// <summary>Show IP interfaces.</summary>
    ShowIpInterfaces = 28,

    /// <summary>Show IP routes.</summary>
    ShowIpRoute = 29,

    /// <summary>Privilege 1+: stream a remote/modem file to an attached serial console.</summary>
    Copy = 30,

    /// <summary>Privilege 15: enter configuration mode (<c>configure [terminal]</c>).</summary>
    ConfigureTerminal = 31,

    /// <summary>Privilege 15: save the candidate configuration (<c>write [memory]</c>).</summary>
    Write = 32,

    /// <summary>Privilege 15: show the running configuration (secrets masked).</summary>
    ShowRunningConfig = 33,

    /// <summary>Privilege 15: show appsettings.json as startup configuration (secrets masked).</summary>
    ShowStartupConfig = 34,

    /// <summary>Privilege 15: any line executed inside configuration mode.</summary>
    ConfigurationCommand = 35
}
