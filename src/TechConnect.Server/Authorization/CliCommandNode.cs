// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliCommandNode.cs
//
// Purpose:
// Represents one node in the shared CLI command tree (token, optional executable
// id, children, argument, and aliases).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// One node in the shared CLI command tree (token, optional executable id, children, argument).
/// </summary>
/// <remarks>
/// Intermediate parents typically have no <see cref="CommandId"/>.
/// A node may be both executable and have children (e.g. <c>show history</c>).
/// Instances are immutable after construction and safe to share across threads.
/// </remarks>
internal sealed class CliCommandNode
{
    /// <summary>
    /// Creates a catalog node.
    /// </summary>
    /// <param name="token">Primary token matched case-insensitively.</param>
    /// <param name="description">Help/discovery description.</param>
    /// <param name="commandId">Executable command id when this node can run without an argument.</param>
    /// <param name="children">Child nodes; empty when leaf.</param>
    /// <param name="argument">Optional argument specification.</param>
    /// <param name="aliases">Alternate tokens that match this node.</param>
    /// <param name="commandIdWithArgument">
    /// Distinct command id used when <paramref name="argument"/> is present on the input line.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="token"/> or <paramref name="description"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="commandIdWithArgument"/> is set without <paramref name="argument"/>.
    /// </exception>
    public CliCommandNode(
        string token,
        string description,
        CliCommandId? commandId = null,
        IReadOnlyList<CliCommandNode>? children = null,
        CliArgumentSpec? argument = null,
        IReadOnlyList<string>? aliases = null,
        CliCommandId? commandIdWithArgument = null)
    {
        Token = token ?? throw new ArgumentNullException(nameof(token));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        CommandId = commandId;
        Children = children ?? Array.Empty<CliCommandNode>();
        Argument = argument;
        Aliases = aliases ?? Array.Empty<string>();
        CommandIdWithArgument = commandIdWithArgument;
        if (commandIdWithArgument is not null && argument is null)
        {
            throw new ArgumentException(
                "commandIdWithArgument requires an argument specification.",
                nameof(commandIdWithArgument));
        }
    }

    /// <summary>
    /// Primary token for this node.
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// Help/discovery description.
    /// </summary>
    public string Description { get; }

    /// <summary>Null for intermediate (non-executable) nodes such as <c>show</c> / <c>set</c>.</summary>
    public CliCommandId? CommandId { get; }

    /// <summary>
    /// When set with an optional <see cref="Argument"/>, identifies the distinct command used when
    /// the argument token is present (e.g. <c>disconnect</c> vs <c>disconnect &lt;session-id&gt;</c>).
    /// </summary>
    public CliCommandId? CommandIdWithArgument { get; }

    /// <summary>
    /// Child nodes beneath this token.
    /// </summary>
    public IReadOnlyList<CliCommandNode> Children { get; }

    /// <summary>
    /// Argument specification when this node accepts a trailing argument; otherwise <see langword="null"/>.
    /// </summary>
    public CliArgumentSpec? Argument { get; }

    /// <summary>
    /// Alternate tokens that match this node (case-insensitive).
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Children"/> is non-empty.
    /// </summary>
    public bool HasChildren => Children.Count > 0;

    /// <summary>
    /// <see langword="true"/> when <see cref="Argument"/> is non-null.
    /// </summary>
    public bool AcceptsArgument => Argument is not null;

    /// <summary>
    /// Returns whether <paramref name="token"/> matches <see cref="Token"/> or any alias (ordinal ignore-case).
    /// </summary>
    /// <param name="token">Candidate token from operator input.</param>
    /// <returns><see langword="true"/> on match.</returns>
    public bool MatchesToken(string token) =>
        Token.Equals(token, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(a => a.Equals(token, StringComparison.OrdinalIgnoreCase));
}
