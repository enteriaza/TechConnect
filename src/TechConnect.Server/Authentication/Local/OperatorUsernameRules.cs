// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorUsernameRules.cs
//
// Purpose:
// Validates and normalizes local operator usernames used as account-store keys.
//
// -----------------------------------------------------------------------------

using System.Text.RegularExpressions;

namespace TechConnect.Server.Authentication.Local;

/// <summary>
/// Length, character-set, and key-normalization rules for local operator usernames.
/// </summary>
/// <remarks>
/// Validation is culture-invariant. Lookup keys use
/// <see cref="Comparer"/> (ordinal ignore-case) after <see cref="NormalizeKey"/>.
/// </remarks>
internal static partial class OperatorUsernameRules
{
    /// <summary>
    /// Minimum allowed username length in characters.
    /// </summary>
    public const int MinLength = 1;

    /// <summary>
    /// Maximum allowed username length in characters.
    /// </summary>
    public const int MaxLength = 64;

    /// <summary>
    /// Comparer used when treating usernames as dictionary keys (ordinal, case-insensitive).
    /// </summary>
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Returns whether <paramref name="username"/> meets length and character-set rules.
    /// </summary>
    /// <param name="username">Candidate username; <see langword="null"/> or whitespace fails.</param>
    /// <returns><see langword="true"/> when the username is non-empty, within length bounds, and matches the allowed pattern.</returns>
    public static bool IsValid(string? username) =>
        !string.IsNullOrWhiteSpace(username) &&
        username.Length is >= MinLength and <= MaxLength &&
        UsernamePattern().IsMatch(username);

    /// <summary>
    /// Trims leading and trailing whitespace for use as a store key.
    /// </summary>
    /// <param name="username">Username to normalize; caller should already have validated it when storing.</param>
    /// <returns>Trimmed username text.</returns>
    public static string NormalizeKey(string username) => username.Trim();

    /// <summary>
    /// Allowed username pattern: letters, digits, and <c>._@+-</c>.
    /// </summary>
    /// <returns>Compiled regex for username validation.</returns>
    [GeneratedRegex(@"^[A-Za-z0-9._@+\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();
}
