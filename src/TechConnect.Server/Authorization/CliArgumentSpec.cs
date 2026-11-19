// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliArgumentSpec.cs
//
// Purpose:
// Describes a single CLI argument specification attached to a catalog node
// (display name, kind, optionality, trailing-token policy).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// Specification for an argument accepted by a <see cref="CliCommandNode"/>.
/// </summary>
internal sealed class CliArgumentSpec
{
    /// <summary>
    /// Creates an argument specification.
    /// </summary>
    /// <param name="displayName">Short name shown in help/discovery.</param>
    /// <param name="description">Longer description for help text.</param>
    /// <param name="kind">Semantic kind used by handlers after parse.</param>
    /// <param name="optional">When <see langword="true"/>, the command may resolve without this argument.</param>
    /// <param name="allowTrailingTokens">When <see langword="true"/>, tokens after the primary argument are retained.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="displayName"/> or <paramref name="description"/> is <see langword="null"/>.
    /// </exception>
    public CliArgumentSpec(
        string displayName,
        string description,
        CliArgumentKind kind,
        bool optional = false,
        bool allowTrailingTokens = false)
    {
        DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        Kind = kind;
        Optional = optional;
        AllowTrailingTokens = allowTrailingTokens;
    }

    /// <summary>
    /// Short display name for help (for example <c>session-id</c>).
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Human-readable description for help/discovery.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Semantic argument kind.
    /// </summary>
    public CliArgumentKind Kind { get; }

    /// <summary>When true, the command resolves without this argument (caller applies defaults).</summary>
    public bool Optional { get; }

    /// <summary>
    /// When true, tokens after the primary argument are retained for the handler
    /// (e.g. optional <c>connect</c> serial keyword parameters).
    /// </summary>
    public bool AllowTrailingTokens { get; }
}
