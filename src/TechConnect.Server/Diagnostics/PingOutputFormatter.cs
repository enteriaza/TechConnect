// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingOutputFormatter.cs
//
// Purpose:
// Formats operator-facing ping headers, per-probe indicators, summaries, and
// sweep size lines for ICMP and session ping sessions.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Formats Cisco-style ping progress and summary text for the operator CLI.
/// </summary>
/// <remarks>
/// Sweep output explicitly disclaims path-MTU exactness; overhead estimates use
/// <see cref="PingLimits"/> constants only.
/// </remarks>
internal static class PingOutputFormatter
{
    /// <summary>
    /// Formats the ICMP / ICMPv6 ping session header (destination, sizes, options).
    /// </summary>
    /// <param name="request">Parsed ping request; <see cref="PingRequest.Destination"/> must be set.</param>
    /// <returns>CRLF-terminated header block including a blank trailing line.</returns>
    public static string FormatIcmpHeader(PingRequest request)
    {
        var ip = request.Destination!.ToString();
        var dest = string.IsNullOrWhiteSpace(request.DestinationInput) ||
                   request.DestinationInput.Equals(ip, StringComparison.OrdinalIgnoreCase)
            ? ip
            : $"{request.DestinationInput} [{ip}]";
        var family = request.Kind == PingTargetKind.Ipv6 ? "ICMPv6" : "ICMP";
        var lines = new StringBuilder();
        lines.Append("Type escape sequence to abort.\r\n");
        if (request.Sweep)
        {
            var overhead = request.Kind == PingTargetKind.Ipv6
                ? PingLimits.Ipv6IcmpOverheadBytes
                : PingLimits.Ipv4IcmpOverheadBytes;
            lines.Append(
                CultureInfo.InvariantCulture,
                $"Sweeping {family} payload sizes {request.SweepMinBytes}-{request.SweepMaxBytes} by {request.SweepIntervalBytes} to {dest}, timeout is {request.TimeoutSeconds} seconds:\r\n");
            lines.Append(
                CultureInfo.InvariantCulture,
                $"(Sizes are ICMP payload bytes; estimated IP packet ≈ payload + {overhead} bytes. Not an exact path-MTU claim.)\r\n");
        }
        else
        {
            lines.Append(
                CultureInfo.InvariantCulture,
                $"Sending {request.Repeat}, {request.PayloadBytes}-byte {family} Echos to {dest}, timeout is {request.TimeoutSeconds} seconds:\r\n");
        }

        if (request.DontFragment)
        {
            lines.Append("Packet options: Don't Fragment\r\n");
        }

        if (request.Source is not null)
        {
            lines.Append(CultureInfo.InvariantCulture, $"Source: {request.Source}\r\n");
        }

        lines.Append("\r\n");
        return lines.ToString();
    }

    /// <summary>
    /// Formats the connector session ping header.
    /// </summary>
    /// <param name="sessionId">Connector session identifier.</param>
    /// <param name="hostname">Reported hostname, or null/blank for <c>unknown-host</c>.</param>
    /// <returns>CRLF-terminated header including a blank trailing line.</returns>
    public static string FormatSessionHeader(string sessionId, string? hostname)
    {
        var host = string.IsNullOrWhiteSpace(hostname) ? "unknown-host" : hostname;
        return $"Pinging connector session {sessionId} ({host}):\r\n\r\n";
    }

    /// <summary>
    /// Maps an ICMP echo status to a single progress character.
    /// </summary>
    /// <param name="status">Echo outcome.</param>
    /// <returns>
    /// <c>!</c> success, <c>.</c> timeout, <c>F</c> fragmentation needed,
    /// <c>U</c> unreachable, <c>?</c> other.
    /// </returns>
    public static char Indicator(IcmpEchoStatus status) =>
        status switch
        {
            IcmpEchoStatus.Success => '!',
            IcmpEchoStatus.TimedOut => '.',
            IcmpEchoStatus.FragmentationNeeded => 'F',
            IcmpEchoStatus.DestinationUnreachable => 'U',
            _ => '?'
        };

    /// <summary>
    /// Formats the success-rate and optional RTT summary after a probe series.
    /// </summary>
    /// <param name="stats">Accumulated probe statistics.</param>
    /// <param name="cancelled">When <see langword="true"/>, prepends an abort line.</param>
    /// <returns>CRLF-terminated summary text.</returns>
    public static string FormatSummary(PingProbeStats stats, bool cancelled = false)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n");
        if (cancelled)
        {
            sb.Append("Ping aborted.\r\n");
        }

        sb.Append(
            CultureInfo.InvariantCulture,
            $"Success rate is {100 - stats.LossPercent} percent ({stats.Received}/{stats.Sent})\r\n");

        var rtt = stats.RoundTripMs;
        if (rtt is { } times)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"Round-trip min/avg/max = {times.Min}/{times.Avg}/{times.Max} ms\r\n");
        }

        if (stats.FragmentationFailures > 0)
        {
            sb.Append(
                CultureInfo.InvariantCulture,
                $"Fragmentation-related failures: {stats.FragmentationFailures}\r\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a session ping summary including the final connector session state line.
    /// </summary>
    /// <param name="stats">Accumulated probe statistics.</param>
    /// <param name="sessionState">Human-readable connector session state.</param>
    /// <param name="cancelled">When <see langword="true"/>, includes the abort line from <see cref="FormatSummary"/>.</param>
    /// <returns>CRLF-terminated summary text.</returns>
    public static string FormatSessionSummary(PingProbeStats stats, string sessionState, bool cancelled = false)
    {
        var sb = new StringBuilder();
        sb.Append(FormatSummary(stats, cancelled));
        sb.Append(CultureInfo.InvariantCulture, $"Session state: {sessionState}\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Formats one sweep size result line with estimated IP packet size.
    /// </summary>
    /// <param name="payloadBytes">ICMP payload size for this step.</param>
    /// <param name="estimatedPacketBytes">Estimated IP packet size (payload + overhead).</param>
    /// <param name="indicator">Progress character for the step.</param>
    /// <returns>CRLF-terminated line.</returns>
    public static string FormatSweepSizeResult(int payloadBytes, int estimatedPacketBytes, char indicator) =>
        FormattableString.Invariant(
            $"  payload {payloadBytes} (≈{estimatedPacketBytes} IP): {indicator}\r\n");

    /// <summary>
    /// Formats the largest successful sweep payload disclaimer line.
    /// </summary>
    /// <param name="largestPayload">Largest successful ICMP payload, or null if none succeeded.</param>
    /// <returns>CRLF-terminated line; does not claim exact path MTU.</returns>
    public static string FormatLargestSuccessfulPayload(int? largestPayload)
    {
        if (largestPayload is null)
        {
            return "No successful payload sizes in this sweep.\r\n";
        }

        return FormattableString.Invariant(
            $"Largest successful ICMP payload in this sweep: {largestPayload} bytes (not claimed as exact path MTU).\r\n");
    }
}
