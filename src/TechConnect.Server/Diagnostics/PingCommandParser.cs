// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingCommandParser.cs
//
// Purpose:
// Parses ping CLI tokens into PingRequest values, including session form,
// destination literals/hostnames, and sweep/size/source/df options.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Parses <c>ping</c> trailing tokens (after the catalog matches <c>ping</c>).
/// </summary>
/// <remarks>
/// Destination address-family selection for hostnames is deferred to
/// <see cref="IDestinationResolver"/> at execution time. Literal <c>ipv6</c>
/// as the second token is rejected (use a destination literal/hostname instead).
/// Session form allows only <c>repeat</c> and <c>timeout</c>.
/// </remarks>
internal static class PingCommandParser
{
    /// <summary>
    /// Attempts to parse <paramref name="tokens"/> into a <see cref="PingRequest"/>.
    /// </summary>
    /// <param name="tokens">Tokenized command including leading <c>ping</c>.</param>
    /// <param name="request">Parsed request when the method returns <see langword="true"/>.</param>
    /// <param name="error">Operator-facing error when parsing or validation fails.</param>
    /// <returns><see langword="true"/> when a validated request was produced.</returns>
    public static bool TryParse(
        IReadOnlyList<string> tokens,
        out PingRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (tokens.Count < 2)
        {
            error = "% Usage: ping <destination> | ping session <session-id>";
            return false;
        }

        if (tokens[1].Equals("session", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseSession(tokens, out request, out error);
        }

        if (tokens[1].Equals("ipv6", StringComparison.OrdinalIgnoreCase))
        {
            error = "% Unknown ping parameter 'ipv6'. Use: ping <destination> (IPv4, IPv6, or hostname).";
            return false;
        }

        return TryParseDestination(tokens, out request, out error);
    }

    /// <summary>Parses <c>ping &lt;destination&gt; …</c> including ICMP options.</summary>
    /// <param name="tokens">Full token list.</param>
    /// <param name="request">Parsed request on success.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns><see langword="true"/> when parsing and validation succeed.</returns>
    private static bool TryParseDestination(
        IReadOnlyList<string> tokens,
        out PingRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        var destinationInput = tokens[1];
        PingTargetKind kind;
        IPAddress? destination;

        if (IPAddress.TryParse(destinationInput, out var literal))
        {
            if (literal.AddressFamily == AddressFamily.InterNetwork)
            {
                kind = PingTargetKind.Ipv4;
                destination = literal;
            }
            else if (literal.AddressFamily == AddressFamily.InterNetworkV6)
            {
                kind = PingTargetKind.Ipv6;
                destination = literal;
            }
            else
            {
                error = "% Unsupported destination address family.";
                return false;
            }
        }
        else
        {
            kind = PingTargetKind.Hostname;
            destination = null;
        }

        var options = new OptionState();
        if (!TryParseOptions(
                tokens,
                startIndex: 2,
                allowDf: true,
                allowSize: true,
                allowSource: true,
                allowSweep: true,
                options,
                out error))
        {
            return false;
        }

        if (kind == PingTargetKind.Ipv4 &&
            options.Source is not null &&
            options.Source.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "% Source address must be IPv4 for an IPv4 destination.";
            return false;
        }

        if (kind == PingTargetKind.Ipv6)
        {
            if (options.DontFragment)
            {
                error = "% Don't Fragment (df) is not supported for IPv6.";
                return false;
            }

            if (options.Source is not null &&
                options.Source.AddressFamily != AddressFamily.InterNetworkV6)
            {
                error = "% Source address must be IPv6 for an IPv6 destination.";
                return false;
            }
        }

        // Hostname: source family validated after resolution.
        request = BuildRequest(kind, destinationInput, destination, sessionId: null, options);
        return ValidateRequest(request, out error);
    }

    /// <summary>Parses <c>ping session &lt;session-id&gt; …</c> (repeat/timeout only).</summary>
    /// <param name="tokens">Full token list.</param>
    /// <param name="request">Parsed request on success.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns><see langword="true"/> when parsing and validation succeed.</returns>
    private static bool TryParseSession(
        IReadOnlyList<string> tokens,
        out PingRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (tokens.Count < 3)
        {
            error = "% Usage: ping session <session-id> {repeat <count>} {timeout <seconds>}";
            return false;
        }

        var sessionId = tokens[2];
        var options = new OptionState();
        if (!TryParseOptions(
                tokens,
                startIndex: 3,
                allowDf: false,
                allowSize: false,
                allowSource: false,
                allowSweep: false,
                options,
                out error))
        {
            return false;
        }

        request = BuildRequest(PingTargetKind.Session, destinationInput: null, destination: null, sessionId, options);
        return ValidateRequest(request, out error);
    }

    /// <summary>
    /// Parses optional ping keywords from <paramref name="startIndex"/> into <paramref name="options"/>.
    /// </summary>
    /// <param name="tokens">Full token list.</param>
    /// <param name="startIndex">First option token index.</param>
    /// <param name="allowDf">Whether <c>df</c> is permitted for this form.</param>
    /// <param name="allowSize">Whether <c>size</c> is permitted.</param>
    /// <param name="allowSource">Whether <c>source</c> is permitted.</param>
    /// <param name="allowSweep">Whether <c>sweep</c> is permitted.</param>
    /// <param name="options">Mutable option accumulator.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns><see langword="true"/> when all options parse successfully.</returns>
    private static bool TryParseOptions(
        IReadOnlyList<string> tokens,
        int startIndex,
        bool allowDf,
        bool allowSize,
        bool allowSource,
        bool allowSweep,
        OptionState options,
        out string? error)
    {
        error = null;
        var i = startIndex;
        while (i < tokens.Count)
        {
            var key = tokens[i];
            if (key.Equals("repeat", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadInt(tokens, ref i, "repeat", out var value, out error) ||
                    value is < PingLimits.MinRepeat or > PingLimits.MaxRepeat)
                {
                    error ??=
                        $"% Invalid repeat. Use an integer {PingLimits.MinRepeat}-{PingLimits.MaxRepeat}.";
                    return false;
                }

                options.Repeat = value;
                continue;
            }

            if (key.Equals("timeout", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadInt(tokens, ref i, "timeout", out var value, out error) ||
                    value is < PingLimits.MinTimeoutSeconds or > PingLimits.MaxTimeoutSeconds)
                {
                    error ??=
                        $"% Invalid timeout. Use an integer {PingLimits.MinTimeoutSeconds}-{PingLimits.MaxTimeoutSeconds} seconds.";
                    return false;
                }

                options.TimeoutSeconds = value;
                continue;
            }

            if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowSize)
                {
                    error = "% Parameter 'size' is not valid for this ping form.";
                    return false;
                }

                if (!TryReadInt(tokens, ref i, "size", out var value, out error) ||
                    value is < PingLimits.MinPayloadBytes or > PingLimits.MaxPayloadBytes)
                {
                    error ??=
                        $"% Invalid size. Use an integer {PingLimits.MinPayloadBytes}-{PingLimits.MaxPayloadBytes} bytes.";
                    return false;
                }

                options.PayloadBytes = value;
                continue;
            }

            if (key.Equals("source", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowSource)
                {
                    error = "% Parameter 'source' is not valid for this ping form.";
                    return false;
                }

                if (i + 1 >= tokens.Count)
                {
                    error = "% Incomplete parameter 'source'.";
                    return false;
                }

                i++;
                if (!IPAddress.TryParse(tokens[i], out var source))
                {
                    error = "% Invalid source address.";
                    return false;
                }

                options.Source = source;
                i++;
                continue;
            }

            if (key.Equals("df", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowDf)
                {
                    error = "% Don't Fragment (df) is only valid for IPv4 ping.";
                    return false;
                }

                options.DontFragment = true;
                i++;
                continue;
            }

            if (key.Equals("sweep", StringComparison.OrdinalIgnoreCase))
            {
                if (!allowSweep)
                {
                    error = "% Parameter 'sweep' is not valid for this ping form.";
                    return false;
                }

                options.Sweep = true;
                i++;
                while (i < tokens.Count)
                {
                    var sweepKey = tokens[i];
                    if (sweepKey.Equals("min", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryReadInt(tokens, ref i, "min", out var value, out error))
                        {
                            return false;
                        }

                        options.SweepMinBytes = value;
                        continue;
                    }

                    if (sweepKey.Equals("max", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryReadInt(tokens, ref i, "max", out var value, out error))
                        {
                            return false;
                        }

                        options.SweepMaxBytes = value;
                        continue;
                    }

                    if (sweepKey.Equals("interval", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryReadInt(tokens, ref i, "interval", out var value, out error))
                        {
                            return false;
                        }

                        options.SweepIntervalBytes = value;
                        continue;
                    }

                    break;
                }

                continue;
            }

            error = $"% Unknown ping parameter '{key}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the next token as an invariant integer matching its decimal string exactly.
    /// </summary>
    /// <param name="tokens">Token list.</param>
    /// <param name="index">Current key index; advanced past the value on success.</param>
    /// <param name="name">Parameter name for error messages.</param>
    /// <param name="value">Parsed integer.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns><see langword="true"/> when a canonical integer token was consumed.</returns>
    private static bool TryReadInt(
        IReadOnlyList<string> tokens,
        ref int index,
        string name,
        out int value,
        out string? error)
    {
        value = 0;
        error = null;
        if (index + 1 >= tokens.Count)
        {
            error = $"% Incomplete parameter '{name}'.";
            return false;
        }

        index++;
        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
            !string.Equals(tokens[index], value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            error = $"% Invalid {name} value.";
            return false;
        }

        index++;
        return true;
    }

    /// <summary>Builds a <see cref="PingRequest"/> from parsed kind and options.</summary>
    /// <param name="kind">Target kind.</param>
    /// <param name="destinationInput">Original destination text, or null for session.</param>
    /// <param name="destination">Literal address when known.</param>
    /// <param name="sessionId">Session id for session form.</param>
    /// <param name="options">Parsed option values with defaults applied.</param>
    /// <returns>Immutable request (not yet sweep-validated).</returns>
    private static PingRequest BuildRequest(
        PingTargetKind kind,
        string? destinationInput,
        IPAddress? destination,
        string? sessionId,
        OptionState options) =>
        new()
        {
            Kind = kind,
            DestinationInput = destinationInput,
            Destination = destination,
            SessionId = sessionId,
            Repeat = options.Repeat,
            TimeoutSeconds = options.TimeoutSeconds,
            PayloadBytes = options.PayloadBytes,
            Source = options.Source,
            DontFragment = options.DontFragment,
            Sweep = options.Sweep,
            SweepMinBytes = options.SweepMinBytes,
            SweepMaxBytes = options.SweepMaxBytes,
            SweepIntervalBytes = options.SweepIntervalBytes
        };

    /// <summary>
    /// Validates sweep range and total probe count against <see cref="PingLimits"/>.
    /// </summary>
    /// <param name="request">Request to validate.</param>
    /// <param name="error">Operator-facing error when invalid.</param>
    /// <returns><see langword="true"/> when valid (non-sweep requests always pass here).</returns>
    private static bool ValidateRequest(PingRequest request, out string? error)
    {
        error = null;
        if (request.Sweep)
        {
            if (request.SweepMinBytes <= 0 ||
                request.SweepIntervalBytes <= 0 ||
                request.SweepMaxBytes < request.SweepMinBytes)
            {
                error = "% Invalid sweep range. Require min > 0, interval > 0, and max >= min.";
                return false;
            }

            if (request.SweepMaxBytes > PingLimits.MaxPayloadBytes)
            {
                error =
                    $"% Sweep maximum exceeds supported payload limit ({PingLimits.MaxPayloadBytes} bytes).";
                return false;
            }

            var steps = ((request.SweepMaxBytes - request.SweepMinBytes) / request.SweepIntervalBytes) + 1;
            var total = steps * request.Repeat;
            if (total > PingLimits.MaxSweepProbes)
            {
                error =
                    $"% Sweep would send {total} probes; maximum allowed is {PingLimits.MaxSweepProbes}.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Mutable accumulator for ping option keywords during parse.</summary>
    private sealed class OptionState
    {
        /// <summary>Probe repeat count.</summary>
        public int Repeat { get; set; } = PingLimits.DefaultRepeat;

        /// <summary>Per-probe timeout in seconds.</summary>
        public int TimeoutSeconds { get; set; } = PingLimits.DefaultTimeoutSeconds;

        /// <summary>Fixed ICMP payload size.</summary>
        public int PayloadBytes { get; set; } = PingLimits.DefaultPayloadBytes;

        /// <summary>Optional local source bind address.</summary>
        public IPAddress? Source { get; set; }

        /// <summary>Don't Fragment flag (IPv4 only).</summary>
        public bool DontFragment { get; set; }

        /// <summary>Whether sweep mode is enabled.</summary>
        public bool Sweep { get; set; }

        /// <summary>Sweep minimum payload.</summary>
        public int SweepMinBytes { get; set; } = PingLimits.DefaultSweepMinBytes;

        /// <summary>Sweep maximum payload.</summary>
        public int SweepMaxBytes { get; set; } = PingLimits.DefaultSweepMaxBytes;

        /// <summary>Sweep step size.</summary>
        public int SweepIntervalBytes { get; set; } = PingLimits.DefaultSweepIntervalBytes;
    }
}
