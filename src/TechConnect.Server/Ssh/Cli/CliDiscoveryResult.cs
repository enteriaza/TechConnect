// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliDiscoveryResult.cs
//
// Purpose:
// Models Tab-completion and context-help (?) outcomes produced by CLI discovery
// for InteractiveLineReader to apply to the edit buffer.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Kind of discovery outcome returned to the interactive line reader.
/// </summary>
internal enum CliDiscoveryKind
{
    /// <summary>No completion or help available.</summary>
    None = 0,

    /// <summary>Exactly one completion candidate; replace the partial token.</summary>
    UniqueCompletion = 1,

    /// <summary>Multiple candidates; may extend a shared prefix and/or show a listing.</summary>
    AmbiguousCompletion = 2,

    /// <summary>Context help listing for <c>?</c> (not inserted into the buffer).</summary>
    ContextHelp = 3
}

/// <summary>
/// Result of Tab completion or <c>?</c> context help against the command catalog.
/// </summary>
internal sealed class CliDiscoveryResult
{
    /// <summary>
    /// Outcome kind.
    /// </summary>
    public CliDiscoveryKind Kind { get; init; }

    /// <summary>Token replacements for unique completion (canonical next token text).</summary>
    public string? CompletionText { get; init; }

    /// <summary>Whether to append a trailing space after unique completion.</summary>
    public bool AppendTrailingSpace { get; init; }

    /// <summary>Start index in the input line of the token being completed.</summary>
    public int ReplaceStart { get; init; }

    /// <summary>End index (exclusive) of the token being completed.</summary>
    public int ReplaceEnd { get; init; }

    /// <summary>
    /// Candidate tokens (completions or help tokens).
    /// </summary>
    public IReadOnlyList<string> Tokens { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Parallel descriptions for context-help listings.
    /// </summary>
    public IReadOnlyList<string> Descriptions { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Pre-formatted display text for ambiguous completion or context help.
    /// </summary>
    public string DisplayText { get; init; } = string.Empty;

    /// <summary>
    /// Shared empty result indicating no discovery action.
    /// </summary>
    public static CliDiscoveryResult None { get; } = new() { Kind = CliDiscoveryKind.None };
}
