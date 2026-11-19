// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteOutputFormatterTests.cs
//
// Purpose:
// Verifies traceroute header/hop/footer formatting and probe normalization display rules.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for TracerouteOutputFormatter.
/// </summary>
public sealed class TracerouteOutputFormatterTests
{
    /// <summary>
    /// Three successful probes render as hop, address, and three RTT columns with fixed padding.
    /// </summary>
    [Fact]
    public void FormatHopLine_ThreeSuccessfulProbes_AlignedColumns()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            1,
            [
                Success(1, 1, "198.18.0.65", 14),
                Success(1, 2, "198.18.0.65", 5),
                Success(1, 3, "198.18.0.65", 13)
            ],
            probesPerHop: 3);

        line.Should().Be(Expected(1, "198.18.0.65", "14 ms", "5 ms", "13 ms"));
    }

    /// <summary>
    /// All-timeout hops show <c>*</c> for the address and each RTT cell.
    /// </summary>
    [Fact]
    public void FormatHopLine_ThreeTimeouts_AllStarColumns()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            8,
            [
                Timeout(8, 1),
                Timeout(8, 2),
                Timeout(8, 3)
            ],
            probesPerHop: 3);

        line.Should().Be(Expected(8, "*", "*", "*", "*"));
    }

    /// <summary>
    /// Mixed success/timeout preserves three RTT columns (stars for timeouts).
    /// </summary>
    [Fact]
    public void FormatHopLine_MixedSuccessAndTimeout_KeepsAllColumns()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            7,
            [
                Timeout(7, 1),
                Success(7, 2, "10.0.0.1", 12),
                Timeout(7, 3)
            ],
            probesPerHop: 3);

        line.Should().Be(Expected(7, "10.0.0.1", "*", "12 ms", "*"));
    }

    /// <summary>
    /// Partial hop results pad missing probes as <c>*</c> out to probesPerHop.
    /// </summary>
    [Fact]
    public void FormatHopLine_PartialHop_PadsMissingProbesAsStars()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            3,
            [Success(3, 1, "100.102.0.220", 5)],
            probesPerHop: 3);

        line.Should().Be(Expected(3, "100.102.0.220", "5 ms", "*", "*"));
    }

    /// <summary>
    /// Long IPv6 addresses stay in the address column and do not shift RTT column origins.
    /// </summary>
    [Fact]
    public void FormatHopLine_Ipv6Address_DoesNotShiftRttColumns()
    {
        var ipv6 = "2001:db8:85a3::8a2e:370:7334";
        var line1 = TracerouteOutputFormatter.FormatHopLine(
            1,
            [Success(1, 1, ipv6, 4), Success(1, 2, ipv6, 5), Success(1, 3, ipv6, 6)],
            probesPerHop: 3);
        var line2 = TracerouteOutputFormatter.FormatHopLine(
            2,
            [Success(2, 1, "10.0.0.1", 1), Success(2, 2, "10.0.0.1", 2), Success(2, 3, "10.0.0.1", 3)],
            probesPerHop: 3);

        IndexOfRtt(line1, "4 ms").Should().Be(IndexOfRtt(line2, "1 ms"));
        IndexOfRtt(line1, "4 ms").Should().Be(RttColumnStart());
        line1.Should().Contain(ipv6);
        line1.Length.Should().Be(line2.Length);
    }

    /// <summary>
    /// Multi-digit hop numbers keep the same RTT column origin and line width as single-digit hops.
    /// </summary>
    [Fact]
    public void FormatHopLine_MultiDigitHop_AlignsWithSingleDigit()
    {
        var hop1 = TracerouteOutputFormatter.FormatHopLine(
            1,
            [Timeout(1, 1), Timeout(1, 2), Timeout(1, 3)],
            probesPerHop: 3);
        var hop10 = TracerouteOutputFormatter.FormatHopLine(
            10,
            [Timeout(10, 1), Timeout(10, 2), Timeout(10, 3)],
            probesPerHop: 3);

        hop1[..3].Should().Be("  1");
        hop10[..3].Should().Be(" 10");
        IndexOfRtt(hop1, "*").Should().Be(IndexOfRtt(hop10, "*"));
        hop1.Length.Should().Be(hop10.Length);
    }

    /// <summary>
    /// When probesPerHop=2, the line has exactly two RTT columns (not three).
    /// </summary>
    [Fact]
    public void FormatHopLine_TwoProbesPerHop_RendersExactlyTwoRttColumns()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            2,
            [Success(2, 1, "8.8.8.8", 20), Timeout(2, 2)],
            probesPerHop: 2);

        line.Should().Be(Expected(2, "8.8.8.8", "20 ms", "*"));
    }

    /// <summary>
    /// Out-of-order probe inputs are sorted by ProbeIndex before rendering RTT cells.
    /// </summary>
    [Fact]
    public void FormatHopLine_OutOfOrderInput_OrdersByProbeIndex()
    {
        var line = TracerouteOutputFormatter.FormatHopLine(
            1,
            [
                Success(1, 3, "1.1.1.1", 30),
                Success(1, 1, "1.1.1.1", 10),
                Success(1, 2, "1.1.1.1", 20)
            ],
            probesPerHop: 3);

        line.Should().Be(Expected(1, "1.1.1.1", "10 ms", "20 ms", "30 ms"));
    }

    /// <summary>
    /// <see cref="TracerouteOutputFormatter.NormalizeProbes"/> keeps the first probe when ProbeIndex duplicates.
    /// </summary>
    [Fact]
    public void NormalizeProbes_DuplicateIndex_KeepsFirst()
    {
        var normalized = TracerouteOutputFormatter.NormalizeProbes(
            1,
            [
                Success(1, 1, "10.0.0.1", 1),
                Success(1, 1, "10.0.0.2", 99)
            ],
            probesPerHop: 1);

        normalized.Should().ContainSingle();
        normalized[0].Responder.Should().Be(IPAddress.Parse("10.0.0.1"));
    }
    /// <summary>Builds the exact expected hop line using formatter column widths.</summary>
    private static string Expected(int ttl, string address, params string[] rttCells)
    {
        var hop = ttl.ToString().PadLeft(TracerouteOutputFormatter.HopWidth);
        var addr = address.PadRight(TracerouteOutputFormatter.AddressWidth);
        var rtts = string.Concat(rttCells.Select(c => c.PadLeft(TracerouteOutputFormatter.RttColumnWidth)));
        return hop + "  " + addr + rtts + "\r\n";
    }
    /// <summary>Character index where the first RTT column begins.</summary>
    private static int RttColumnStart() =>
        TracerouteOutputFormatter.HopWidth + 2 + TracerouteOutputFormatter.AddressWidth;
    /// <summary>Finds a left-padded RTT cell within a formatted hop line.</summary>
    private static int IndexOfRtt(string line, string cell) =>
        line.IndexOf(cell.PadLeft(TracerouteOutputFormatter.RttColumnWidth), StringComparison.Ordinal);
    /// <summary>TimeExceeded probe fixture with responder and RTT.</summary>
    private static TracerouteProbeResult Success(int ttl, int index, string address, double ms) =>
        new()
        {
            Ttl = ttl,
            ProbeIndex = index,
            Status = TracerouteProbeStatus.TimeExceeded,
            Responder = IPAddress.Parse(address),
            RoundTripTime = TimeSpan.FromMilliseconds(ms)
        };
    /// <summary>TimedOut probe fixture for a hop/probe index.</summary>
    private static TracerouteProbeResult Timeout(int ttl, int index) =>
        new()
        {
            Ttl = ttl,
            ProbeIndex = index,
            Status = TracerouteProbeStatus.TimedOut
        };
}
