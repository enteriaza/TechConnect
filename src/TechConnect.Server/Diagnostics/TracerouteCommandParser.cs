// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteCommandParser.cs
//
// Purpose:
// Parses traceroute CLI tokens into TracerouteRequest values with validated
// timeout/probe/ttl/source/size/port bounds and duration/port progression checks.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Parses <c>traceroute</c> tokens into a validated <see cref="TracerouteRequest"/>.
/// </summary>
/// <remarks>
/// Address-family selection is deferred to <see cref="IDestinationResolver"/> at
/// execution time for hostnames; hostnames temporarily use
/// <see cref="TracerouteAddressFamily.Ipv4"/> with <see cref="TracerouteRequest.RequiresResolution"/> set.
/// Literal <c>ipv6</c> as the second token is rejected. Port progression and
/// estimated duration are validated even though the ICMP Echo client ignores port.
/// </remarks>
internal static class TracerouteCommandParser
{
    /// <summary>
    /// Attempts to parse <paramref name="tokens"/> into a <see cref="TracerouteRequest"/>.
    /// </summary>
    /// <param name="tokens">Tokenized command including leading <c>traceroute</c>.</param>
    /// <param name="request">Parsed request when the method returns <see langword="true"/>.</param>
    /// <param name="error">Operator-facing error when parsing or validation fails.</param>
    /// <returns><see langword="true"/> when a validated request was produced.</returns>
    public static bool TryParse(
        IReadOnlyList<string> tokens,
        out TracerouteRequest? request,
        out string? error)
    {
        request = null;
        error = null;

        if (tokens.Count < 2)
        {
            error = "% Usage: traceroute <destination>";
            return false;
        }

        if (tokens[1].Equals("ipv6", StringComparison.OrdinalIgnoreCase))
        {
            error =
                "% Unknown traceroute parameter 'ipv6'. Use: traceroute <destination> (IPv4, IPv6, or hostname).";
            return false;
        }

        var destinationInput = tokens[1];
        TracerouteAddressFamily? family = null;
        IPAddress? destination = null;

        if (IPAddress.TryParse(destinationInput, out var literal))
        {
            if (literal.AddressFamily == AddressFamily.InterNetwork)
            {
                family = TracerouteAddressFamily.Ipv4;
                destination = literal;
            }
            else if (literal.AddressFamily == AddressFamily.InterNetworkV6)
            {
                family = TracerouteAddressFamily.Ipv6;
                destination = literal;
            }
            else
            {
                error = "% Unsupported destination address family.";
                return false;
            }
        }

        var options = new OptionState();
        if (!TryParseOptions(tokens, startIndex: 2, options, out error))
        {
            return false;
        }

        if (family == TracerouteAddressFamily.Ipv4 &&
            options.Source is not null &&
            options.Source.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "% Source address must be IPv4 for an IPv4 destination.";
            return false;
        }

        if (family == TracerouteAddressFamily.Ipv6 &&
            options.Source is not null &&
            options.Source.AddressFamily != AddressFamily.InterNetworkV6)
        {
            error = "% Source address must be IPv6 for an IPv6 destination.";
            return false;
        }

        request = BuildRequest(
            family ?? TracerouteAddressFamily.Ipv4,
            destinationInput,
            destination ?? IPAddress.None,
            requiresResolution: destination is null,
            options);
        return ValidateRequest(request, out error);
    }

    /// <summary>Parses timeout/probe/ttl/source/size/port keywords from <paramref name="startIndex"/>.</summary>
    /// <param name="tokens">Full token list.</param>
    /// <param name="startIndex">First option token index.</param>
    /// <param name="options">Mutable option accumulator.</param>
    /// <param name="error">Operator-facing error on failure.</param>
    /// <returns><see langword="true"/> when all options parse successfully.</returns>
    private static bool TryParseOptions(
        IReadOnlyList<string> tokens,
        int startIndex,
        OptionState options,
        out string? error)
    {
        error = null;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var key = tokens[i];
            if (IsValueKeyword(key))
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "% Incomplete traceroute parameter: " + key;
                    return false;
                }

                var value = tokens[++i];
                if (!TryApplyValue(key, value, options, out error))
                {
                    return false;
                }

                continue;
            }

            error = "% Unknown traceroute parameter: " + key;
            return false;
        }

        return true;
    }

    /// <summary>Returns whether <paramref name="key"/> is a known value-taking option keyword.</summary>
    /// <param name="key">Token to test.</param>
    /// <returns><see langword="true"/> for timeout/probe/ttl/source/size/port.</returns>
    private static bool IsValueKeyword(string key) =>
        key.Equals("timeout", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("probe", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("ttl", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("source", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("size", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("port", StringComparison.OrdinalIgnoreCase);

    /// <summary>Applies one keyword/value pair to <paramref name="options"/>.</summary>
    /// <param name="key">Option keyword.</param>
    /// <param name="value">Option value token.</param>
    /// <param name="options">Mutable accumulator.</param>
    /// <param name="error">Operator-facing error on failure (duplicate/invalid).</param>
    /// <returns><see langword="true"/> when applied.</returns>
    private static bool TryApplyValue(
        string key,
        string value,
        OptionState options,
        out string? error)
    {
        error = null;
        if (key.Equals("timeout", StringComparison.OrdinalIgnoreCase))
        {
            if (options.TimeoutSeconds is not null)
            {
                error = "% Duplicate traceroute parameter: timeout";
                return false;
            }

            if (!TryParsePositiveInt(value, out var seconds))
            {
                error = "% Invalid timeout value.";
                return false;
            }

            options.TimeoutSeconds = seconds;
            return true;
        }

        if (key.Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            if (options.ProbesPerHop is not null)
            {
                error = "% Duplicate traceroute parameter: probe";
                return false;
            }

            if (!TryParsePositiveInt(value, out var probes))
            {
                error = "% Invalid probe count.";
                return false;
            }

            options.ProbesPerHop = probes;
            return true;
        }

        if (key.Equals("ttl", StringComparison.OrdinalIgnoreCase))
        {
            if (options.MaxTtl is not null)
            {
                error = "% Duplicate traceroute parameter: ttl";
                return false;
            }

            if (!TryParsePositiveInt(value, out var ttl))
            {
                error = "% Invalid ttl value.";
                return false;
            }

            options.MaxTtl = ttl;
            return true;
        }

        if (key.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            if (options.Source is not null)
            {
                error = "% Duplicate traceroute parameter: source";
                return false;
            }

            if (!IPAddress.TryParse(value, out var source))
            {
                error = "% Invalid source address.";
                return false;
            }

            options.Source = source;
            return true;
        }

        if (key.Equals("size", StringComparison.OrdinalIgnoreCase))
        {
            if (options.PayloadBytes is not null)
            {
                error = "% Duplicate traceroute parameter: size";
                return false;
            }

            if (!TryParseNonNegativeInt(value, out var size))
            {
                error = "% Invalid size value.";
                return false;
            }

            options.PayloadBytes = size;
            return true;
        }

        if (key.Equals("port", StringComparison.OrdinalIgnoreCase))
        {
            if (options.InitialPort is not null)
            {
                error = "% Duplicate traceroute parameter: port";
                return false;
            }

            if (!TryParsePositiveInt(value, out var port))
            {
                error = "% Invalid port value.";
                return false;
            }

            options.InitialPort = port;
            return true;
        }

        error = "% Unknown traceroute parameter: " + key;
        return false;
    }

    /// <summary>Builds a request applying defaults for omitted options.</summary>
    /// <param name="family">Selected or placeholder family.</param>
    /// <param name="destinationInput">Original destination text.</param>
    /// <param name="destination">Literal or placeholder address.</param>
    /// <param name="requiresResolution">Whether DNS resolution is still required.</param>
    /// <param name="options">Parsed options.</param>
    /// <returns>Immutable request (not yet fully validated).</returns>
    private static TracerouteRequest BuildRequest(
        TracerouteAddressFamily family,
        string destinationInput,
        IPAddress destination,
        bool requiresResolution,
        OptionState options) =>
        new()
        {
            Family = family,
            DestinationInput = destinationInput,
            Destination = destination,
            RequiresResolution = requiresResolution,
            Source = options.Source,
            TimeoutSeconds = options.TimeoutSeconds ?? TracerouteLimits.DefaultTimeoutSeconds,
            ProbesPerHop = options.ProbesPerHop ?? TracerouteLimits.DefaultProbesPerHop,
            MaxTtl = options.MaxTtl ?? TracerouteLimits.DefaultMaxTtl,
            InitialPort = options.InitialPort ?? TracerouteLimits.DefaultInitialPort,
            PayloadBytes = options.PayloadBytes ?? TracerouteLimits.DefaultPayloadBytes
        };

    /// <summary>
    /// Validates numeric bounds, UDP port progression, and estimated wall duration.
    /// </summary>
    /// <param name="request">Request to validate.</param>
    /// <param name="error">Operator-facing error when invalid.</param>
    /// <returns><see langword="true"/> when all bounds pass.</returns>
    private static bool ValidateRequest(TracerouteRequest request, out string? error)
    {
        error = null;

        if (request.TimeoutSeconds is < TracerouteLimits.MinTimeoutSeconds
            or > TracerouteLimits.MaxTimeoutSeconds)
        {
            error =
                $"% Timeout must be {TracerouteLimits.MinTimeoutSeconds}-{TracerouteLimits.MaxTimeoutSeconds} seconds.";
            return false;
        }

        if (request.ProbesPerHop is < TracerouteLimits.MinProbesPerHop
            or > TracerouteLimits.MaxProbesPerHop)
        {
            error =
                $"% Probe count must be {TracerouteLimits.MinProbesPerHop}-{TracerouteLimits.MaxProbesPerHop}.";
            return false;
        }

        if (request.MaxTtl is < TracerouteLimits.MinTtl or > TracerouteLimits.MaxTtl)
        {
            error = $"% TTL / hop limit must be {TracerouteLimits.MinTtl}-{TracerouteLimits.MaxTtl}.";
            return false;
        }

        if (request.InitialPort is < TracerouteLimits.MinPort or > TracerouteLimits.MaxPort)
        {
            error = $"% Port must be {TracerouteLimits.MinPort}-{TracerouteLimits.MaxPort}.";
            return false;
        }

        if (request.PayloadBytes is < TracerouteLimits.MinPayloadBytes
            or > TracerouteLimits.MaxPayloadBytes)
        {
            error =
                $"% Size (UDP payload) must be {TracerouteLimits.MinPayloadBytes}-{TracerouteLimits.MaxPayloadBytes} bytes.";
            return false;
        }

        int lastPort;
        try
        {
            lastPort = TracerouteLimits.LastDestinationPort(
                request.InitialPort,
                request.MaxTtl,
                request.ProbesPerHop);
        }
        catch (OverflowException)
        {
            error = "% Port progression would overflow the UDP port space.";
            return false;
        }

        if (lastPort > TracerouteLimits.MaxPort)
        {
            error =
                $"% Port progression would exceed {TracerouteLimits.MaxPort} " +
                $"(initial {request.InitialPort}, {request.MaxTtl} hops × {request.ProbesPerHop} probes).";
            return false;
        }

        int estimated;
        try
        {
            estimated = TracerouteLimits.EstimateDurationSeconds(
                request.MaxTtl,
                request.ProbesPerHop,
                request.TimeoutSeconds);
        }
        catch (OverflowException)
        {
            error = "% Traceroute parameters would produce an unbounded execution time.";
            return false;
        }

        if (estimated > TracerouteLimits.MaxEstimatedDurationSeconds)
        {
            error =
                $"% Estimated maximum duration {estimated}s exceeds " +
                $"{TracerouteLimits.MaxEstimatedDurationSeconds}s (ttl × probe × timeout).";
            return false;
        }

        return true;
    }

    /// <summary>Parses a strictly positive invariant integer (no sign/whitespace).</summary>
    /// <param name="text">Token text.</param>
    /// <param name="value">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parse succeeds and value is greater than zero.</returns>
    private static bool TryParsePositiveInt(string text, out int value)
    {
        value = 0;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

    /// <summary>Parses a non-negative invariant integer (no sign/whitespace).</summary>
    /// <param name="text">Token text.</param>
    /// <param name="value">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parse succeeds and value is greater than or equal to zero.</returns>
    private static bool TryParseNonNegativeInt(string text, out int value)
    {
        value = 0;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
               value >= 0;
    }

    /// <summary>Mutable accumulator for traceroute option keywords during parse.</summary>
    private sealed class OptionState
    {
        /// <summary>Optional per-probe timeout in seconds.</summary>
        public int? TimeoutSeconds { get; set; }

        /// <summary>Optional probes per hop.</summary>
        public int? ProbesPerHop { get; set; }

        /// <summary>Optional maximum TTL / hop limit.</summary>
        public int? MaxTtl { get; set; }

        /// <summary>Optional initial port (CLI compatibility).</summary>
        public int? InitialPort { get; set; }

        /// <summary>Optional payload size in bytes.</summary>
        public int? PayloadBytes { get; set; }

        /// <summary>Optional local source bind address.</summary>
        public IPAddress? Source { get; set; }
    }
}
