// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CopyUrlRedaction.cs
//
// Purpose:
// Extracts URL userinfo credentials for auth and redacts passwords from URIs and
// copy CLI lines so logs and history never retain cleartext secrets.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.RegularExpressions;

namespace TechConnect.Server.Copy;

/// <summary>
/// Credential extraction and redaction for copy URLs / CLI lines.
/// </summary>
/// <remarks>
/// Redaction replaces the password portion of <c>user:password@</c> with
/// <c>****</c> while leaving username-only userinfo unchanged.
/// Extracted passwords are for in-memory auth only and must not be logged.
/// </remarks>
internal static partial class CopyUrlRedaction
{
    // scheme://user:pass@host... — stop at first unencoded @ after ://
    /// <summary>
    /// Matches an absolute URI with optional userinfo before the first <c>@</c>.
    /// </summary>
    [GeneratedRegex(
        @"^(?<scheme>[a-zA-Z][a-zA-Z0-9+.-]*)://(?:(?<userinfo>[^@/?#]*)@)?(?<rest>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex UriWithOptionalUserInfo();

    /// <summary>
    /// Returns a display-safe URI with any password masked as <c>****</c>.
    /// </summary>
    /// <param name="uri">Raw URI, possibly containing userinfo credentials.</param>
    /// <returns>Trimmed URI with password redacted when present; empty when null/whitespace.</returns>
    public static string RedactUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return uri ?? string.Empty;
        }

        var match = UriWithOptionalUserInfo().Match(uri.Trim());
        if (!match.Success || !match.Groups["userinfo"].Success)
        {
            return uri.Trim();
        }

        var scheme = match.Groups["scheme"].Value;
        var userInfo = match.Groups["userinfo"].Value;
        var rest = match.Groups["rest"].Value;
        var colon = userInfo.IndexOf(':');
        if (colon < 0)
        {
            return $"{scheme}://{userInfo}@{rest}";
        }

        var user = userInfo[..colon];
        return $"{scheme}://{user}:****@{rest}";
    }

    /// <summary>Redacts credentials in a full CLI line beginning with <c>copy</c>.</summary>
    /// <param name="line">Full command line as typed.</param>
    /// <returns>
    /// The same line with URI tokens that contain userinfo password-masked;
    /// non-<c>copy</c> lines are returned unchanged.
    /// </returns>
    public static string SanitizeCommandLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !tokens[0].Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        var first = true;
        foreach (var token in tokens)
        {
            if (!first)
            {
                sb.Append(' ');
            }

            first = false;
            if (LooksLikeUriWithUserInfo(token))
            {
                sb.Append(RedactUri(token));
            }
            else
            {
                sb.Append(token);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Splits optional userinfo from a URI for authentication while producing a credential-free URI.
    /// </summary>
    /// <param name="uri">Raw absolute URI that may include <c>user:pass@</c>.</param>
    /// <param name="withoutUserInfo">URI with userinfo removed (<c>scheme://rest</c>).</param>
    /// <param name="username">Decoded username when userinfo was present.</param>
    /// <param name="password">
    /// Decoded password when an unencoded <c>:</c> delimiter was present; otherwise null.
    /// </param>
    /// <returns>True when userinfo was present and extracted.</returns>
    public static bool TryExtractUserInfo(
        string uri,
        out string withoutUserInfo,
        out string? username,
        out string? password)
    {
        username = null;
        password = null;
        withoutUserInfo = uri;
        var match = UriWithOptionalUserInfo().Match(uri.Trim());
        if (!match.Success || !match.Groups["userinfo"].Success)
        {
            return false;
        }

        var scheme = match.Groups["scheme"].Value;
        var userInfo = match.Groups["userinfo"].Value;
        var rest = match.Groups["rest"].Value;
        withoutUserInfo = $"{scheme}://{rest}";

        var colon = IndexOfUnencodedColon(userInfo);
        if (colon < 0)
        {
            username = Uri.UnescapeDataString(userInfo);
            return true;
        }

        username = Uri.UnescapeDataString(userInfo[..colon]);
        password = Uri.UnescapeDataString(userInfo[(colon + 1)..]);
        return true;
    }

    /// <summary>
    /// Heuristic: token looks like a URI that includes userinfo (<c>://</c> and <c>@</c>).
    /// </summary>
    /// <param name="token">CLI token.</param>
    /// <returns>True when redaction should be attempted.</returns>
    private static bool LooksLikeUriWithUserInfo(string token) =>
        token.Contains("://", StringComparison.Ordinal) &&
        token.Contains('@') &&
        UriWithOptionalUserInfo().IsMatch(token);

    /// <summary>
    /// Finds the first unencoded <c>:</c> in userinfo, skipping <c>%XX</c> escapes.
    /// </summary>
    /// <param name="userInfo">Raw userinfo segment.</param>
    /// <returns>Index of the password delimiter, or -1 when absent.</returns>
    private static int IndexOfUnencodedColon(string userInfo)
    {
        for (var i = 0; i < userInfo.Length; i++)
        {
            if (userInfo[i] == '%')
            {
                i += 2;
                continue;
            }

            if (userInfo[i] == ':')
            {
                return i;
            }
        }

        return -1;
    }
}
