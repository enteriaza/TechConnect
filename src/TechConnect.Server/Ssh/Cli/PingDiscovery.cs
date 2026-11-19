// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingDiscovery.cs
//
// Purpose:
// Provides Tab/? discovery for ping destination/session options gated by
// CliCommandPolicy Ping privilege.
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Catalog-adjacent discovery for <c>ping</c> (destination / session + options).
/// </summary>
internal sealed class PingDiscovery
{
    /// <summary>Optional registry for <c>ping session &lt;id&gt;</c> suggestions.</summary>
    private readonly ConnectorSessionRegistry? _sessionRegistry;

    /// <summary>Local IPv4/global IPv6 addresses for <c>source</c> completion/help.</summary>
    private readonly IIPv6NetworkAvailability _ipv6;

    /// <summary>
    /// Initializes ping discovery with optional session registry and address availability.
    /// </summary>
    /// <param name="sessionRegistry">Session registry for session-id suggestions; may be null.</param>
    /// <param name="ipv6">Local address provider for source suggestions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ipv6"/> is <see langword="null"/>.</exception>
    public PingDiscovery(
        ConnectorSessionRegistry? sessionRegistry,
        IIPv6NetworkAvailability ipv6)
    {
        _sessionRegistry = sessionRegistry;
        _ipv6 = ipv6 ?? throw new ArgumentNullException(nameof(ipv6));
    }

    /// <summary>
    /// Returns whether <paramref name="before"/> starts with the <c>ping</c> verb (case-insensitive).
    /// </summary>
    /// <param name="before">Completed tokens before the partial under the cursor.</param>
    /// <returns><see langword="true"/> when discovery should be delegated to this helper.</returns>
    public static bool IsPingContext(string before)
    {
        if (string.IsNullOrWhiteSpace(before))
        {
            return false;
        }

        var tokens = before.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens[0].Equals("ping", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tab-completes ping destination/session options when <see cref="CliCommandId.Ping"/> is allowed.
    /// </summary>
    /// <param name="before">Completed tokens before the partial.</param>
    /// <param name="partial">Current partial token.</param>
    /// <param name="tokenStart">Replace-range start.</param>
    /// <param name="cursor">Replace-range end.</param>
    /// <param name="identity">Operator for policy gating.</param>
    /// <param name="terminalColumns">Width for ambiguous listings.</param>
    /// <returns>Completion result, or <see cref="CliDiscoveryResult.None"/> when denied or no matches.</returns>
    public CliDiscoveryResult Complete(
        string before,
        string partial,
        int tokenStart,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Ping))
        {
            return CliDiscoveryResult.None;
        }

        var tokens = Split(before);
        var position = Classify(tokens);
        return position switch
        {
            PingPosition.Target => CompleteTargets(partial, tokenStart, cursor, terminalColumns),
            PingPosition.SessionId => CompleteSessions(partial, tokenStart, cursor, terminalColumns),
            PingPosition.DestinationOptions => CompleteKeywords(
                RemainingDestinationOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SessionOptions => CompleteKeywords(
                RemainingSessionOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SourceValue => CompleteSource(
                tokens,
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SweepOptions => CompleteKeywords(
                RemainingSweepOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.Value => CliDiscoveryResult.None,
            _ => CliDiscoveryResult.None
        };
    }

    /// <summary>
    /// Context <c>?</c> help for the current ping position when Ping policy allows.
    /// </summary>
    /// <param name="before">Completed tokens before the partial.</param>
    /// <param name="partial">Current partial token (filters listings).</param>
    /// <param name="tokenStart">Replace-range start.</param>
    /// <param name="cursor">Replace-range end.</param>
    /// <param name="identity">Operator for policy gating.</param>
    /// <param name="terminalColumns">Width for help listings.</param>
    /// <returns>Context-help result, or None when denied or empty.</returns>
    public CliDiscoveryResult Help(
        string before,
        string partial,
        int tokenStart,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Ping))
        {
            return CliDiscoveryResult.None;
        }

        var tokens = Split(before);
        var position = Classify(tokens);
        return position switch
        {
            PingPosition.Target => HelpListing(
                BuildTargetHelpEntries(),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SessionId => HelpListing(
                [("<session-id>", "Connector session identifier")],
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.DestinationOptions => HelpListing(
                RemainingDestinationOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SessionOptions => HelpListing(
                RemainingSessionOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SourceValue => HelpListing(
                GetSourceHelp(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.SweepOptions => HelpListing(
                RemainingSweepOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            PingPosition.Value => HelpListing(
                [("<value>", "Numeric or address value for the preceding keyword")],
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            _ => CliDiscoveryResult.None
        };
    }

    /// <summary>Help rows for the first argument after <c>ping</c> (destination vs session).</summary>
    private static List<(string Token, string Description)> BuildTargetHelpEntries() =>
    [
        ("<destination>", "IPv4 address, IPv6 address, or hostname"),
        ("session", "Ping a connector session")
    ];

    /// <summary>
    /// Completes the first ping argument: currently only the <c>session</c> keyword (hosts are free-form).
    /// </summary>
    private CliDiscoveryResult CompleteTargets(
        string partial,
        int tokenStart,
        int cursor,
        int terminalColumns)
    {
        var candidates = new List<string>();
        if ("session".StartsWith(partial, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("session");
        }

        return CompleteFromList(candidates, partial, tokenStart, cursor, appendSpace: true, terminalColumns);
    }

    /// <summary>
    /// Completes connector session ids after <c>ping session</c> from the registry.
    /// </summary>
    private CliDiscoveryResult CompleteSessions(
        string partial,
        int tokenStart,
        int cursor,
        int terminalColumns)
    {
        if (_sessionRegistry is null)
        {
            return CliDiscoveryResult.None;
        }

        var ids = CliDiscoveryOrdering.Sort(
            _sessionRegistry.List()
                .Select(s => s.SessionId)
                .Where(id => id.StartsWith(partial, StringComparison.OrdinalIgnoreCase)));
        return CompleteFromList(ids, partial, tokenStart, cursor, appendSpace: true, terminalColumns);
    }

    /// <summary>
    /// Completes local source addresses matching the destination family when known.
    /// </summary>
    private CliDiscoveryResult CompleteSource(
        IReadOnlyList<string> tokens,
        string partial,
        int tokenStart,
        int cursor,
        int terminalColumns)
    {
        var family = DetectFamily(tokens);
        IEnumerable<IPAddress> addresses = family == AddressFamily.InterNetworkV6
            ? _ipv6.GetLocalGlobalIpv6Addresses()
            : family == AddressFamily.InterNetwork
                ? _ipv6.GetLocalIpv4Addresses()
                : _ipv6.GetLocalIpv4Addresses().Concat(_ipv6.GetLocalGlobalIpv6Addresses());

        var list = addresses
            .Select(a => a.ToString())
            .Where(a => a.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return CompleteFromList(list, partial, tokenStart, cursor, appendSpace: true, terminalColumns);
    }

    /// <summary>
    /// Help entries for <c>source</c>: live local addresses, or a placeholder when none.
    /// </summary>
    private List<(string Token, string Description)> GetSourceHelp(IReadOnlyList<string> tokens)
    {
        var family = DetectFamily(tokens);
        IReadOnlyList<IPAddress> addresses = family == AddressFamily.InterNetworkV6
            ? _ipv6.GetLocalGlobalIpv6Addresses()
            : family == AddressFamily.InterNetwork
                ? _ipv6.GetLocalIpv4Addresses()
                : _ipv6.GetLocalIpv4Addresses().Concat(_ipv6.GetLocalGlobalIpv6Addresses()).ToArray();
        if (addresses.Count == 0)
        {
            return [("<address>", "Local source address matching the destination family")];
        }

        return addresses.Select(a => (a.ToString(), "Local interface address")).ToList();
    }

    /// <summary>
    /// Infers address family from a parseable IP at tokens[1]; null when hostname or missing.
    /// </summary>
    private static AddressFamily? DetectFamily(IReadOnlyList<string> tokens)
    {
        if (tokens.Count >= 2 &&
            IPAddress.TryParse(tokens[1], out var dest))
        {
            return dest.AddressFamily;
        }

        return null;
    }

    /// <summary>
    /// Destination-mode options not yet supplied (omit <c>df</c> for IPv6 literals).
    /// </summary>
    private static List<(string Token, string Description)> RemainingDestinationOptions(IReadOnlyList<string> tokens)
    {
        var supplied = SuppliedOptionKeys(tokens, startIndex: 2);
        var isIpv6Literal = tokens.Count >= 2 &&
                            IPAddress.TryParse(tokens[1], out var dest) &&
                            dest.AddressFamily == AddressFamily.InterNetworkV6;
        var options = new List<(string, string)>
        {
            ("repeat", "Number of echo requests"),
            ("size", "ICMP payload size in bytes"),
            ("timeout", "Per-probe timeout in seconds"),
            ("source", "Local source address"),
            ("sweep", "Sweep ICMP payload sizes")
        };
        if (!isIpv6Literal)
        {
            options.Insert(4, ("df", "Set Don't Fragment on IPv4 packets"));
        }

        return FilterOptions(options, supplied);
    }

    /// <summary>Session-mode options not yet supplied (<c>repeat</c>, <c>timeout</c>).</summary>
    private static List<(string Token, string Description)> RemainingSessionOptions(IReadOnlyList<string> tokens)
    {
        var supplied = SuppliedOptionKeys(tokens, startIndex: 3);
        return FilterOptions(
            [
                ("repeat", "Number of session health probes"),
                ("timeout", "Per-probe timeout in seconds")
            ],
            supplied);
    }

    /// <summary>Sweep sub-options (<c>min</c>/<c>max</c>/<c>interval</c>) not yet supplied after <c>sweep</c>.</summary>
    private static List<(string Token, string Description)> RemainingSweepOptions(IReadOnlyList<string> tokens)
    {
        var sweepIndex = IndexOf(tokens, "sweep");
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sweepIndex >= 0)
        {
            for (var i = sweepIndex + 1; i + 1 < tokens.Count; i += 2)
            {
                supplied.Add(tokens[i]);
            }
        }

        return FilterOptions(
            [
                ("min", "Minimum ICMP payload size"),
                ("max", "Maximum ICMP payload size"),
                ("interval", "Payload size increment")
            ],
            supplied);
    }

    /// <summary>Drops options whose token is in <paramref name="supplied"/>, then sorts for display.</summary>
    private static List<(string Token, string Description)> FilterOptions(
        IEnumerable<(string Token, string Description)> options,
        HashSet<string> supplied) =>
        CliDiscoveryOrdering.Sort(options.Where(o => !supplied.Contains(o.Token))).ToList();

    /// <summary>
    /// Collects option keywords already present from <paramref name="startIndex"/> (flags and key/value pairs).
    /// </summary>
    private static HashSet<string> SuppliedOptionKeys(IReadOnlyList<string> tokens, int startIndex)
    {
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var key = tokens[i];
            if (key.Equals("df", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("sweep", StringComparison.OrdinalIgnoreCase))
            {
                supplied.Add(key);
                continue;
            }

            if (IsValueKeyword(key) && i + 1 < tokens.Count)
            {
                supplied.Add(key);
                i++;
            }
        }

        return supplied;
    }

    /// <summary>Whether <paramref name="key"/> takes a following value (vs flag-only <c>df</c>/<c>sweep</c>).</summary>
    private static bool IsValueKeyword(string key) =>
        key.Equals("repeat", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("size", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("timeout", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("source", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("min", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("max", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("interval", StringComparison.OrdinalIgnoreCase);

    /// <summary>Classifies cursor position from completed ping tokens (target vs session vs options).</summary>
    private static PingPosition Classify(IReadOnlyList<string> tokens)
    {
        if (tokens.Count <= 1)
        {
            return PingPosition.Target;
        }

        if (tokens[1].Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count == 2)
            {
                return PingPosition.SessionId;
            }

            return ClassifySessionOptions(tokens);
        }

        return ClassifyOptions(tokens, addressIndex: 1);
    }

    /// <summary>
    /// Walks destination options after the address token to find keyword/value/sweep/source positions.
    /// </summary>
    private static PingPosition ClassifyOptions(IReadOnlyList<string> tokens, int addressIndex)
    {
        var i = addressIndex + 1;
        while (i < tokens.Count)
        {
            var key = tokens[i];
            if (key.Equals("df", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }

            if (key.Equals("sweep", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                while (i < tokens.Count)
                {
                    if (!keyIsSweep(tokens[i]))
                    {
                        break;
                    }

                    if (i + 1 >= tokens.Count)
                    {
                        return PingPosition.Value;
                    }

                    i += 2;
                }

                if (i >= tokens.Count)
                {
                    return PingPosition.SweepOptions;
                }

                continue;
            }

            if (IsValueKeyword(key))
            {
                if (i + 1 >= tokens.Count)
                {
                    return key.Equals("source", StringComparison.OrdinalIgnoreCase)
                        ? PingPosition.SourceValue
                        : PingPosition.Value;
                }

                i += 2;
                continue;
            }

            i++;
        }

        return PingPosition.DestinationOptions;

        static bool keyIsSweep(string k) =>
            k.Equals("min", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("max", StringComparison.OrdinalIgnoreCase) ||
            k.Equals("interval", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Walks session-mode options after <c>ping session &lt;id&gt;</c>.</summary>
    private static PingPosition ClassifySessionOptions(IReadOnlyList<string> tokens)
    {
        var i = 3;
        while (i < tokens.Count)
        {
            var key = tokens[i];
            if (IsValueKeyword(key))
            {
                if (i + 1 >= tokens.Count)
                {
                    return PingPosition.Value;
                }

                i += 2;
                continue;
            }

            i++;
        }

        return PingPosition.SessionOptions;
    }

    /// <summary>Case-insensitive index of <paramref name="value"/> in <paramref name="tokens"/>, or -1.</summary>
    private static int IndexOf(IReadOnlyList<string> tokens, string value)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whitespace-splits <paramref name="before"/> into tokens (empty when blank).</summary>
    private static string[] Split(string before) =>
        string.IsNullOrWhiteSpace(before)
            ? []
            : before.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Completes from option keyword tokens filtered by <paramref name="partial"/>.</summary>
    private static CliDiscoveryResult CompleteKeywords(
        IReadOnlyList<(string Token, string Description)> options,
        string partial,
        int tokenStart,
        int cursor,
        int terminalColumns) =>
        CompleteFromList(
            options.Select(o => o.Token)
                .Where(t => t.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .ToList(),
            partial,
            tokenStart,
            cursor,
            appendSpace: true,
            terminalColumns);

    /// <summary>Formats filtered help entries as a <see cref="CliDiscoveryKind.ContextHelp"/> result.</summary>
    private static CliDiscoveryResult HelpListing(
        IReadOnlyList<(string Token, string Description)> entries,
        string partial,
        int tokenStart,
        int cursor,
        int terminalColumns)
    {
        var filtered = CliDiscoveryOrdering.Sort(
            string.IsNullOrEmpty(partial)
                ? entries
                : entries.Where(e => e.Token.StartsWith(partial, StringComparison.OrdinalIgnoreCase)));
        if (filtered.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.ContextHelp,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = filtered.Select(e => e.Token).ToList(),
            Descriptions = filtered.Select(e => e.Description).ToList(),
            DisplayText = CliDiscoveryFormatter.FormatHelpListing(filtered, terminalColumns)
        };
    }

    /// <summary>
    /// Builds unique/ambiguous completion from <paramref name="matches"/> (prefix filter + LCP).
    /// </summary>
    private static CliDiscoveryResult CompleteFromList(
        IReadOnlyList<string> matches,
        string partial,
        int tokenStart,
        int cursor,
        bool appendSpace,
        int terminalColumns)
    {
        var list = CliDiscoveryOrdering.Sort(
                matches
                    .Where(m => m.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (list.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        if (list.Count == 1)
        {
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.UniqueCompletion,
                CompletionText = list[0],
                AppendTrailingSpace = appendSpace,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = list
            };
        }

        var prefix = list[0];
        for (var i = 1; i < list.Count; i++)
        {
            var current = list[i];
            var n = Math.Min(prefix.Length, current.Length);
            var shared = 0;
            while (shared < n &&
                   char.ToLowerInvariant(prefix[shared]) == char.ToLowerInvariant(current[shared]))
            {
                shared++;
            }

            prefix = prefix[..shared];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.AmbiguousCompletion,
            CompletionText = prefix,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = list,
            DisplayText = CliDiscoveryFormatter.FormatCandidateColumns(list, terminalColumns)
        };
    }
    /// <summary>
    /// Cursor position within a <c>ping</c> command for Tab/<c>?</c> dispatch.
    /// </summary>
    private enum PingPosition
    {
        /// <summary>After <c>ping</c>: expecting destination or <c>session</c>.</summary>
        Target,

        /// <summary>After <c>ping session</c>: expecting a session id.</summary>
        SessionId,

        /// <summary>After a destination: expecting destination option keywords.</summary>
        DestinationOptions,

        /// <summary>After <c>ping session &lt;id&gt;</c>: expecting session option keywords.</summary>
        SessionOptions,

        /// <summary>After <c>source</c> with no value yet: suggest local addresses.</summary>
        SourceValue,

        /// <summary>Inside/after <c>sweep</c>: expecting sweep sub-option keywords.</summary>
        SweepOptions,

        /// <summary>After a value-taking keyword with no value yet (numeric free-form).</summary>
        Value
    }
}
