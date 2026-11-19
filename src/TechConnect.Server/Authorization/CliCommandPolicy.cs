// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandPolicy.cs
//
// Purpose:
// Maps authenticated privilege levels to allowed CLI commands and discovery.
//
// -----------------------------------------------------------------------------
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authorization;

/// <summary>
/// Single source of truth for command privilege requirements (Cisco-style levels 1–15).
/// Default-deny: commands absent from the policy are denied.
/// Help and completion text come from <see cref="CliCommandCatalog"/>.
/// </summary>
internal static class CliCommandPolicy
{
    /// <summary>
    /// Minimum privilege per command (cumulative: session privilege ≥ minimum).
    /// </summary>
    private static readonly IReadOnlyDictionary<CliCommandId, PrivilegeLevel> MinimumPrivilege =
        new Dictionary<CliCommandId, PrivilegeLevel>
        {
            [CliCommandId.Help] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Exit] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ClearScreen] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowVersion] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowPrivilege] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowClock] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowTerminal] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowUsers] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowSessions] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowSessionsDetail] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowSession] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowSerial] = PrivilegeLevel.FromInt(7),
            [CliCommandId.ShowHistory] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowHistorySession] = PrivilegeLevel.FromInt(5),
            [CliCommandId.ShowHistoryUser] = PrivilegeLevel.FromInt(11),
            [CliCommandId.ShowDns] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowIpInterfaces] = PrivilegeLevel.FromInt(1),
            [CliCommandId.ShowIpRoute] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Nslookup] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Connect] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Attach] = PrivilegeLevel.FromInt(1),
            [CliCommandId.DisconnectSelf] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Disconnect] = PrivilegeLevel.FromInt(7),
            [CliCommandId.Enable] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Disable] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Ping] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Traceroute] = PrivilegeLevel.FromInt(1),
            [CliCommandId.Copy] = PrivilegeLevel.FromInt(1)
        };

    /// <summary>
    /// Looks up the minimum privilege required to execute <paramref name="command"/>.
    /// </summary>
    /// <param name="command">Command id to query.</param>
    /// <param name="minimum">Minimum privilege when found; otherwise <c>default</c>.</param>
    /// <returns><see langword="true"/> when the command is present in the policy map.</returns>
    public static bool TryGetMinimumPrivilege(CliCommandId command, out PrivilegeLevel minimum)
    {
        if (MinimumPrivilege.TryGetValue(command, out minimum))
        {
            return true;
        }

        minimum = default;
        return false;
    }

    /// <summary>
    /// Returns whether <paramref name="command"/> has a defined minimum privilege entry.
    /// </summary>
    public static bool IsDefined(CliCommandId command) => MinimumPrivilege.ContainsKey(command);

    /// <summary>
    /// Returns whether <paramref name="privilege"/> meets the minimum for <paramref name="command"/>.
    /// </summary>
    /// <remarks>Undefined commands and invalid privilege values are denied (default-deny).</remarks>
    public static bool IsAllowed(PrivilegeLevel privilege, CliCommandId command)
    {
        if (!TryGetMinimumPrivilege(command, out var minimum))
        {
            return false;
        }

        if (!PrivilegeLevel.IsValid(privilege.Value))
        {
            return false;
        }

        return privilege >= minimum;
    }

    /// <summary>
    /// Builds multi-line help text listing commands visible at <paramref name="privilege"/>.
    /// </summary>
    /// <param name="privilege">Operator privilege used for visibility filtering.</param>
    /// <param name="terminalColumns">Approximate terminal width for column layout (minimum effective 40).</param>
    /// <returns>Help text including command synopses and descriptions.</returns>
    public static string BuildHelpText(PrivilegeLevel privilege, int terminalColumns = 80)
    {
        var width = terminalColumns < 40 ? 80 : terminalColumns;
        var tokenCol = Math.Max(12, Math.Min(36, width / 3));
        var lines = new List<string> { "Available commands:" };
        foreach (var entry in EnumerateHelpEntries(privilege)
                     .OrderBy(e => e.Synopsis, CliDiscoveryOrdering.Comparer))
        {
            lines.Add("  " + PadToken(entry.Synopsis, tokenCol) + "  " + entry.Description);
            if (entry.CommandId == CliCommandId.Connect)
            {
                lines.Add("  " + new string(' ', tokenCol) + "  Default serial: 9600 8N1; optional baud/databits/parity/stopbits");
            }

            if (entry.CommandId == CliCommandId.Attach)
            {
                lines.Add("  " + new string(' ', tokenCol) + "  Available after Ctrl+] (attachment retained)");
            }

            if (entry.CommandId == CliCommandId.DisconnectSelf)
            {
                lines.Add("  " + new string(' ', tokenCol) + "  Releases your attachment only; does not terminate the connector");
            }

            if (entry.CommandId == CliCommandId.Disconnect)
            {
                lines.Add("  " + new string(' ', tokenCol) + "  Terminates the connector session (any attachment)");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Pads a synopsis token to <paramref name="width"/> for columnar help layout.
    /// </summary>
    private static string PadToken(string token, int width) =>
        token.Length >= width ? token : token.PadRight(width);

    /// <summary>
    /// Enumerates help entries visible at the given privilege from the catalog.
    /// </summary>
    private static IEnumerable<(CliCommandId CommandId, string Synopsis, string Description)> EnumerateHelpEntries(
        PrivilegeLevel privilege)
    {
        foreach (var root in CliCommandCatalog.Roots)
        {
            foreach (var entry in FlattenExecutable(root, prefix: null))
            {
                if (!IsAllowed(privilege, entry.CommandId))
                {
                    continue;
                }

                yield return entry;
            }
        }
    }

    /// <summary>
    /// Flattens executable catalog nodes into synopsis/description help entries.
    /// </summary>
    private static IEnumerable<(CliCommandId CommandId, string Synopsis, string Description)> FlattenExecutable(
        CliCommandNode node,
        string? prefix)
    {
        var path = prefix is null ? node.Token : prefix + " " + node.Token;
        if (node.CommandId is { } id)
        {
            if (id == CliCommandId.Help)
            {
                yield return (id, "help, ?", node.Description);
            }
            else if (node.CommandIdWithArgument is { } withArg && node.Argument is not null)
            {
                yield return (id, path, DescribeBareForm(id, node.Description));
                yield return (
                    withArg,
                    path + " " + node.Argument.DisplayName,
                    DescribeArgumentForm(withArg, node.Argument.Description));
            }
            else if (id == CliCommandId.Connect)
            {
                yield return (
                    id,
                    "connect <session-id> [baud ...] [databits ...] [parity ...] [stopbits ...]",
                    node.Description);
            }
            else if (id == CliCommandId.Ping)
            {
                yield return (
                    id,
                    "ping <destination> | ping session <session-id>",
                    node.Description);
            }
            else if (id == CliCommandId.Traceroute)
            {
                yield return (
                    id,
                    "traceroute <destination>",
                    node.Description);
            }
            else if (id == CliCommandId.Copy)
            {
                yield return (
                    id,
                    "copy {http:|https:|tftp:|scp:|xmodem:|ymodem:} serial:<session-id>",
                    node.Description);
            }
            else
            {
                var synopsis = node.Argument is null
                    ? path
                    : path + " " + node.Argument.DisplayName;
                yield return (id, synopsis, node.Description);
            }
        }

        foreach (var child in node.Children)
        {
            foreach (var entry in FlattenExecutable(child, path))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Description override for the bare (no-argument) form of a command when needed.
    /// </summary>
    private static string DescribeBareForm(CliCommandId id, string fallback) =>
        id == CliCommandId.DisconnectSelf
            ? "Release your current connector attachment"
            : fallback;

    /// <summary>
    /// Description override for the argument form of a command when needed.
    /// </summary>
    private static string DescribeArgumentForm(CliCommandId id, string fallback) =>
        id == CliCommandId.Disconnect
            ? "Administratively disconnect a connector session"
            : fallback;

    /// <summary>
    /// Maps a command id to a stable audit/log name string.
    /// </summary>
    /// <param name="command">Command to name.</param>
    /// <returns>Lowercase token-style name used in audit details.</returns>
    public static string ToAuditName(CliCommandId command) => command switch
    {
        CliCommandId.Help => "help",
        CliCommandId.Exit => "exit",
        CliCommandId.ClearScreen => "clear screen",
        CliCommandId.ShowVersion => "show version",
        CliCommandId.ShowPrivilege => "show privilege",
        CliCommandId.ShowClock => "show clock",
        CliCommandId.ShowTerminal => "show terminal",
        CliCommandId.ShowUsers => "show users",
        CliCommandId.ShowSessions => "show sessions",
        CliCommandId.ShowSessionsDetail => "show sessions detail",
        CliCommandId.ShowSession => "show session",
        CliCommandId.ShowSerial => "show serial",
        CliCommandId.ShowHistory => "show history",
        CliCommandId.ShowHistorySession => "show history session",
        CliCommandId.ShowHistoryUser => "show history user",
        CliCommandId.ShowDns => "show dns",
        CliCommandId.ShowIpInterfaces => "show ip interfaces",
        CliCommandId.ShowIpRoute => "show ip route",
        CliCommandId.Nslookup => "nslookup",
        CliCommandId.Connect => "connect",
        CliCommandId.Attach => "attach",
        CliCommandId.DisconnectSelf => "disconnect",
        CliCommandId.Disconnect => "disconnect session",
        CliCommandId.Enable => "enable",
        CliCommandId.Disable => "disable",
        CliCommandId.Ping => "ping",
        CliCommandId.Traceroute => "traceroute",
        CliCommandId.Copy => "copy",
        _ => command.ToString()
    };
}
