// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliParseOutcome.cs
//
// Purpose:
// Structured parse status and result values produced when matching operator
// input against the CLI command catalog.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// Status of a CLI parse attempt against the command catalog.
/// </summary>
internal enum CliParseStatus
{
    /// <summary>Input does not match any catalog path.</summary>
    Invalid = 0,

    /// <summary>Input matches a prefix but is not yet a complete executable command.</summary>
    Incomplete = 1,

    /// <summary>Input fully resolved to a leaf command with a <see cref="CliCommandId"/>.</summary>
    Resolved = 2
}

/// <summary>
/// Immutable parse result carrying status, tokens, and optional resolved node/command.
/// </summary>
internal readonly struct CliParseResult
{
    /// <summary>
    /// Parse status for this result.
    /// </summary>
    public CliParseStatus Status { get; }

    /// <summary>
    /// Resolved command id when <see cref="Status"/> is <see cref="CliParseStatus.Resolved"/>; otherwise default.
    /// </summary>
    public CliCommandId CommandId { get; }

    /// <summary>
    /// Tokenized input used for the parse.
    /// </summary>
    public string[] Tokens { get; }

    /// <summary>
    /// Deepest matched catalog node when available (incomplete or resolved).
    /// </summary>
    public CliCommandNode? Node { get; }

    /// <summary>
    /// Creates a parse result with the given fields.
    /// </summary>
    private CliParseResult(
        CliParseStatus status,
        CliCommandId commandId,
        string[] tokens,
        CliCommandNode? node)
    {
        Status = status;
        CommandId = commandId;
        Tokens = tokens;
        Node = node;
    }

    /// <summary>
    /// Creates an invalid parse result for the given tokens.
    /// </summary>
    /// <param name="tokens">Tokenized input that failed to match.</param>
    /// <returns>Result with <see cref="CliParseStatus.Invalid"/>.</returns>
    public static CliParseResult Invalid(string[] tokens) =>
        new(CliParseStatus.Invalid, default, tokens, null);

    /// <summary>
    /// Creates an incomplete parse result, optionally retaining the deepest matched node.
    /// </summary>
    /// <param name="tokens">Tokenized input.</param>
    /// <param name="node">Optional deepest matched node for discovery/help.</param>
    /// <returns>Result with <see cref="CliParseStatus.Incomplete"/>.</returns>
    public static CliParseResult Incomplete(string[] tokens, CliCommandNode? node = null) =>
        new(CliParseStatus.Incomplete, default, tokens, node);

    /// <summary>
    /// Creates a resolved parse result for an executable catalog leaf.
    /// </summary>
    /// <param name="commandId">Resolved command identity.</param>
    /// <param name="tokens">Tokenized input.</param>
    /// <param name="node">Leaf catalog node that matched.</param>
    /// <returns>Result with <see cref="CliParseStatus.Resolved"/>.</returns>
    public static CliParseResult Resolved(CliCommandId commandId, string[] tokens, CliCommandNode node) =>
        new(CliParseStatus.Resolved, commandId, tokens, node);
}
