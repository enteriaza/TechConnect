// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteOutputFormatter.cs
//
// Purpose:
// Formats traceroute header, fixed-width hop rows (address + per-probe RTT),
// and footer lines for the operator CLI.
//
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Formats traceroute header, hop rows, and footer.
/// </summary>
/// <remarks>
/// Hop rows are always complete: one address column plus a fixed RTT column per
/// configured probe. Missing probe slots (cancellation / incomplete hop) are
/// normalized to timed-out placeholders and displayed as <c>*</c>.
/// </remarks>
internal static class TracerouteOutputFormatter
{
    /// <summary>Right-aligned hop number width (supports TTL up to 64).</summary>
    internal const int HopWidth = 3;

    /// <summary>Left-aligned address / hostname+IP width.</summary>
    internal const int AddressWidth = 56;

    /// <summary>Fixed width for each RTT / timeout column.</summary>
    internal const int RttColumnWidth = 11;

    /// <summary>
    /// Formats the traceroute session header (destination label and hop/probe counts).
    /// </summary>
    /// <param name="request">Active traceroute request.</param>
    /// <returns>
    /// CRLF-terminated header. When input differs from the resolved IP, shows
    /// <c>hostname [ip]</c>; otherwise the IP alone.
    /// </returns>
    public static string FormatHeader(TracerouteRequest request)
    {
        var dest = request.Destination.ToString();
        var label = request.DestinationInput.Equals(dest, StringComparison.OrdinalIgnoreCase)
            ? dest
            : $"{request.DestinationInput} [{dest}]";

        return
            $"Tracing route to {label}\r\n" +
            $"Maximum hops: {request.MaxTtl}, probes per hop: {request.ProbesPerHop}\r\n";
    }

    /// <summary>
    /// Renders one hop row after all probes for that hop have been collected.
    /// </summary>
    /// <param name="ttl">Hop number / TTL value shown in the first column.</param>
    /// <param name="probes">Collected probe results (may be incomplete or out of order).</param>
    /// <param name="probesPerHop">Configured probe count; must be at least 1.</param>
    /// <param name="reverseHostname">
    /// Optional reverse DNS name prepended as <c>name [ip]</c> when a responder is known.
    /// </param>
    /// <returns>CRLF-terminated fixed-width hop line.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="probesPerHop"/> is less than 1.</exception>
    /// <remarks>
    /// Missing probe slots are shown as <c>*</c>. Destination unreachable and
    /// administratively prohibited append <c> !H</c> / <c> !A</c> to the RTT cell.
    /// </remarks>
    public static string FormatHopLine(
        int ttl,
        IReadOnlyList<TracerouteProbeResult> probes,
        int probesPerHop,
        string? reverseHostname = null)
    {
        if (probesPerHop < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probesPerHop));
        }

        var ordered = NormalizeProbes(ttl, probes, probesPerHop);
        var address = SelectAddress(ordered, reverseHostname);

        var sb = new StringBuilder(HopWidth + 2 + AddressWidth + probesPerHop * RttColumnWidth + 2);
        sb.Append(CultureInfo.InvariantCulture, $"{ttl,HopWidth}");
        sb.Append("  ");
        sb.Append(PadAddress(address));

        for (var i = 0; i < ordered.Count; i++)
        {
            sb.Append(FormatRttCell(ordered[i]));
        }

        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>
    /// Formats the traceroute footer based on how the session ended.
    /// </summary>
    /// <param name="destinationReached"><see langword="true"/> when an Echo Reply (or reachability) was observed.</param>
    /// <param name="cancelled"><see langword="true"/> when the operator canceled; takes precedence.</param>
    /// <param name="terminalFailure"><see langword="true"/> when the trace aborted on error/unreachable.</param>
    /// <returns>CRLF-terminated footer line.</returns>
    public static string FormatFooter(bool destinationReached, bool cancelled, bool terminalFailure)
    {
        if (cancelled)
        {
            return "Trace cancelled.\r\n";
        }

        if (terminalFailure)
        {
            return "Trace aborted.\r\n";
        }

        if (destinationReached)
        {
            return "Trace complete.\r\n";
        }

        return "Trace complete: destination not reached.\r\n";
    }

    /// <summary>
    /// Ensures exactly <paramref name="probesPerHop"/> slots ordered by probe index.
    /// </summary>
    /// <param name="ttl">TTL assigned to placeholder timed-out slots.</param>
    /// <param name="probes">Raw probe results; out-of-range indices are ignored; first wins on duplicates.</param>
    /// <param name="probesPerHop">Required slot count.</param>
    /// <returns>A list of length <paramref name="probesPerHop"/> with timed-out placeholders for gaps.</returns>
    internal static List<TracerouteProbeResult> NormalizeProbes(
        int ttl,
        IReadOnlyList<TracerouteProbeResult> probes,
        int probesPerHop)
    {
        var byIndex = new TracerouteProbeResult?[probesPerHop];
        foreach (var probe in probes)
        {
            if (probe.ProbeIndex < 1 || probe.ProbeIndex > probesPerHop)
            {
                continue;
            }

            // Keep the first result for a given probe index (ignore duplicates).
            byIndex[probe.ProbeIndex - 1] ??= probe;
        }

        var result = new List<TracerouteProbeResult>(probesPerHop);
        for (var i = 0; i < probesPerHop; i++)
        {
            result.Add(byIndex[i] ?? new TracerouteProbeResult
            {
                Ttl = ttl,
                ProbeIndex = i + 1,
                Status = TracerouteProbeStatus.TimedOut
            });
        }

        return result;
    }

    /// <summary>
    /// Selects the address column text from the first non-timeout/non-error responder.
    /// </summary>
    /// <param name="ordered">Normalized probe slots.</param>
    /// <param name="reverseHostname">Optional reverse DNS label.</param>
    /// <returns>IP, <c>hostname [ip]</c>, or <c>*</c> when no usable responder exists.</returns>
    private static string SelectAddress(
        IReadOnlyList<TracerouteProbeResult> ordered,
        string? reverseHostname)
    {
        foreach (var probe in ordered)
        {
            if (probe.Responder is not null &&
                probe.Status is not TracerouteProbeStatus.TimedOut and not TracerouteProbeStatus.Error)
            {
                var ip = probe.Responder.ToString();
                if (!string.IsNullOrWhiteSpace(reverseHostname))
                {
                    return $"{reverseHostname} [{ip}]";
                }

                return ip;
            }
        }

        return "*";
    }

    /// <summary>Pads or returns the address column to <see cref="AddressWidth"/>.</summary>
    /// <param name="address">Address column text.</param>
    /// <returns>Right-padded text, or the original if already at/above width.</returns>
    private static string PadAddress(string address)
    {
        if (address.Length >= AddressWidth)
        {
            return address;
        }

        return address.PadRight(AddressWidth);
    }

    /// <summary>
    /// Formats one RTT cell, including unreachable markers and timeout stars.
    /// </summary>
    /// <param name="probe">Probe result for this column.</param>
    /// <returns>Left-padded cell of width <see cref="RttColumnWidth"/>.</returns>
    private static string FormatRttCell(TracerouteProbeResult probe)
    {
        if (probe.Status is TracerouteProbeStatus.TimedOut or TracerouteProbeStatus.Error ||
            probe.RoundTripTime is null)
        {
            return PadRtt("*");
        }

        var ms = probe.RoundTripTime.Value.TotalMilliseconds;
        string text;
        if (ms < 1)
        {
            text = "<1 ms";
        }
        else
        {
            text = string.Create(
                CultureInfo.InvariantCulture,
                $"{Math.Round(ms, MidpointRounding.AwayFromZero)} ms");
        }

        if (probe.Status is TracerouteProbeStatus.DestinationUnreachable
            or TracerouteProbeStatus.AdministrativelyProhibited)
        {
            text += probe.Status == TracerouteProbeStatus.AdministrativelyProhibited ? " !A" : " !H";
        }

        return PadRtt(text);
    }

    /// <summary>Left-pads an RTT cell to <see cref="RttColumnWidth"/>.</summary>
    /// <param name="text">Cell text.</param>
    /// <returns>Padded or original text when already at/above width.</returns>
    private static string PadRtt(string text)
    {
        if (text.Length >= RttColumnWidth)
        {
            return text;
        }

        return text.PadLeft(RttColumnWidth);
    }
}
