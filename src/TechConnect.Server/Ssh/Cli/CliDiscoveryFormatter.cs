// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CliDiscoveryFormatter.cs
//
// Purpose:
// Formats CLI Tab-completion columns and context-help token/description
// listings for the current terminal width.
//
// -----------------------------------------------------------------------------
using System.Text;
using TechConnect.Server.Authorization;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Formats CLI discovery help entries and ambiguous-completion candidate columns.
/// </summary>
internal static class CliDiscoveryFormatter
{
    /// <summary>
    /// Fallback terminal width when the client reports a very small column count.
    /// </summary>
    public const int DefaultColumns = 80;

    /// <summary>
    /// Normalizes terminal columns for formatting (values under 20 become <see cref="DefaultColumns"/>).
    /// </summary>
    /// <param name="columns">Raw terminal columns.</param>
    /// <returns>Usable column width.</returns>
    public static int NormalizeColumns(int columns) =>
        columns < 20 ? DefaultColumns : columns;

    /// <summary>
    /// Formats a single token/description help row padded for the terminal width.
    /// </summary>
    /// <param name="token">Command or keyword token (may be empty for description-only rows).</param>
    /// <param name="description">Help description text.</param>
    /// <param name="terminalColumns">Current terminal width.</param>
    /// <param name="indent">Leading space count.</param>
    /// <returns>Single help line without trailing newline.</returns>
    public static string FormatHelpEntry(
        string token,
        string description,
        int terminalColumns,
        int indent = 2)
    {
        var width = NormalizeColumns(terminalColumns);
        var tokenCol = Math.Max(12, Math.Min(18, width / 4));
        var pad = new string(' ', indent);
        if (string.IsNullOrEmpty(token))
        {
            return pad + new string(' ', tokenCol) + "  " + description;
        }

        if (token.Length >= tokenCol)
        {
            return pad + token + "  " + description;
        }

        return pad + token.PadRight(tokenCol) + "  " + description;
    }

    /// <summary>
    /// Formats ambiguous completion candidates into multi-column text for the terminal width.
    /// </summary>
    /// <param name="tokens">Candidate tokens.</param>
    /// <param name="terminalColumns">Current terminal width.</param>
    /// <returns>CRLF-separated column layout, or empty when there are no tokens.</returns>
    public static string FormatCandidateColumns(IReadOnlyList<string> tokens, int terminalColumns)
    {
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        var ordered = CliDiscoveryOrdering.Sort(tokens);
        var width = NormalizeColumns(terminalColumns);
        var maxLen = ordered.Max(t => t.Length);
        var colWidth = Math.Max(maxLen + 2, 8);
        var cols = Math.Max(1, width / colWidth);
        var sb = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && i % cols == 0)
            {
                sb.Append("\r\n");
            }

            var cell = ordered[i].PadRight(colWidth);
            if (i % cols == cols - 1 || i == ordered.Count - 1)
            {
                sb.Append(ordered[i]);
            }
            else
            {
                sb.Append(cell);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats an ordered list of token/description pairs as context-help output.
    /// </summary>
    /// <param name="entries">Help entries.</param>
    /// <param name="terminalColumns">Current terminal width.</param>
    /// <returns>CRLF-joined help lines, or empty when there are no entries.</returns>
    public static string FormatHelpListing(
        IReadOnlyList<(string Token, string Description)> entries,
        int terminalColumns)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var ordered = CliDiscoveryOrdering.Sort(entries);
        var sb = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("\r\n");
            }

            sb.Append(FormatHelpEntry(ordered[i].Token, ordered[i].Description, terminalColumns));
        }

        return sb.ToString();
    }
}
