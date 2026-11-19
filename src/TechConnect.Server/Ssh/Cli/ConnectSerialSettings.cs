// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectSerialSettings.cs
//
// Purpose:
// Parses and defaults connect serial baud/databits/parity/stopbits settings
// for CLI connect and discovery.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using TechConnect.Protocol;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Complete serial configuration for <c>connect</c>. Defaults are always 9600 8N1;
/// unspecified CLI parameters keep these defaults (never a prior operator's settings).
/// </summary>
internal sealed record ConnectSerialSettings(
    int BaudRate,
    int DataBits,
    string Parity,
    string StopBits,
    string Handshake = "None")
{
    /// <summary>Default baud rate (9600) when the operator omits <c>baud</c>.</summary>
    public const int DefaultBaudRate = 9600;

    /// <summary>Default data bits (8) when the operator omits <c>databits</c>.</summary>
    public const int DefaultDataBits = 8;

    /// <summary>Default parity label (<c>None</c>) when the operator omits <c>parity</c>.</summary>
    public const string DefaultParity = "None";

    /// <summary>Default stop-bits label (<c>One</c>) when the operator omits <c>stopbits</c>.</summary>
    public const string DefaultStopBits = "One";

    /// <summary>Default handshake (<c>None</c>); not currently exposed as a CLI keyword.</summary>
    public const string DefaultHandshake = "None";

    /// <summary>Canonical 9600 8N1 defaults used when parsing with no/partial parameters.</summary>
    public static ConnectSerialSettings Defaults { get; } = new(
        DefaultBaudRate,
        DefaultDataBits,
        DefaultParity,
        DefaultStopBits,
        DefaultHandshake);

    /// <summary>Optional <c>connect</c> serial keywords with help descriptions (catalog for discovery).</summary>
    public static IReadOnlyList<(string Keyword, string Description)> ParameterKeywords { get; } =
    [
        ("baud", "Serial baud rate"),
        ("databits", "Serial data bits"),
        ("parity", "Serial parity"),
        ("stopbits", "Serial stop bits")
    ];

    /// <summary>
    /// Baud rates from <see cref="ControlProtocol.SupportedBaudRates"/> as decimal strings for Tab.
    /// </summary>
    public static IReadOnlyList<string> SupportedBaudRateValues { get; } =
        ControlProtocol.SupportedBaudRates
            .Select(r => r.ToString(CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>Allowed <c>databits</c> values for discovery (5–8).</summary>
    public static IReadOnlyList<string> SupportedDataBitsValues { get; } = ["5", "6", "7", "8"];

    /// <summary>Allowed <c>parity</c> tokens for discovery (lowercase CLI forms).</summary>
    public static IReadOnlyList<string> SupportedParityValues { get; } =
        ["none", "odd", "even", "mark", "space"];

    /// <summary>Allowed <c>stopbits</c> tokens for discovery (<c>1</c>, <c>2</c>, <c>1.5</c>).</summary>
    public static IReadOnlyList<string> SupportedStopBitsValues { get; } = ["1", "2", "1.5"];

    /// <summary>
    /// Returns whether <paramref name="token"/> is a known connect serial parameter keyword.
    /// </summary>
    /// <param name="token">Candidate keyword (case-insensitive).</param>
    public static bool IsParameterKeyword(string token) =>
        ParameterKeywords.Any(k => k.Keyword.Equals(token, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Value suggestions for a trailing connect keyword (<c>baud</c>/<c>databits</c>/<c>parity</c>/<c>stopbits</c>).
    /// </summary>
    /// <param name="keyword">Parameter keyword (case-insensitive).</param>
    /// <returns>Supported values, or an empty list when the keyword is unknown.</returns>
    public static IReadOnlyList<string> GetValueSuggestions(string keyword)
    {
        if (keyword.Equals("baud", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedBaudRateValues;
        }

        if (keyword.Equals("databits", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedDataBitsValues;
        }

        if (keyword.Equals("parity", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedParityValues;
        }

        if (keyword.Equals("stopbits", StringComparison.OrdinalIgnoreCase))
        {
            return SupportedStopBitsValues;
        }

        return [];
    }

    /// <summary>
    /// Keywords already supplied as complete key/value pairs in trailing tokens after the session id.
    /// </summary>
    public static HashSet<string> GetSuppliedKeywords(IReadOnlyList<string> trailingTokens)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < trailingTokens.Count; i += 2)
        {
            seen.Add(trailingTokens[i]);
        }

        return seen;
    }

    /// <summary>
    /// Parses optional keyword parameters after the session id:
    /// <c>baud</c>, <c>databits</c>, <c>parity</c>, <c>stopbits</c>.
    /// </summary>
    public static bool TryParse(
        IReadOnlyList<string> parameterTokens,
        out ConnectSerialSettings settings,
        out string? error)
    {
        settings = Defaults;
        error = null;

        var baud = DefaultBaudRate;
        var dataBits = DefaultDataBits;
        var parity = DefaultParity;
        var stopBits = DefaultStopBits;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parameterTokens.Count; )
        {
            var key = parameterTokens[i];
            if (i + 1 >= parameterTokens.Count)
            {
                error = $"% Incomplete serial parameter '{key}'.";
                return false;
            }

            var value = parameterTokens[i + 1];
            i += 2;

            if (!seen.Add(key))
            {
                error = $"% Duplicate serial parameter '{key}'.";
                return false;
            }

            if (key.Equals("baud", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out baud) ||
                    !string.Equals(value, baud.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                {
                    error = "% Invalid baud rate.";
                    return false;
                }

                if (!ControlProtocol.IsSupportedBaudRate(baud))
                {
                    error =
                        $"% Unsupported baud rate. Supported: {string.Join(", ", ControlProtocol.SupportedBaudRates)}";
                    return false;
                }

                continue;
            }

            if (key.Equals("databits", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out dataBits) ||
                    !string.Equals(value, dataBits.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                    dataBits is < 5 or > 8)
                {
                    error = "% Invalid databits. Use an integer from 5 to 8.";
                    return false;
                }

                continue;
            }

            if (key.Equals("parity", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizeParity(value, out parity))
                {
                    error = "% Invalid parity. Use none, odd, even, mark, or space.";
                    return false;
                }

                continue;
            }

            if (key.Equals("stopbits", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryNormalizeStopBits(value, out stopBits))
                {
                    error = "% Invalid stopbits. Use 1, 2, or 1.5.";
                    return false;
                }

                continue;
            }

            error = $"% Unknown serial parameter '{key}'.";
            return false;
        }

        settings = new ConnectSerialSettings(baud, dataBits, parity, stopBits, DefaultHandshake);
        return true;
    }

    /// <summary>
    /// Maps CLI parity tokens (<c>none</c>/<c>n</c>, <c>odd</c>/<c>o</c>, …) to protocol labels.
    /// </summary>
    /// <param name="value">Operator-supplied parity token.</param>
    /// <param name="normalized">Pascal-case label (<c>None</c>, <c>Odd</c>, …) on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is recognized.</returns>
    private static bool TryNormalizeParity(string value, out string normalized)
    {
        normalized = DefaultParity;
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "None";
            return true;
        }

        if (value.Equals("odd", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("o", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Odd";
            return true;
        }

        if (value.Equals("even", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("e", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Even";
            return true;
        }

        if (value.Equals("mark", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("m", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Mark";
            return true;
        }

        if (value.Equals("space", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("s", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Space";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Maps CLI stop-bits tokens (<c>1</c>/<c>one</c>, <c>2</c>, <c>1.5</c>, …) to protocol labels.
    /// </summary>
    /// <param name="value">Operator-supplied stop-bits token.</param>
    /// <param name="normalized">Label (<c>One</c>, <c>Two</c>, <c>OnePointFive</c>) on success.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is recognized.</returns>
    private static bool TryNormalizeStopBits(string value, out string normalized)
    {
        normalized = DefaultStopBits;
        if (value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("one", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "One";
            return true;
        }

        if (value.Equals("2", StringComparison.Ordinal) ||
            value.Equals("two", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "Two";
            return true;
        }

        if (value.Equals("1.5", StringComparison.Ordinal) ||
            value.Equals("1.5", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("onepointfive", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("OnePointFive", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "OnePointFive";
            return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a compact summary such as <c>9600 8N1</c> for CLI confirmations.
    /// </summary>
    /// <returns>Baud, data bits, first parity letter, and stop-bits digit form.</returns>
    public string FormatSummary() =>
        $"{BaudRate} {DataBits}{Parity[0]}{StopBits switch { "One" => "1", "Two" => "2", "OnePointFive" => "1.5", _ => StopBits }}";
}
