// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CopySourceParser.cs
//
// Purpose:
// Parses copy source and serial: destination tokens into CopySourceUri / session
// ids, including scheme-only stubs that need interactive completion.
//
// -----------------------------------------------------------------------------

using System.Globalization;

namespace TechConnect.Server.Copy;

/// <summary>
/// Parses <c>copy</c> source tokens into <see cref="CopySourceUri"/> values.
/// Incomplete scheme-only forms return a stub that needs interactive completion.
/// </summary>
/// <remarks>
/// Credentials present in URL userinfo are extracted into
/// <see cref="CopySourceUri.Username"/> / <see cref="CopySourceUri.Password"/> and
/// omitted from <see cref="CopySourceUri.DisplayUri"/>. Modem schemes
/// (<c>xmodem:</c> / <c>ymodem:</c>) never use network URLs.
/// </remarks>
internal static class CopySourceParser
{
    /// <summary>
    /// Returns true when <paramref name="token"/> is a bare scheme ending in <c>:</c>
    /// (for example <c>http:</c>) and maps it to a <see cref="CopySourceKind"/>.
    /// </summary>
    /// <param name="token">Raw CLI token.</param>
    /// <param name="kind">Mapped scheme when the method returns true.</param>
    /// <returns>True if the token is a recognized scheme-only form.</returns>
    public static bool TryParseSchemeOnly(string token, out CopySourceKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var t = token.Trim();
        if (!t.EndsWith(':'))
        {
            return false;
        }

        return TryMapScheme(t.TrimEnd(':'), out kind);
    }

    /// <summary>
    /// Parses a full or partial copy source token.
    /// </summary>
    /// <param name="token">Source token from the CLI.</param>
    /// <param name="source">Parsed source when successful.</param>
    /// <param name="error">Operator-facing error when parse fails.</param>
    /// <param name="needsInteractiveCompletion">
    /// True when the scheme is known but host/path (or SCP path) must be prompted.
    /// Modem scheme-only forms set this to false.
    /// </param>
    /// <returns>True when a source object was produced (even if incomplete).</returns>
    public static bool TryParse(
        string token,
        out CopySourceUri? source,
        out string? error,
        out bool needsInteractiveCompletion)
    {
        source = null;
        error = null;
        needsInteractiveCompletion = false;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "% Missing copy source.";
            return false;
        }

        var trimmed = token.Trim();
        if (TryParseModem(trimmed, out source))
        {
            return true;
        }

        if (TryParseSchemeOnly(trimmed, out var schemeOnly))
        {
            if (schemeOnly is CopySourceKind.Xmodem or CopySourceKind.Ymodem)
            {
                source = new CopySourceUri
                {
                    Kind = schemeOnly,
                    DisplayUri = schemeOnly == CopySourceKind.Xmodem ? "xmodem:" : "ymodem:"
                };
                return true;
            }

            needsInteractiveCompletion = true;
            source = new CopySourceUri
            {
                Kind = schemeOnly,
                DisplayUri = trimmed.ToLowerInvariant()
            };
            return true;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            error = "% Invalid copy source. Use http:/https:/tftp:/scp:/xmodem:/ymodem: or a full URL.";
            return false;
        }

        CopyUrlRedaction.TryExtractUserInfo(trimmed, out var safeUri, out var username, out var password);

        if (!Uri.TryCreate(safeUri, UriKind.Absolute, out var uri))
        {
            error = "% Invalid copy source URL.";
            return false;
        }

        if (!TryMapScheme(uri.Scheme, out var kind))
        {
            error = "% Unsupported copy source scheme.";
            return false;
        }

        if (kind is CopySourceKind.Xmodem or CopySourceKind.Ymodem)
        {
            error = "% XMODEM/YMODEM do not use network URLs. Use xmodem: or ymodem:.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "% Copy source URL requires a host.";
            return false;
        }

        var path = uri.AbsolutePath;
        if (uri.Scheme.Equals("scp", StringComparison.OrdinalIgnoreCase))
        {
            // scp://user@host/path — AbsolutePath includes leading /
            path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            if (string.IsNullOrWhiteSpace(path))
            {
                needsInteractiveCompletion = true;
            }
        }
        else if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            needsInteractiveCompletion = true;
            path = "/";
        }

        var port = uri.IsDefaultPort ? (int?)null : uri.Port;
        source = new CopySourceUri
        {
            Kind = kind,
            DisplayUri = BuildDisplayUri(kind, uri.Host, port, path, username),
            Host = uri.Host,
            Port = port,
            Path = path,
            Username = username ?? (string.IsNullOrEmpty(uri.UserInfo) ? null : uri.UserInfo),
            Password = password
        };
        return true;
    }

    /// <summary>
    /// Builds a completed HTTP/HTTPS/TFTP/SCP source after interactive host/filename prompts.
    /// </summary>
    /// <param name="kind">Network source kind.</param>
    /// <param name="host">Remote host.</param>
    /// <param name="filename">Remote path or filename.</param>
    /// <param name="username">Optional username (required later for SCP).</param>
    /// <param name="password">Optional password; never placed in <see cref="CopySourceUri.DisplayUri"/>.</param>
    /// <param name="port">Optional non-default port.</param>
    /// <returns>A fully populated <see cref="CopySourceUri"/>.</returns>
    public static CopySourceUri CompleteHttpLike(
        CopySourceKind kind,
        string host,
        string filename,
        string? username,
        string? password,
        int? port = null)
    {
        var path = filename.StartsWith('/') ? filename : "/" + filename.TrimStart('/');
        if (kind == CopySourceKind.Scp)
        {
            path = filename.TrimStart('/');
        }

        return new CopySourceUri
        {
            Kind = kind,
            DisplayUri = BuildDisplayUri(kind, host, port, path, username),
            Host = host,
            Port = port,
            Path = path,
            Username = username,
            Password = password
        };
    }

    /// <summary>
    /// Parses a destination token of the form <c>serial:&lt;session-id&gt;</c>.
    /// </summary>
    /// <param name="token">Destination token.</param>
    /// <param name="sessionId">Extracted session id on success.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns>True when a non-empty session id was parsed.</returns>
    public static bool TryParseDestination(string token, out string sessionId, out string? error)
    {
        sessionId = string.Empty;
        error = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "% Missing destination. Use serial:<session-id>.";
            return false;
        }

        const string prefix = "serial:";
        if (!token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "% Destination must be serial:<session-id>.";
            return false;
        }

        sessionId = token[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            error = "% Incomplete destination. Use serial:<session-id>.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses modem tokens with or without a trailing colon.
    /// </summary>
    /// <param name="token">Trimmed source token.</param>
    /// <param name="source">Modem <see cref="CopySourceUri"/> when recognized.</param>
    /// <returns>True when the token is xmodem or ymodem.</returns>
    private static bool TryParseModem(string token, out CopySourceUri? source)
    {
        source = null;
        if (token.Equals("xmodem:", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("xmodem", StringComparison.OrdinalIgnoreCase))
        {
            source = new CopySourceUri { Kind = CopySourceKind.Xmodem, DisplayUri = "xmodem:" };
            return true;
        }

        if (token.Equals("ymodem:", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("ymodem", StringComparison.OrdinalIgnoreCase))
        {
            source = new CopySourceUri { Kind = CopySourceKind.Ymodem, DisplayUri = "ymodem:" };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Maps a scheme name string to <see cref="CopySourceKind"/>.
    /// </summary>
    /// <param name="scheme">Scheme without trailing colon.</param>
    /// <param name="kind">Mapped kind on success.</param>
    /// <returns>True when the scheme is supported.</returns>
    private static bool TryMapScheme(string scheme, out CopySourceKind kind)
    {
        kind = default;
        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Http;
            return true;
        }

        if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Https;
            return true;
        }

        if (scheme.Equals("tftp", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Tftp;
            return true;
        }

        if (scheme.Equals("scp", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Scp;
            return true;
        }

        if (scheme.Equals("xmodem", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Xmodem;
            return true;
        }

        if (scheme.Equals("ymodem", StringComparison.OrdinalIgnoreCase))
        {
            kind = CopySourceKind.Ymodem;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a credential-free display URI, optionally including username (never password).
    /// </summary>
    /// <param name="kind">Source kind.</param>
    /// <param name="host">Host name or address.</param>
    /// <param name="port">Optional non-default port.</param>
    /// <param name="path">Remote path.</param>
    /// <param name="username">Optional username for display.</param>
    /// <returns>Display/log-safe absolute-looking URI string.</returns>
    private static string BuildDisplayUri(
        CopySourceKind kind,
        string host,
        int? port,
        string path,
        string? username)
    {
        var scheme = kind switch
        {
            CopySourceKind.Http => "http",
            CopySourceKind.Https => "https",
            CopySourceKind.Tftp => "tftp",
            CopySourceKind.Scp => "scp",
            _ => kind.ToString().ToLowerInvariant()
        };

        var userPart = string.IsNullOrEmpty(username) ? string.Empty : username + "@";
        var portPart = port is null
            ? string.Empty
            : ":" + port.Value.ToString(CultureInfo.InvariantCulture);
        var pathPart = kind == CopySourceKind.Scp
            ? "/" + path.TrimStart('/')
            : (path.StartsWith('/') ? path : "/" + path);
        return $"{scheme}://{userPart}{host}{portPart}{pathPart}";
    }
}
