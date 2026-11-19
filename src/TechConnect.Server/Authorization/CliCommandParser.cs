// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandParser.cs
//
// Purpose:
// Parses operator input against the CLI command catalog into structured outcomes.
//
// -----------------------------------------------------------------------------
namespace TechConnect.Server.Authorization;

/// <summary>
/// Resolves raw CLI input against <see cref="CliCommandCatalog"/> without executing handlers.
/// </summary>
internal static class CliCommandParser
{
    /// <summary>
    /// Parses whitespace-separated CLI input against <see cref="CliCommandCatalog.Roots"/>.
    /// </summary>
    /// <param name="input">Raw operator line; whitespace-only is invalid.</param>
    /// <returns>
    /// Invalid, incomplete, or resolved result. Does not authorize privilege or execute handlers.
    /// </returns>
    public static CliParseResult Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return CliParseResult.Invalid([]);
        }

        var tokens = input.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return CliParseResult.Invalid([]);
        }

        CliCommandNode? node = null;
        var level = CliCommandCatalog.Roots;
        var index = 0;

        while (index < tokens.Length)
        {
            var match = FindNode(level, tokens[index]);
            if (match is null)
            {
                return CliParseResult.Invalid(tokens);
            }

            node = match;
            index++;

            if (node.HasChildren)
            {
                level = node.Children;
                continue;
            }

            if (node.CommandId is null)
            {
                return CliParseResult.Invalid(tokens);
            }

            if (node.Argument is null)
            {
                if (index < tokens.Length)
                {
                    return CliParseResult.Invalid(tokens);
                }

                return CliParseResult.Resolved(node.CommandId.Value, tokens, node);
            }

            if (index >= tokens.Length)
            {
                if (node.Argument.Optional)
                {
                    return CliParseResult.Resolved(ResolveCommandId(node, argumentPresent: false), tokens, node);
                }

                return CliParseResult.Incomplete(tokens, node);
            }

            // Consume one argument token.
            index++;
            if (index < tokens.Length && !node.Argument.AllowTrailingTokens)
            {
                return CliParseResult.Invalid(tokens);
            }

            // Trailing tokens (e.g. connect serial keywords) stay in the token array for the handler.
            return CliParseResult.Resolved(ResolveCommandId(node, argumentPresent: true), tokens, node);
        }

        if (node is null)
        {
            return CliParseResult.Invalid(tokens);
        }

        // Allow executable parents that also have optional subcommands (e.g. show history).
        if (node.CommandId is { } commandId && node.Argument is null)
        {
            return CliParseResult.Resolved(commandId, tokens, node);
        }

        if (node.HasChildren || node.CommandId is null)
        {
            return CliParseResult.Incomplete(tokens, node);
        }

        if (node.Argument is not null)
        {
            if (node.Argument.Optional)
            {
                return CliParseResult.Resolved(ResolveCommandId(node, argumentPresent: false), tokens, node);
            }

            return CliParseResult.Incomplete(tokens, node);
        }

        return CliParseResult.Resolved(node.CommandId.Value, tokens, node);
    }

    /// <summary>
    /// Selects <see cref="CliCommandNode.CommandId"/> or <see cref="CliCommandNode.CommandIdWithArgument"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Executable node lacks <see cref="CliCommandNode.CommandId"/>.</exception>
    private static CliCommandId ResolveCommandId(CliCommandNode node, bool argumentPresent)
    {
        if (argumentPresent && node.CommandIdWithArgument is { } withArg)
        {
            return withArg;
        }

        return node.CommandId
               ?? throw new InvalidOperationException("Executable catalog node is missing CommandId.");
    }

    /// <summary>
    /// Legacy helper: true only when input fully resolves to an executable command.
    /// </summary>
    public static bool TryResolve(string? input, out CliCommandId commandId, out string[] tokens)
    {
        var result = Parse(input);
        tokens = result.Tokens;
        if (result.Status != CliParseStatus.Resolved)
        {
            commandId = default;
            return false;
        }

        commandId = result.CommandId;
        return true;
    }

    /// <summary>
    /// Finds the first node at <paramref name="level"/> matching <paramref name="token"/> (including aliases).
    /// </summary>
    private static CliCommandNode? FindNode(IReadOnlyList<CliCommandNode> level, string token)
    {
        foreach (var candidate in level)
        {
            if (candidate.MatchesToken(token))
            {
                return candidate;
            }
        }

        return null;
    }
}
