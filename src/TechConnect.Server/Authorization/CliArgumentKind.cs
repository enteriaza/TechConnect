// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliArgumentKind.cs
//
// Purpose:
// Enumerates CLI argument kinds recognized by the command catalog and parser.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// Kind of positional/trailing argument expected by a CLI catalog node.
/// </summary>
internal enum CliArgumentKind
{
    /// <summary>Connector/session identifier token.</summary>
    SessionId = 1,

    /// <summary>Serial baud-rate integer.</summary>
    BaudRate = 2,

    /// <summary>Operator or account username token.</summary>
    Username = 3,

    /// <summary>Privilege level token (1–15 or legacy role name).</summary>
    PrivilegeLevel = 4,

    /// <summary>Ping target: destination (IPv4/IPv6/hostname) or <c>session</c> (trailing options allowed).</summary>
    PingTarget = 5,

    /// <summary>Traceroute destination (IPv4/IPv6/hostname; trailing options allowed).</summary>
    TracerouteTarget = 6,

    /// <summary>DNS hostname or IP address for <c>nslookup</c>.</summary>
    Hostname = 7,

    /// <summary>Copy source scheme/URL; trailing <c>serial:&lt;session-id&gt;</c> required.</summary>
    CopySource = 8,

    /// <summary>Copy destination <c>serial:&lt;session-id&gt;</c>.</summary>
    CopyDestination = 9
}
