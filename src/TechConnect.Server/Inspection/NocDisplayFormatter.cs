// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    NocDisplayFormatter.cs
//
// Purpose:
// Formats operator-facing NOC display text for version, privilege, clock,
// terminal, users, DNS, interfaces, routes, and connector sessions.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net.Sockets;
using System.Text;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Inspection;

/// <summary>
/// Formats operator-facing NOC inspection output for the interactive CLI.
/// </summary>
/// <remarks>
/// DNS configuration formatting explicitly states that TechConnect never uses
/// the OS DNS resolver. Route formatting prefixes unavailable reasons with <c>%</c>.
/// </remarks>
internal static class NocDisplayFormatter
{
    /// <summary>Formats application name, versions, runtime, protocol, start time, and uptime.</summary>
    /// <param name="info">Application snapshot.</param>
    /// <returns>Multi-line version block (no trailing CRLF on the last line).</returns>
    public static string FormatVersion(ApplicationInfo info)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Application : {info.ApplicationName}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Version     : {info.Version}");
        if (!string.IsNullOrWhiteSpace(info.InformationalVersion) &&
            !string.Equals(info.InformationalVersion, info.Version, StringComparison.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Informational: {info.InformationalVersion}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime     : .NET {info.RuntimeVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Protocol    : {info.ProtocolVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Started (UTC): {info.StartedAtUtc:yyyy-MM-dd HH:mm:ss}Z");
        sb.Append(CultureInfo.InvariantCulture, $"Uptime      : {FormatDuration(info.Uptime)}");
        return sb.ToString();
    }

    /// <summary>Formats login vs active privilege for the current operator.</summary>
    /// <param name="login">Authenticated operator identity.</param>
    /// <param name="active">Active privilege after enable/disable.</param>
    /// <returns>Multi-line privilege block.</returns>
    public static string FormatPrivilege(AuthenticatedOperator login, PrivilegeLevel active)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Username           : {login.Username}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Operator session   : {login.OperatorSessionId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Login privilege    : {login.Privilege.Value}");
        sb.Append(CultureInfo.InvariantCulture, $"Active privilege   : {active.Value}");
        if (login.Privilege.Value != active.Value)
        {
            sb.AppendLine();
            sb.Append("(Active privilege differs from login privilege after enable/disable.)");
        }

        return sb.ToString();
    }

    /// <summary>Formats local and UTC clock lines for the given zone.</summary>
    /// <param name="utcNow">Current UTC instant.</param>
    /// <param name="localZone">Local time zone for conversion.</param>
    /// <returns>Multi-line clock block.</returns>
    public static string FormatClock(DateTimeOffset utcNow, TimeZoneInfo localZone)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, localZone);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Local time : {local:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Time zone  : {localZone.Id} ({localZone.DisplayName})");
        sb.Append(CultureInfo.InvariantCulture, $"UTC time   : {utcNow.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z");
        return sb.ToString();
    }

    /// <summary>Formats the current operator CLI terminal/session details.</summary>
    /// <param name="record">Operator CLI session record.</param>
    /// <returns>Multi-line terminal block.</returns>
    public static string FormatTerminal(OperatorCliSessionRecord record)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Operator session : {record.OperatorSessionId}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Username         : {record.Username}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Login privilege  : {record.LoginPrivilege.Value}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Active privilege : {record.ActivePrivilege.Value}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"CLI state        : {(record.TerminalModeActive ? "serial-terminal" : "interactive-cli")}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Attachment       : {(string.IsNullOrWhiteSpace(record.AttachedConnectorSessionId) ? "none" : record.AttachedConnectorSessionId)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"TERM             : {record.TermType ?? "(none)"}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Columns          : {record.Columns}");
        sb.Append(CultureInfo.InvariantCulture, $"Rows             : {record.Rows}");
        return sb.ToString();
    }

    /// <summary>Formats a tabular list of connected SSH operators.</summary>
    /// <param name="users">Operator sessions.</param>
    /// <returns>Table text, or a no-operators message when empty.</returns>
    public static string FormatUsers(IReadOnlyList<OperatorCliSessionRecord> users)
    {
        if (users.Count == 0)
        {
            return "No connected SSH operators.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("USERNAME       PRIV  SESSION              REMOTE                 CONNECTED  ATTACHMENT");
        foreach (var u in users)
        {
            sb.Append(Pad(u.Username, 14)).Append(' ');
            sb.Append(Pad(u.ActivePrivilege.Value.ToString(CultureInfo.InvariantCulture), 5)).Append(' ');
            sb.Append(Pad(Truncate(u.OperatorSessionId, 20), 20)).Append(' ');
            sb.Append(Pad(Truncate(u.RemoteEndpoint ?? "-", 22), 22)).Append(' ');
            sb.Append(Pad(u.ConnectedAtUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "Z", 10))
                .Append(' ');
            sb.AppendLine(string.IsNullOrWhiteSpace(u.AttachedConnectorSessionId)
                ? "-"
                : u.AttachedConnectorSessionId);
        }

        sb.Append(
            CultureInfo.InvariantCulture,
            $"{users.Count} SSH operator{(users.Count == 1 ? "" : "s")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Formats an nslookup-style result including server, answers, and duration.</summary>
    /// <param name="result">Lookup outcome from TechConnect DNS.</param>
    /// <returns>Multi-line DNS lookup text.</returns>
    public static string FormatDnsLookup(DnsLookupResult result)
    {
        var sb = new StringBuilder();
        if (result.ServerUsed is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Server:  {result.ServerUsed}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Address: {result.ServerUsed}#53");
        }
        else
        {
            sb.AppendLine("Server:  (none configured)");
            sb.AppendLine("Address: (DNS disabled in TechConnect)");
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(result.QueryTypeLabel))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Query:   {result.QueryTypeLabel}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"Name:    {result.Hostname}");
        if (!result.Success)
        {
            sb.Append(CultureInfo.InvariantCulture, $"*** Lookup failed: {result.Error}");
            if (result.Duration > TimeSpan.Zero)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({result.Duration.TotalMilliseconds:0} ms)");
            }

            return sb.ToString();
        }

        if (result.IsReverseLookup)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"PTR:     {result.PtrHostname}");
        }
        else
        {
            foreach (var addr in result.Addresses)
            {
                var label = addr.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA";
                sb.AppendLine(CultureInfo.InvariantCulture, $"Address: {addr}  ({label})");
            }
        }

        sb.Append(CultureInfo.InvariantCulture, $"Lookup time: {result.Duration.TotalMilliseconds:0} ms");
        return sb.ToString();
    }

    /// <summary>
    /// Formats TechConnect DNS configuration and notes that the OS resolver is never used.
    /// </summary>
    /// <param name="config">TechConnect DNS snapshot.</param>
    /// <returns>Multi-line DNS configuration text.</returns>
    public static string FormatDnsConfiguration(DnsConfigurationSnapshot config)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"DNS enabled : {(config.Enabled ? "yes" : "no")}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"Query timeout: {config.QueryTimeoutSeconds} second{(config.QueryTimeoutSeconds == 1 ? "" : "s")}");
        sb.AppendLine("DNS servers (TechConnect configuration order):");
        if (config.Servers.Count == 0)
        {
            sb.AppendLine("  (none — hostname resolution and reverse DNS are disabled)");
        }
        else
        {
            for (var i = 0; i < config.Servers.Count; i++)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {i + 1}. {config.Servers[i]}");
            }
        }

        sb.Append("Note: TechConnect never uses the operating-system DNS resolver.");
        return sb.ToString();
    }

    /// <summary>Formats network interface inventory for NOC display.</summary>
    /// <param name="interfaces">Interface snapshots.</param>
    /// <returns>Multi-line interface text, or a none-reported message.</returns>
    public static string FormatInterfaces(IReadOnlyList<NetworkInterfaceInfo> interfaces)
    {
        if (interfaces.Count == 0)
        {
            return "No network interfaces reported.";
        }

        var sb = new StringBuilder();
        foreach (var nic in interfaces)
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"Interface: {nic.Name}");
            if (!string.IsNullOrWhiteSpace(nic.Description))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  Description : {nic.Description}");
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  Status      : {nic.Status}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  Type        : {nic.Type}");
            if (nic.Ipv4Addresses.Count == 0 && nic.Ipv6Addresses.Count == 0)
            {
                sb.AppendLine("  Addresses   : (none)");
                continue;
            }

            foreach (var a in nic.Ipv4Addresses)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  IPv4        : {a.Address}/{a.PrefixLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            }

            foreach (var a in nic.Ipv6Addresses)
            {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  IPv6        : {a.Address}/{a.PrefixLength?.ToString(CultureInfo.InvariantCulture) ?? "?"}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats IPv4/IPv6 routing tables, or an unavailable reason prefixed with <c>%</c>.
    /// </summary>
    /// <param name="table">Routing snapshot.</param>
    /// <returns>Route tables or unavailable message.</returns>
    public static string FormatRoutes(RoutingTableSnapshot table)
    {
        if (!string.IsNullOrWhiteSpace(table.UnavailableReason))
        {
            return "% " + table.UnavailableReason;
        }

        if (table.Routes.Count == 0)
        {
            return "No routes reported.";
        }

        var ipv4 = table.Routes.Where(r => r.Family == AddressFamily.InterNetwork).ToList();
        var ipv6 = table.Routes.Where(r => r.Family == AddressFamily.InterNetworkV6).ToList();
        var sb = new StringBuilder();

        AppendRouteSection(sb, "IPv4 Routing Table", ipv4);
        if (ipv4.Count > 0 && ipv6.Count > 0)
        {
            sb.AppendLine();
        }

        AppendRouteSection(sb, "IPv6 Routing Table", ipv6);

        sb.AppendLine();
        sb.Append(
            CultureInfo.InvariantCulture,
            $"{table.Routes.Count} route{(table.Routes.Count == 1 ? "" : "s")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Appends one address-family route section with fixed-width columns.</summary>
    /// <param name="sb">Output builder.</param>
    /// <param name="title">Section title.</param>
    /// <param name="routes">Routes for this family (skipped when empty).</param>
    private static void AppendRouteSection(StringBuilder sb, string title, IReadOnlyList<RouteEntry> routes)
    {
        if (routes.Count == 0)
        {
            return;
        }

        sb.AppendLine(title);
        sb.AppendLine();
        sb.AppendLine("Destination                        Next Hop                           Interface       Metric");
        foreach (var r in routes)
        {
            var dest = r.Destination + "/" + r.PrefixLength;
            var next = r.IsOnLink || r.NextHop is null
                ? "On-link"
                : r.NextHop.ToString();
            var iface = r.InterfaceName
                        ?? (r.InterfaceIndex is int idx ? "if" + idx.ToString(CultureInfo.InvariantCulture) : "-");

            sb.Append(Pad(Truncate(dest, 34), 34)).Append(' ');
            sb.Append(Pad(Truncate(next, 34), 34)).Append(' ');
            sb.Append(Pad(Truncate(iface, 15), 15)).Append(' ');
            sb.AppendLine(r.Metric?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }
    }

    /// <summary>Formats detailed connector session snapshots for NOC display.</summary>
    /// <param name="sessions">Active connector sessions.</param>
    /// <returns>Multi-line session detail, or a none-active message.</returns>
    public static string FormatSessionsDetail(IReadOnlyList<ConnectorSessionSnapshot> sessions)
    {
        if (sessions.Count == 0)
        {
            return "No active connector sessions.";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < sessions.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine();
                sb.AppendLine(new string('-', 60));
                sb.AppendLine();
            }

            var s = sessions[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"Session ID     : {s.SessionId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Hostname       : {s.Hostname ?? "Not reported"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"App version    : {s.ApplicationVersion ?? "Not reported"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Remote         : {s.RemoteEndpoint ?? "-"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"TLS            : {s.TlsProtocol ?? "-"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Connected      : {s.ConnectedAtUtc:yyyy-MM-dd HH:mm:ss}Z");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Duration       : {FormatDuration(s.ConnectionDuration)}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Last activity  : {(s.LastActivityUtc is { } a ? a.ToString("yyyy-MM-dd HH:mm:ss") + "Z" : "-")}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"State          : {s.ConnectionState}");
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"Attachment     : {(s.OperatorAttached ? (s.OperatorUsername ?? "attached") : "none")}");
            sb.Append(
                CultureInfo.InvariantCulture,
                $"Terminal       : {(s.TerminalActive ? "active" : "inactive")}");
            if (s.SerialConfigurationReported)
            {
                sb.AppendLine();
                sb.Append(
                    CultureInfo.InvariantCulture,
                    $"Serial         : {s.PortName ?? "?"} {s.BaudRate} {s.DataBits}{s.Parity}{s.StopBits}");
            }
        }

        sb.AppendLine();
        sb.Append(
            CultureInfo.InvariantCulture,
            $"{sessions.Count} active session{(sessions.Count == 1 ? "" : "s")}");
        return sb.ToString();
    }

    /// <summary>Formats a duration as days/hours/minutes or hours/minutes/seconds or minutes/seconds.</summary>
    /// <param name="value">Duration to format.</param>
    /// <returns>Compact duration string.</returns>
    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)value.TotalDays}d {value.Hours:D2}h {value.Minutes:D2}m");
        }

        if (value.TotalHours >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{value.Hours}h {value.Minutes:D2}m {value.Seconds:D2}s");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{value.Minutes}m {value.Seconds:D2}s");
    }

    /// <summary>Right-pads <paramref name="value"/> to <paramref name="width"/> when shorter.</summary>
    /// <param name="value">Text to pad.</param>
    /// <param name="width">Target width.</param>
    /// <returns>Padded or original text.</returns>
    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value.PadRight(width);

    /// <summary>Truncates <paramref name="value"/> with an ellipsis when longer than <paramref name="width"/>.</summary>
    /// <param name="value">Text to truncate.</param>
    /// <param name="width">Maximum width.</param>
    /// <returns>Original or truncated text.</returns>
    private static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(1, width - 1)] + "…";
}
