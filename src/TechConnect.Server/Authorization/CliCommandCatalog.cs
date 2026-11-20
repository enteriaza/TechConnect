// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandCatalog.cs
//
// Purpose:
// Builds and owns the static CLI command tree used for parse and authorization.
//
// -----------------------------------------------------------------------------
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authorization;

/// <summary>
/// Single source of truth for CLI command tokens, descriptions, nesting, and arguments.
/// Privilege for execution remains in <see cref="CliCommandPolicy"/>; discovery visibility
/// is derived from that policy via <see cref="CliCommandId"/>.
/// </summary>
internal static class CliCommandCatalog
{
    /// <summary>Shared session-id argument used by show/disconnect-style commands.</summary>
    private static readonly CliArgumentSpec SessionIdArgument = new(
        "<session-id>",
        "Connector session identifier",
        CliArgumentKind.SessionId);

    /// <summary>Connect session-id argument that retains trailing serial option tokens.</summary>
    private static readonly CliArgumentSpec ConnectSessionArgument = new(
        "<session-id>",
        "Connector session identifier",
        CliArgumentKind.SessionId,
        allowTrailingTokens: true);

    /// <summary>Username argument for history-by-user.</summary>
    private static readonly CliArgumentSpec UsernameArgument = new(
        "<username>",
        "Operator username",
        CliArgumentKind.Username);

    /// <summary>Optional privilege-level argument for enable.</summary>
    private static readonly CliArgumentSpec PrivilegeLevelArgument = new(
        "<level>",
        "Privilege level 1-15 (default 15)",
        CliArgumentKind.PrivilegeLevel,
        optional: true);

    /// <summary>Ping target argument retaining trailing options.</summary>
    private static readonly CliArgumentSpec PingTargetArgument = new(
        "<target>",
        "IPv4/IPv6 address, hostname, or session <session-id>",
        CliArgumentKind.PingTarget,
        allowTrailingTokens: true);

    /// <summary>Traceroute destination argument retaining trailing options.</summary>
    private static readonly CliArgumentSpec TracerouteTargetArgument = new(
        "<destination>",
        "IPv4/IPv6 address or hostname",
        CliArgumentKind.TracerouteTarget,
        allowTrailingTokens: true);

    /// <summary>Hostname/IP argument for nslookup.</summary>
    private static readonly CliArgumentSpec HostnameArgument = new(
        "<hostname>",
        "DNS hostname or IP address (PTR)",
        CliArgumentKind.Hostname);

    /// <summary>Copy source scheme/URL argument retaining trailing destination tokens.</summary>
    private static readonly CliArgumentSpec CopySourceArgument = new(
        "<source>",
        "http:/https:/tftp:/scp:/xmodem:/ymodem: or a full URL",
        CliArgumentKind.CopySource,
        allowTrailingTokens: true);

    /// <summary>
    /// Discovery-only argument shown after the copy source token (destination is not connect trailing keywords).
    /// </summary>
    internal static readonly CliArgumentSpec CopyDestinationArgument = new(
        "serial:<session-id>",
        "Destination connector session matching your attachment",
        CliArgumentKind.CopyDestination);

    /// <summary>Optional configuration source for <c>configure</c>.</summary>
    internal static readonly CliArgumentSpec ConfigureModeArgument = new(
        "terminal",
        "Configure from the terminal (default)",
        CliArgumentKind.ConfigureMode,
        optional: true);

    /// <summary>Optional write target for <c>write</c>.</summary>
    internal static readonly CliArgumentSpec WriteTargetArgument = new(
        "memory",
        "Write to startup-config (appsettings.json)",
        CliArgumentKind.WriteTarget,
        optional: true);

    /// <summary>
    /// Root-level catalog nodes for the TechConnect CLI.
    /// </summary>
    public static IReadOnlyList<CliCommandNode> Roots { get; } = BuildRoots();

    /// <summary>
    /// Constructs the immutable root command tree.
    /// </summary>
    private static IReadOnlyList<CliCommandNode> BuildRoots() =>
    [
        new CliCommandNode(
            "help",
            "Show this help",
            CliCommandId.Help,
            aliases: ["?"]),
        new CliCommandNode(
            "exit",
            "Disconnect from TechConnect Server",
            CliCommandId.Exit,
            aliases: ["quit"]),
        new CliCommandNode(
            "clear",
            "Clear the local terminal display",
            children:
            [
                new CliCommandNode(
                    "screen",
                    "Clear the current SSH terminal",
                    CliCommandId.ClearScreen)
            ]),
        new CliCommandNode(
            "show",
            "Display operational information",
            children:
            [
                new CliCommandNode(
                    "version",
                    "Show TechConnect application and runtime version",
                    CliCommandId.ShowVersion),
                new CliCommandNode(
                    "privilege",
                    "Show authenticated identity and active privilege",
                    CliCommandId.ShowPrivilege),
                new CliCommandNode(
                    "clock",
                    "Show server date, time, and time zone",
                    CliCommandId.ShowClock),
                new CliCommandNode(
                    "terminal",
                    "Show this SSH CLI session",
                    CliCommandId.ShowTerminal),
                new CliCommandNode(
                    "users",
                    "List connected SSH operators",
                    CliCommandId.ShowUsers),
                new CliCommandNode(
                    "sessions",
                    "List connector sessions (identity, version, endpoint, attachment)",
                    CliCommandId.ShowSessions,
                    children:
                    [
                        new CliCommandNode(
                            "detail",
                            "Expanded connector session details",
                            CliCommandId.ShowSessionsDetail)
                    ]),
                new CliCommandNode(
                    "session",
                    "Show connector, serial, attachment, and pending baud detail",
                    CliCommandId.ShowSession,
                    argument: SessionIdArgument),
                new CliCommandNode(
                    "serial",
                    "Show cached serial settings for the attached connector",
                    CliCommandId.ShowSerial),
                new CliCommandNode(
                    "history",
                    "Show your recent operational audit events",
                    CliCommandId.ShowHistory,
                    children:
                    [
                        new CliCommandNode(
                            "session",
                            "Show audit events for a connector session",
                            CliCommandId.ShowHistorySession,
                            argument: SessionIdArgument),
                        new CliCommandNode(
                            "user",
                            "Show audit events for an operator username",
                            CliCommandId.ShowHistoryUser,
                            argument: UsernameArgument)
                    ]),
                new CliCommandNode(
                    "dns",
                    "Show DNS resolvers configured on this server",
                    CliCommandId.ShowDns),
                new CliCommandNode(
                    "running-config",
                    "Show the configuration in use (secrets masked)",
                    CliCommandId.ShowRunningConfig),
                new CliCommandNode(
                    "startup-config",
                    "Show appsettings.json configuration (secrets masked)",
                    CliCommandId.ShowStartupConfig),
                new CliCommandNode(
                    "ip",
                    "Show this server's IP configuration",
                    children:
                    [
                        new CliCommandNode(
                            "interfaces",
                            "Show local network interfaces and addresses",
                            CliCommandId.ShowIpInterfaces),
                        new CliCommandNode(
                            "route",
                            "Show the server routing table",
                            CliCommandId.ShowIpRoute)
                    ])
            ]),
        new CliCommandNode(
            "connect",
            "Attach, apply serial settings (default 9600 8N1), and enter terminal",
            CliCommandId.Connect,
            argument: ConnectSessionArgument),
        new CliCommandNode(
            "ping",
            "ICMP, ICMPv6, or connector session diagnostics",
            CliCommandId.Ping,
            argument: PingTargetArgument),
        new CliCommandNode(
            "traceroute",
            "Trace the network path to a destination (ICMP Echo)",
            CliCommandId.Traceroute,
            argument: TracerouteTargetArgument),
        new CliCommandNode(
            "nslookup",
            "Resolve a hostname or reverse-lookup an address using TechConnect DNS",
            CliCommandId.Nslookup,
            argument: HostnameArgument),
        new CliCommandNode(
            "copy",
            "Copy a file to an attached serial console (binary-safe stream)",
            CliCommandId.Copy,
            argument: CopySourceArgument),
        new CliCommandNode(
            "attach",
            "Resume terminal mode on your existing attachment (after Ctrl+])",
            CliCommandId.Attach),
        new CliCommandNode(
            "enable",
            "Raise privilege level",
            CliCommandId.Enable,
            argument: PrivilegeLevelArgument),
        new CliCommandNode(
            "disable",
            "Return to privilege level 1",
            CliCommandId.Disable),
        new CliCommandNode(
            "configure",
            "Enter configuration mode",
            CliCommandId.ConfigureTerminal,
            argument: ConfigureModeArgument,
            aliases: ["conf", "config"]),
        new CliCommandNode(
            "write",
            "Save configuration changes (from configuration mode)",
            CliCommandId.Write,
            argument: WriteTargetArgument,
            aliases: ["wr"]),
        new CliCommandNode(
            "disconnect",
            "Release your attachment, or administratively disconnect a connector session",
            CliCommandId.DisconnectSelf,
            argument: new CliArgumentSpec(
                "<session-id>",
                "Connector session to administratively disconnect",
                CliArgumentKind.SessionId,
                optional: true),
            commandIdWithArgument: CliCommandId.Disconnect)
    ];

    /// <summary>
    /// Returns whether <paramref name="node"/> (or any descendant executable) is allowed at <paramref name="privilege"/>.
    /// </summary>
    public static bool IsVisible(CliCommandNode node, PrivilegeLevel privilege)
    {
        if (node.CommandId is { } id)
        {
            if (CliCommandPolicy.IsAllowed(privilege, id))
            {
                return true;
            }

            return node.CommandIdWithArgument is { } withArg &&
                   CliCommandPolicy.IsAllowed(privilege, withArg);
        }

        foreach (var child in node.Children)
        {
            if (IsVisible(child, privilege))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns children of <paramref name="parent"/> that are visible at <paramref name="privilege"/>.
    /// </summary>
    public static IEnumerable<CliCommandNode> GetVisibleChildren(
        CliCommandNode parent,
        PrivilegeLevel privilege) =>
        parent.Children.Where(c => IsVisible(c, privilege));

    /// <summary>
    /// Returns root catalog nodes visible at <paramref name="privilege"/>.
    /// </summary>
    public static IEnumerable<CliCommandNode> GetVisibleRoots(PrivilegeLevel privilege) =>
        Roots.Where(r => IsVisible(r, privilege));
}

