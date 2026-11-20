// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandDiscovery.cs
//
// Purpose:
// Context-aware Tab completion and ? help against CliCommandCatalog with
// CliCommandPolicy visibility filtering.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Context-aware Tab completion and <c>?</c> help against <see cref="CliCommandCatalog"/>.
/// </summary>
/// <remarks>
/// Does not execute commands; visibility is derived from <see cref="CliCommandPolicy"/>
/// (default-deny; session privilege ≥ command minimum). Ping/traceroute contexts are
/// delegated to specialized discovery helpers that also gate on Ping/Traceroute policy.
/// </remarks>
internal sealed class CliCommandDiscovery
{
    /// <summary>Optional session registry for session-id argument suggestions.</summary>
    private readonly ConnectorSessionRegistry? _sessionRegistry;

    /// <summary>Ping-specific discovery helper.</summary>
    private readonly PingDiscovery _pingDiscovery;

    /// <summary>Traceroute-specific discovery helper.</summary>
    private readonly TracerouteDiscovery _tracerouteDiscovery;

    /// <summary>
    /// Initializes discovery with optional session registry and IPv6 helpers.
    /// </summary>
    /// <param name="sessionRegistry">Optional connector session registry.</param>
    /// <param name="ipv6Availability">Optional local address availability.</param>
    public CliCommandDiscovery(
        ConnectorSessionRegistry? sessionRegistry = null,
        IIPv6NetworkAvailability? ipv6Availability = null)
    {
        _sessionRegistry = sessionRegistry;
        var ipv6 = ipv6Availability ?? new SystemIPv6NetworkAvailability();
        _pingDiscovery = new PingDiscovery(sessionRegistry, ipv6);
        _tracerouteDiscovery = new TracerouteDiscovery(ipv6);
    }

    /// <summary>
    /// Resolves Tab completion at the given cursor against the catalog or ping/traceroute helpers.
    /// </summary>
    /// <param name="line">Current edit buffer.</param>
    /// <param name="cursor">Cursor index within <paramref name="line"/>.</param>
    /// <param name="identity">Operator identity whose privilege filters visibility.</param>
    /// <param name="terminalColumns">Terminal width for listings.</param>
    /// <returns>Completion result (possibly <see cref="CliDiscoveryResult.None"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public CliDiscoveryResult Complete(
        string line,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var (before, tokenStart, partial, _) = Analyze(line, cursor);
        if (PingDiscovery.IsPingContext(before))
        {
            return _pingDiscovery.Complete(before, partial, tokenStart, cursor, identity, terminalColumns);
        }

        if (TracerouteDiscovery.IsTracerouteContext(before))
        {
            return _tracerouteDiscovery.Complete(before, partial, tokenStart, cursor, identity, terminalColumns);
        }

        var context = ResolveContext(before, identity.Privilege);
        if (context.Status == ContextStatus.Invalid)
        {
            return CliDiscoveryResult.None;
        }

        if (context.TrailingMode == TrailingMode.Keyword)
        {
            return CompleteFromCandidates(
                RemainingConnectKeywords(context.TrailingTokens),
                partial,
                tokenStart,
                cursor,
                appendTrailingSpace: true,
                terminalColumns);
        }

        if (context.TrailingMode == TrailingMode.KeywordValue &&
            context.TrailingKeyword is { } keyword)
        {
            return CompleteFromCandidates(
                ConnectSerialSettings.GetValueSuggestions(keyword),
                partial,
                tokenStart,
                cursor,
                appendTrailingSpace: true,
                terminalColumns);
        }

        if (context.ExpectingArgument is { } argSpec)
        {
            return CompleteArgument(
                argSpec,
                context.ArgumentCommandId,
                partial,
                tokenStart,
                cursor,
                identity,
                terminalColumns);
        }

        var matches = MatchChildren(context.Level, partial, identity.Privilege)
            .OrderBy(m => m.Token, CliDiscoveryOrdering.Comparer)
            .ToList();
        if (matches.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        if (matches.Count == 1)
        {
            var node = matches[0];
            var appendSpace = node.HasChildren || AcceptsAuthorizedArgument(node, identity.Privilege);
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.UniqueCompletion,
                CompletionText = node.Token,
                AppendTrailingSpace = appendSpace,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = [node.Token]
            };
        }

        var tokens = matches.Select(m => m.Token).ToList();
        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.AmbiguousCompletion,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = tokens,
            CompletionText = LongestCommonPrefix(tokens),
            DisplayText = CliDiscoveryFormatter.FormatCandidateColumns(tokens, terminalColumns)
        };
    }

    /// <summary>
    /// Resolves context-sensitive <c>?</c> help at the given cursor against the catalog or helpers.
    /// </summary>
    /// <param name="line">Current edit buffer.</param>
    /// <param name="cursor">Cursor index within <paramref name="line"/>.</param>
    /// <param name="identity">Operator identity whose privilege filters visibility.</param>
    /// <param name="terminalColumns">Terminal width for listings.</param>
    /// <returns>Context-help result (possibly <see cref="CliDiscoveryResult.None"/>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public CliDiscoveryResult Help(
        string line,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var (before, tokenStart, partial, _) = Analyze(line, cursor);
        if (PingDiscovery.IsPingContext(before))
        {
            return _pingDiscovery.Help(before, partial, tokenStart, cursor, identity, terminalColumns);
        }

        if (TracerouteDiscovery.IsTracerouteContext(before))
        {
            return _tracerouteDiscovery.Help(before, partial, tokenStart, cursor, identity, terminalColumns);
        }

        var context = ResolveContext(before, identity.Privilege);
        if (context.Status == ContextStatus.Invalid)
        {
            return CliDiscoveryResult.None;
        }

        if (context.TrailingMode == TrailingMode.Keyword)
        {
            var entries = CliDiscoveryOrdering.Sort(
                RemainingConnectKeywords(context.TrailingTokens)
                    .Where(k => k.Keyword.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .Select(k => (Token: k.Keyword, k.Description)));
            if (entries.Count == 0)
            {
                return CliDiscoveryResult.None;
            }

            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.ContextHelp,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = entries.Select(e => e.Token).ToList(),
                Descriptions = entries.Select(e => e.Description).ToList(),
                DisplayText = CliDiscoveryFormatter.FormatHelpListing(entries, terminalColumns)
            };
        }

        if (context.TrailingMode == TrailingMode.KeywordValue &&
            context.TrailingKeyword is { } valueKeyword)
        {
            var values = CliDiscoveryOrdering.Sort(
                ConnectSerialSettings.GetValueSuggestions(valueKeyword)
                    .Where(v => v.StartsWith(partial, StringComparison.OrdinalIgnoreCase)));
            if (values.Count == 0)
            {
                return CliDiscoveryResult.None;
            }

            var description = ConnectSerialSettings.ParameterKeywords
                .First(k => k.Keyword.Equals(valueKeyword, StringComparison.OrdinalIgnoreCase))
                .Description;
            var entries = values.Select(v => (v, description)).ToList();
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.ContextHelp,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = values,
                Descriptions = values.Select(_ => description).ToList(),
                DisplayText = CliDiscoveryFormatter.FormatHelpListing(entries, terminalColumns)
            };
        }

        if (context.ExpectingArgument is { } argSpec)
        {
            return HelpArgument(
                argSpec,
                context.ArgumentCommandId,
                partial,
                tokenStart,
                cursor,
                identity,
                terminalColumns);
        }

        var matches = MatchChildren(context.Level, partial, identity.Privilege)
            .OrderBy(m => m.Token, CliDiscoveryOrdering.Comparer)
            .ToList();
        if (matches.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        var commandEntries = matches
            .Select(m => (m.Token, m.Description))
            .ToList();
        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.ContextHelp,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = commandEntries.Select(e => e.Token).ToList(),
            Descriptions = commandEntries.Select(e => e.Description).ToList(),
            DisplayText = CliDiscoveryFormatter.FormatHelpListing(commandEntries, terminalColumns)
        };
    }

    /// <summary>
    /// Completes the current token against live argument suggestions for <paramref name="argSpec"/>.
    /// </summary>
    /// <param name="argSpec">Expected argument shape from the catalog node.</param>
    /// <param name="argumentCommandId">Command id whose policy gates suggestion visibility.</param>
    /// <param name="partial">Partial token being completed.</param>
    /// <param name="tokenStart">Buffer index of the token start.</param>
    /// <param name="cursor">Cursor index (replace end).</param>
    /// <param name="identity">Operator used for <see cref="CliCommandPolicy"/> checks.</param>
    /// <param name="terminalColumns">Width for ambiguous listings.</param>
    /// <returns>Unique, ambiguous, or empty completion result.</returns>
    private CliDiscoveryResult CompleteArgument(
        CliArgumentSpec argSpec,
        CliCommandId? argumentCommandId,
        string partial,
        int tokenStart,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        var suggestions = GetArgumentSuggestions(argSpec, argumentCommandId, identity).ToList();
        return CompleteFromCandidates(
            suggestions,
            partial,
            tokenStart,
            cursor,
            appendTrailingSpace: true,
            terminalColumns);
    }

    /// <summary>
    /// Builds Cisco-style <c>?</c> help for an expected argument position.
    /// </summary>
    /// <remarks>
    /// Most argument kinds show the placeholder form (<see cref="CliArgumentSpec.DisplayName"/>),
    /// not live value dumps; Tab lists values. Privilege-level arguments list allowed levels when
    /// any match <paramref name="partial"/>.
    /// </remarks>
    private CliDiscoveryResult HelpArgument(
        CliArgumentSpec argSpec,
        CliCommandId? argumentCommandId,
        string partial,
        int tokenStart,
        int cursor,
        AuthenticatedOperator identity,
        int terminalColumns)
    {
        // Cisco-style ?: show the expected argument form, not live value dumps (Tab lists values).
        // Ping/traceroute targets are owned by dedicated discovery once the verb is present.
        if (argSpec.Kind is CliArgumentKind.SessionId or CliArgumentKind.BaudRate or CliArgumentKind.Username
            or CliArgumentKind.PingTarget or CliArgumentKind.TracerouteTarget or CliArgumentKind.Hostname
            or CliArgumentKind.CopySource or CliArgumentKind.CopyDestination)
        {
            if (argumentCommandId is { } required &&
                !CliCommandPolicy.IsAllowed(identity.Privilege, required))
            {
                return CliDiscoveryResult.None;
            }

            var entries = new List<(string, string)> { (argSpec.DisplayName, argSpec.Description) };
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.ContextHelp,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = [argSpec.DisplayName],
                Descriptions = [argSpec.Description],
                DisplayText = CliDiscoveryFormatter.FormatHelpListing(entries, terminalColumns)
            };
        }

        if (argSpec.Kind == CliArgumentKind.PrivilegeLevel)
        {
            var levels = CliDiscoveryOrdering.Sort(
                GetArgumentSuggestions(argSpec, argumentCommandId, identity)
                    .Where(s => s.StartsWith(partial, StringComparison.OrdinalIgnoreCase)));
            if (levels.Count == 0)
            {
                var entries = new List<(string, string)> { (argSpec.DisplayName, argSpec.Description) };
                return new CliDiscoveryResult
                {
                    Kind = CliDiscoveryKind.ContextHelp,
                    ReplaceStart = tokenStart,
                    ReplaceEnd = cursor,
                    Tokens = [argSpec.DisplayName],
                    Descriptions = [argSpec.Description],
                    DisplayText = CliDiscoveryFormatter.FormatHelpListing(entries, terminalColumns)
                };
            }

            var levelEntries = levels.Select(l => (l, argSpec.Description)).ToList();
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.ContextHelp,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = levels.ToList(),
                Descriptions = levels.Select(_ => argSpec.Description).ToList(),
                DisplayText = CliDiscoveryFormatter.FormatHelpListing(levelEntries, terminalColumns)
            };
        }

        var fallback = new List<(string, string)> { (argSpec.DisplayName, argSpec.Description) };
        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.ContextHelp,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = [argSpec.DisplayName],
            Descriptions = [argSpec.Description],
            DisplayText = CliDiscoveryFormatter.FormatHelpListing(fallback, terminalColumns)
        };
    }

    /// <summary>
    /// Yields Tab completion candidates for <paramref name="argSpec"/> under policy.
    /// </summary>
    /// <param name="argSpec">Argument kind driving suggestion source.</param>
    /// <param name="argumentCommandId">Command that must be allowed, when set.</param>
    /// <param name="identity">Operator privilege for gating.</param>
    /// <returns>
    /// Baud rates, privilege levels, session ids, or copy URL schemes depending on kind;
    /// empty when policy denies or no registry/source is available.
    /// </returns>
    private IEnumerable<string> GetArgumentSuggestions(
        CliArgumentSpec argSpec,
        CliCommandId? argumentCommandId,
        AuthenticatedOperator identity)
    {
        if (argumentCommandId is { } required &&
            !CliCommandPolicy.IsAllowed(identity.Privilege, required))
        {
            yield break;
        }

        switch (argSpec.Kind)
        {
            case CliArgumentKind.BaudRate:
                if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Connect))
                {
                    yield break;
                }

                foreach (var rate in ConnectSerialSettings.SupportedBaudRateValues)
                {
                    yield return rate;
                }

                yield break;

            case CliArgumentKind.PrivilegeLevel:
                if (!CliCommandPolicy.IsAllowed(identity.Privilege, CliCommandId.Enable))
                {
                    yield break;
                }

                for (var level = PrivilegeLevel.Minimum; level <= PrivilegeLevel.Maximum; level++)
                {
                    yield return level.ToString(CultureInfo.InvariantCulture);
                }

                yield break;

            case CliArgumentKind.SessionId:
                if (_sessionRegistry is null)
                {
                    yield break;
                }

                foreach (var sessionId in CliDiscoveryOrdering.Sort(
                             _sessionRegistry.List().Select(s => s.SessionId)))
                {
                    yield return sessionId;
                }

                yield break;

            case CliArgumentKind.ConfigureMode:
                yield return "terminal";
                yield break;

            case CliArgumentKind.WriteTarget:
                yield return "memory";
                yield break;

            case CliArgumentKind.CopySource:
                foreach (var scheme in new[]
                         {
                             "http:", "https:", "tftp:", "scp:", "xmodem:", "ymodem:"
                         })
                {
                    yield return scheme;
                }

                yield break;

            case CliArgumentKind.CopyDestination:
            {
                var any = false;
                if (_sessionRegistry is not null)
                {
                    foreach (var sessionId in CliDiscoveryOrdering.Sort(
                                 _sessionRegistry.List().Select(s => s.SessionId)))
                    {
                        any = true;
                        yield return "serial:" + sessionId;
                    }
                }

                if (!any)
                {
                    yield return "serial:";
                }

                yield break;
            }
        }
    }

    /// <summary>
    /// Connect serial keywords not yet supplied as complete key/value pairs after the session id.
    /// </summary>
    /// <param name="trailingTokens">Tokens after the primary connect argument.</param>
    /// <returns>Remaining <see cref="ConnectSerialSettings.ParameterKeywords"/> entries.</returns>
    private static IEnumerable<(string Keyword, string Description)> RemainingConnectKeywords(
        IReadOnlyList<string> trailingTokens)
    {
        var supplied = ConnectSerialSettings.GetSuppliedKeywords(trailingTokens);
        foreach (var entry in ConnectSerialSettings.ParameterKeywords)
        {
            if (!supplied.Contains(entry.Keyword))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Filters <paramref name="candidates"/> by <paramref name="partial"/> and builds a completion result.
    /// </summary>
    /// <param name="candidates">Raw suggestion strings.</param>
    /// <param name="partial">Case-insensitive prefix filter.</param>
    /// <param name="tokenStart">Replace-range start.</param>
    /// <param name="cursor">Replace-range end.</param>
    /// <param name="appendTrailingSpace">Whether a unique match should append a space.</param>
    /// <param name="terminalColumns">Column width for ambiguous display text.</param>
    /// <returns>
    /// <see cref="CliDiscoveryKind.UniqueCompletion"/>, <see cref="CliDiscoveryKind.AmbiguousCompletion"/>
    /// (with longest common prefix), or <see cref="CliDiscoveryResult.None"/>.
    /// </returns>
    private static CliDiscoveryResult CompleteFromCandidates(
        IEnumerable<string> candidates,
        string partial,
        int tokenStart,
        int cursor,
        bool appendTrailingSpace,
        int terminalColumns)
    {
        var matches = CliDiscoveryOrdering.Sort(
                candidates
                    .Where(c => c.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return CliDiscoveryResult.None;
        }

        if (matches.Count == 1)
        {
            return new CliDiscoveryResult
            {
                Kind = CliDiscoveryKind.UniqueCompletion,
                CompletionText = matches[0],
                AppendTrailingSpace = appendTrailingSpace,
                ReplaceStart = tokenStart,
                ReplaceEnd = cursor,
                Tokens = matches
            };
        }

        return new CliDiscoveryResult
        {
            Kind = CliDiscoveryKind.AmbiguousCompletion,
            ReplaceStart = tokenStart,
            ReplaceEnd = cursor,
            Tokens = matches,
            CompletionText = LongestCommonPrefix(matches),
            DisplayText = CliDiscoveryFormatter.FormatCandidateColumns(matches, terminalColumns)
        };
    }

    /// <summary>
    /// Completes from keyword/description pairs by matching on the keyword text only.
    /// </summary>
    private static CliDiscoveryResult CompleteFromCandidates(
        IEnumerable<(string Keyword, string Description)> candidates,
        string partial,
        int tokenStart,
        int cursor,
        bool appendTrailingSpace,
        int terminalColumns) =>
        CompleteFromCandidates(
            candidates.Select(c => c.Keyword),
            partial,
            tokenStart,
            cursor,
            appendTrailingSpace,
            terminalColumns);

    /// <summary>
    /// Case-insensitive longest common prefix across <paramref name="values"/> (for ambiguous Tab).
    /// </summary>
    /// <param name="values">Candidate tokens (must be non-empty for a useful prefix).</param>
    /// <returns>Shared prefix, or empty when none.</returns>
    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var prefix = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var current = values[i];
            var length = Math.Min(prefix.Length, current.Length);
            var shared = 0;
            while (shared < length &&
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

        return prefix;
    }

    /// <summary>
    /// Whether unique completion of <paramref name="node"/> should append a trailing space for an argument.
    /// </summary>
    /// <param name="node">Matched catalog node.</param>
    /// <param name="privilege">Operator privilege for the argument's command id.</param>
    /// <returns>
    /// <see langword="true"/> when the node has an argument and the governing command is allowed.
    /// </returns>
    private static bool AcceptsAuthorizedArgument(CliCommandNode node, PrivilegeLevel privilege)
    {
        if (node.Argument is null)
        {
            return false;
        }

        var argumentCommand = node.CommandIdWithArgument ?? node.CommandId;
        return argumentCommand is { } id && CliCommandPolicy.IsAllowed(privilege, id);
    }

    /// <summary>
    /// Visible catalog children at <paramref name="level"/> whose token or alias matches <paramref name="partial"/>.
    /// </summary>
    /// <param name="level">Current command-tree level.</param>
    /// <param name="partial">Case-insensitive prefix (empty matches all visible).</param>
    /// <param name="privilege">Privilege used by <see cref="CliCommandCatalog.IsVisible"/>.</param>
    /// <returns>Matching nodes (unordered; callers sort).</returns>
    private static IEnumerable<CliCommandNode> MatchChildren(
        IReadOnlyList<CliCommandNode> level,
        string partial,
        PrivilegeLevel privilege)
    {
        foreach (var node in level)
        {
            if (!CliCommandCatalog.IsVisible(node, privilege))
            {
                continue;
            }

            if (node.Token.StartsWith(partial, StringComparison.OrdinalIgnoreCase) ||
                node.Aliases.Any(a => a.StartsWith(partial, StringComparison.OrdinalIgnoreCase)))
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Splits the edit buffer at <paramref name="cursor"/> into completed text, token start, and partial.
    /// </summary>
    /// <param name="line">Current line buffer (null treated as empty).</param>
    /// <param name="cursor">Cursor index clamped to the line length.</param>
    /// <returns>
    /// <c>Before</c>: trimmed completed tokens before the partial;
    /// <c>TokenStart</c>/<c>Partial</c>: current token under the cursor;
    /// <c>EndsWithSpace</c>: whether the cursor sits after whitespace (empty partial).
    /// </returns>
    private static (string Before, int TokenStart, string Partial, bool EndsWithSpace) Analyze(
        string line,
        int cursor)
    {
        line ??= string.Empty;
        cursor = Math.Clamp(cursor, 0, line.Length);
        var upToCursor = line[..cursor];
        var endsWithSpace = upToCursor.Length > 0 && char.IsWhiteSpace(upToCursor[^1]);

        if (endsWithSpace)
        {
            return (upToCursor.TrimEnd(), cursor, string.Empty, true);
        }

        var tokenStart = upToCursor.Length;
        while (tokenStart > 0 && !char.IsWhiteSpace(upToCursor[tokenStart - 1]))
        {
            tokenStart--;
        }

        var partial = upToCursor[tokenStart..];
        var before = upToCursor[..tokenStart].TrimEnd();
        return (before, tokenStart, partial, false);
    }

    /// <summary>
    /// Walks <see cref="CliCommandCatalog"/> tokens in <paramref name="before"/> to decide discovery mode.
    /// </summary>
    /// <param name="before">Completed tokens before the partial under the cursor.</param>
    /// <param name="privilege">Operator privilege for visibility and policy.</param>
    /// <returns>
    /// Root/child listing, argument expectation, connect trailing keywords/values, or Invalid
    /// when a token is unknown or invisible. Ping/traceroute are handled before this method.
    /// </returns>
    private static Context ResolveContext(string before, PrivilegeLevel privilege)
    {
        if (string.IsNullOrWhiteSpace(before))
        {
            return Context.AtRoots(CliCommandCatalog.Roots);
        }

        var tokens = before.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var level = CliCommandCatalog.Roots;
        CliCommandNode? node = null;
        for (var i = 0; i < tokens.Length; i++)
        {
            var match = level.FirstOrDefault(n => n.MatchesToken(tokens[i]));
            if (match is null)
            {
                return Context.Invalid;
            }

            if (!CliCommandCatalog.IsVisible(match, privilege))
            {
                return Context.Invalid;
            }

            node = match;
            if (node.HasChildren)
            {
                level = node.Children;
                continue;
            }

            if (node.Argument is not null)
            {
                if (i + 1 >= tokens.Length)
                {
                    return ArgumentContext(node, privilege);
                }

                i++; // consume primary argument value (e.g. session-id or copy source)
                if (!node.Argument.AllowTrailingTokens)
                {
                    return i + 1 < tokens.Length ? Context.Invalid : Context.AtRoots([]);
                }

                // Connect alone owns baud/databits/parity/stopbits trailing keyword pairs.
                // Copy (and other AllowTrailingTokens commands) must not inherit that context.
                if (node.CommandId == CliCommandId.Connect)
                {
                    var trailing = tokens.AsSpan(i + 1).ToArray();
                    return TrailingKeywordContext(node, privilege, trailing);
                }

                if (node.CommandId == CliCommandId.Copy)
                {
                    if (i + 1 >= tokens.Length)
                    {
                        return Context.ForArgument(
                            CliCommandCatalog.CopyDestinationArgument,
                            CliCommandId.Copy);
                    }

                    // Source + destination present; nothing further to complete.
                    return i + 2 < tokens.Length ? Context.Invalid : Context.AtRoots([]);
                }

                // Ping/traceroute use dedicated discovery before ResolveContext; treat leftover
                // tokens after their primary argument as end-of-command when we land here.
                return i + 1 < tokens.Length ? Context.Invalid : Context.AtRoots([]);
            }

            return Context.AtRoots([]);
        }

        if (node is null)
        {
            return Context.AtRoots(CliCommandCatalog.Roots);
        }

        if (node.HasChildren)
        {
            return Context.AtRoots(node.Children);
        }

        if (node.Argument is not null)
        {
            return ArgumentContext(node, privilege);
        }

        return Context.AtRoots([]);
    }

    /// <summary>
    /// Builds connect trailing-keyword context from tokens after the session id.
    /// </summary>
    /// <param name="node">Connect command node.</param>
    /// <param name="privilege">Must allow <see cref="CliCommandId.Connect"/>.</param>
    /// <param name="trailing">Baud/databits/parity/stopbits keyword pairs (and optional incomplete keyword).</param>
    /// <returns>
    /// Keyword listing when the trailing count is even; keyword-value when odd and the last
    /// token is a known parameter keyword; Invalid otherwise.
    /// </returns>
    private static Context TrailingKeywordContext(
        CliCommandNode node,
        PrivilegeLevel privilege,
        IReadOnlyList<string> trailing)
    {
        var commandId = node.CommandId;
        if (commandId is null || !CliCommandPolicy.IsAllowed(privilege, commandId.Value))
        {
            return Context.Invalid;
        }

        // Odd count: last token is a keyword awaiting its value.
        if (trailing.Count % 2 == 1)
        {
            var keyword = trailing[^1];
            if (!ConnectSerialSettings.IsParameterKeyword(keyword))
            {
                return Context.Invalid;
            }

            var completePairs = trailing.Take(trailing.Count - 1).ToArray();
            return Context.ForKeywordValue(completePairs, keyword);
        }

        return Context.ForKeywords(trailing);
    }

    /// <summary>
    /// Context for a node that expects its primary argument next (or optional-arg end-of-command).
    /// </summary>
    /// <param name="node">Leaf or argument-bearing catalog node.</param>
    /// <param name="privilege">Operator privilege for the argument command id.</param>
    /// <returns>
    /// Argument context when allowed; empty roots when an optional argument is denied;
    /// Invalid when a required argument's command is denied.
    /// </returns>
    private static Context ArgumentContext(CliCommandNode node, PrivilegeLevel privilege)
    {
        var argumentCommand = node.CommandIdWithArgument ?? node.CommandId;
        if (argumentCommand is { } required &&
            !CliCommandPolicy.IsAllowed(privilege, required))
        {
            return node.Argument!.Optional ? Context.AtRoots([]) : Context.Invalid;
        }

        return Context.ForArgument(node.Argument!, argumentCommand);
    }

    /// <summary>
    /// Whether <see cref="Context"/> represents a usable discovery position or a hard stop.
    /// </summary>
    private enum ContextStatus
    {
        /// <summary>Valid catalog position for Tab/<c>?</c>.</summary>
        Ok,

        /// <summary>Unknown, invisible, or policy-denied path; discovery returns None.</summary>
        Invalid
    }

    /// <summary>
    /// Connect trailing-token mode after the primary session-id argument.
    /// </summary>
    private enum TrailingMode
    {
        /// <summary>Not in connect trailing-keyword mode.</summary>
        None,

        /// <summary>Expecting a serial parameter keyword (baud/databits/parity/stopbits).</summary>
        Keyword,

        /// <summary>Expecting the value for <see cref="Context.TrailingKeyword"/>.</summary>
        KeywordValue
    }

    /// <summary>
    /// Resolved discovery position within the command tree or connect trailing parameters.
    /// </summary>
    private readonly struct Context
    {
        /// <summary>Ok vs Invalid.</summary>
        public ContextStatus Status { get; }

        /// <summary>Catalog children to list when not expecting an argument or trailing keywords.</summary>
        public IReadOnlyList<CliCommandNode> Level { get; }

        /// <summary>Primary argument spec when the next token is that argument.</summary>
        public CliArgumentSpec? ExpectingArgument { get; }

        /// <summary>Command id that gates the expected argument, when any.</summary>
        public CliCommandId? ArgumentCommandId { get; }

        /// <summary>Connect trailing keyword/value mode.</summary>
        public TrailingMode TrailingMode { get; }

        /// <summary>Already-supplied trailing tokens (complete pairs for keyword mode).</summary>
        public IReadOnlyList<string> TrailingTokens { get; }

        /// <summary>Incomplete trailing keyword awaiting a value when mode is KeywordValue.</summary>
        public string? TrailingKeyword { get; }

        /// <summary>
        /// Creates a context with the given discovery fields.
        /// </summary>
        private Context(
            ContextStatus status,
            IReadOnlyList<CliCommandNode> level,
            CliArgumentSpec? arg,
            CliCommandId? argumentCommandId = null,
            TrailingMode trailingMode = TrailingMode.None,
            IReadOnlyList<string>? trailingTokens = null,
            string? trailingKeyword = null)
        {
            Status = status;
            Level = level;
            ExpectingArgument = arg;
            ArgumentCommandId = argumentCommandId;
            TrailingMode = trailingMode;
            TrailingTokens = trailingTokens ?? [];
            TrailingKeyword = trailingKeyword;
        }

        /// <summary>Shared invalid context (empty level, no argument).</summary>
        public static Context Invalid { get; } = new(ContextStatus.Invalid, [], null);

        /// <summary>
        /// Listing context at the given catalog level (roots or children).
        /// </summary>
        /// <param name="level">Nodes visible for completion/help.</param>
        public static Context AtRoots(IReadOnlyList<CliCommandNode> level) =>
            new(ContextStatus.Ok, level, null);

        /// <summary>
        /// Context expecting <paramref name="arg"/> next, gated by <paramref name="argumentCommandId"/>.
        /// </summary>
        public static Context ForArgument(CliArgumentSpec arg, CliCommandId? argumentCommandId) =>
            new(ContextStatus.Ok, [], arg, argumentCommandId);

        /// <summary>
        /// Connect mode: suggest remaining serial parameter keywords.
        /// </summary>
        /// <param name="trailingTokens">Complete keyword/value pairs already entered.</param>
        public static Context ForKeywords(IReadOnlyList<string> trailingTokens) =>
            new(
                ContextStatus.Ok,
                [],
                null,
                trailingMode: TrailingMode.Keyword,
                trailingTokens: trailingTokens);

        /// <summary>
        /// Connect mode: suggest values for the incomplete trailing <paramref name="keyword"/>.
        /// </summary>
        /// <param name="trailingTokens">Complete pairs before the incomplete keyword.</param>
        /// <param name="keyword">Parameter keyword awaiting its value.</param>
        public static Context ForKeywordValue(IReadOnlyList<string> trailingTokens, string keyword) =>
            new(
                ContextStatus.Ok,
                [],
                null,
                trailingMode: TrailingMode.KeywordValue,
                trailingTokens: trailingTokens,
                trailingKeyword: keyword);
    }
}
