// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteDiscovery.cs
//
// Purpose:
// Provides Tab/? discovery for traceroute destination/options gated by
// CliCommandPolicy Traceroute privilege.
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Catalog-adjacent discovery for <c>traceroute</c> (destination + options).
/// </summary>
internal sealed class TracerouteDiscovery
{
    /// <summary>Local IPv4/global IPv6 addresses for <c>source</c> completion/help.</summary>
    private readonly IIPv6NetworkAvailability _ipv6;

    /// <summary>
    /// Initializes traceroute discovery with local address availability.
    /// </summary>
    /// <param name="ipv6">Local address provider for source suggestions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ipv6"/> is <see langword="null"/>.</exception>
    public TracerouteDiscovery(IIPv6NetworkAvailability ipv6)
    {
        _ipv6 = ipv6 ?? throw new ArgumentNullException(nameof(ipv6));
    }

    /// <summary>
    /// Returns whether <paramref name="before"/> starts with the <c>traceroute</c> verb (case-insensitive).
    /// </summary>
    /// <param name="before">Completed tokens before the partial under the cursor.</param>
    /// <returns><see langword="true"/> when discovery should be delegated to this helper.</returns>
    public static bool IsTracerouteContext(string before)
    {
        if (string.IsNullOrWhiteSpace(before))
        {
            return false;
        }

        var tokens = before.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens[0].Equals("traceroute", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Tab-completes traceroute options when <see cref="CliCommandId.Traceroute"/> is allowed.
    /// </summary>
    /// <remarks>
    /// Target (hostname/IP) is free-form: Complete returns None at that position; Help shows a placeholder.
    /// </remarks>
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
        if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Traceroute))
        {
            return CliDiscoveryResult.None;
        }

        var tokens = Split(before);
        return Classify(tokens) switch
        {
            Position.Target => CliDiscoveryResult.None,
            Position.Options => CompleteKeywords(
                RemainingOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            Position.SourceValue => CompleteSource(tokens, partial, tokenStart, cursor, terminalColumns),
            Position.Value => CliDiscoveryResult.None,
            _ => CliDiscoveryResult.None
        };
    }

    /// <summary>
    /// Context <c>?</c> help for the current traceroute position when Traceroute policy allows.
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
        if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Traceroute))
        {
            return CliDiscoveryResult.None;
        }

        var tokens = Split(before);
        return Classify(tokens) switch
        {
            Position.Target => HelpListing(
                [("<destination>", "IPv4 address, IPv6 address, or hostname")],
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            Position.Options => HelpListing(
                RemainingOptions(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            Position.SourceValue => HelpListing(
                GetSourceHelp(tokens),
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            Position.Value => HelpListing(
                [("<value>", "Numeric or address value for the preceding keyword")],
                partial,
                tokenStart,
                cursor,
                terminalColumns),
            _ => CliDiscoveryResult.None
        };
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
        if (tokens.Count >= 2 && IPAddress.TryParse(tokens[1], out var dest))
        {
            return dest.AddressFamily;
        }

        return null;
    }

    /// <summary>
    /// Traceroute option keywords not yet supplied after the destination.
    /// </summary>
    private static List<(string Token, string Description)> RemainingOptions(IReadOnlyList<string> tokens)
    {
        var supplied = SuppliedOptionKeys(tokens, startIndex: 2);
        return FilterOptions(
            [
                ("timeout", "Per-probe timeout in seconds"),
                ("probe", "Number of probes per hop"),
                ("ttl", "Maximum TTL / hop count"),
                ("source", "Local source address"),
                ("port", "Accepted for compatibility (unused; probes are ICMP Echo)"),
                ("size", "ICMP Echo payload size in bytes")
            ],
            supplied);
    }

    /// <summary>Drops options whose token is in <paramref name="supplied"/>, then sorts for display.</summary>
    private static List<(string Token, string Description)> FilterOptions(
        IEnumerable<(string Token, string Description)> options,
        HashSet<string> supplied) =>
        CliDiscoveryOrdering.Sort(options.Where(o => !supplied.Contains(o.Token))).ToList();

    /// <summary>
    /// Collects option keywords already present as complete key/value pairs from <paramref name="startIndex"/>.
    /// </summary>
    private static HashSet<string> SuppliedOptionKeys(IReadOnlyList<string> tokens, int startIndex)
    {
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var key = tokens[i];
            if (IsValueKeyword(key) && i + 1 < tokens.Count)
            {
                supplied.Add(key);
                i++;
            }
        }

        return supplied;
    }

    /// <summary>Whether <paramref name="key"/> is a traceroute keyword that takes a following value.</summary>
    private static bool IsValueKeyword(string key) =>
        key.Equals("timeout", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("probe", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ttl", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("source", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("size", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("port", StringComparison.OrdinalIgnoreCase);

    /// <summary>Classifies cursor position from completed traceroute tokens.</summary>
    private static Position Classify(IReadOnlyList<string> tokens)
    {
        if (tokens.Count <= 1)
        {
            return Position.Target;
        }

        return ClassifyOptions(tokens, addressIndex: 1);
    }

    /// <summary>
    /// Walks options after the destination to find keyword/value/source positions.
    /// </summary>
    private static Position ClassifyOptions(IReadOnlyList<string> tokens, int addressIndex)
    {
        var start = addressIndex + 1;
        for (var i = start; i < tokens.Count; i++)
        {
            var key = tokens[i];
            if (!IsValueKeyword(key))
            {
                continue;
            }

            if (i + 1 >= tokens.Count)
            {
                return key.Equals("source", StringComparison.OrdinalIgnoreCase)
                    ? Position.SourceValue
                    : Position.Value;
            }

            i++;
        }

        return Position.Options;
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
    /// Cursor position within a <c>traceroute</c> command for Tab/<c>?</c> dispatch.
    /// </summary>
    private enum Position
    {
        /// <summary>After <c>traceroute</c>: expecting a destination (free-form for Tab).</summary>
        Target,

        /// <summary>After a destination: expecting option keywords.</summary>
        Options,

        /// <summary>After <c>source</c> with no value yet: suggest local addresses.</summary>
        SourceValue,

        /// <summary>After a value-taking keyword with no value yet (numeric free-form).</summary>
        Value
    }
}
