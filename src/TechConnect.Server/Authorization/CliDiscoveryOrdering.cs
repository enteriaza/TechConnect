// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliDiscoveryOrdering.cs
//
// Purpose:
// Orders CLI discovery / help listings for operator presentation.
//
// -----------------------------------------------------------------------------
namespace TechConnect.Server.Authorization;

/// <summary>
/// Deterministic presentation ordering for CLI help and completion candidates.
/// Case-insensitive primary key with ordinal tie-breaker; does not mutate catalogs.
/// </summary>
internal static class CliDiscoveryOrdering
{
    /// <summary>
    /// Comparer: <see cref="StringComparer.OrdinalIgnoreCase"/>, then
    /// <see cref="StringComparer.Ordinal"/> when ignore-case equal.
    /// </summary>
    public static IComparer<string> Comparer { get; } = new OrdinalIgnoreCaseThenOrdinalComparer();

    /// <summary>
    /// Returns tokens sorted with <see cref="Comparer"/> (stable presentation order).
    /// </summary>
    /// <param name="tokens">Tokens to sort; not mutated.</param>
    /// <returns>New array in discovery order.</returns>
    public static IReadOnlyList<string> Sort(IEnumerable<string> tokens) =>
        tokens.OrderBy(t => t, Comparer).ToArray();

    /// <summary>
    /// Returns token/description pairs sorted by token with <see cref="Comparer"/>.
    /// </summary>
    /// <param name="entries">Entries to sort; not mutated.</param>
    /// <returns>New array in discovery order.</returns>
    public static IReadOnlyList<(string Token, string Description)> Sort(
        IEnumerable<(string Token, string Description)> entries) =>
        entries.OrderBy(e => e.Token, Comparer).ToArray();

    /// <summary>
    /// Comparer implementation: ordinal-ignore-case, then ordinal for ties.
    /// </summary>
    private sealed class OrdinalIgnoreCaseThenOrdinalComparer : IComparer<string>
    {
        /// <summary>
        /// Compares two strings using ignore-case then ordinal tie-break.
        /// </summary>
        /// <param name="x">Left string.</param>
        /// <param name="y">Right string.</param>
        /// <returns>Signed comparison result.</returns>
        public int Compare(string? x, string? y)
        {
            var ignoreCase = StringComparer.OrdinalIgnoreCase.Compare(x, y);
            return ignoreCase != 0
                ? ignoreCase
                : StringComparer.Ordinal.Compare(x, y);
        }
    }
}
