// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingProbeStats.cs
//
// Purpose:
// Accumulates per-session ping counters and round-trip statistics for ICMP and
// connector session probe summaries.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Mutable accumulator for ping probe outcomes and successful RTT samples.
/// </summary>
/// <remarks>
/// <see cref="LossPercent"/> treats any non-<see cref="IcmpEchoStatus.Success"/>
/// outcome as loss (<c>Sent − Received</c>). Session probes use
/// <see cref="RecordSession"/> which maps failure to the timed-out bucket.
/// </remarks>
internal sealed class PingProbeStats
{
    /// <summary>Successful RTT samples in milliseconds.</summary>
    private readonly List<double> _successMs = [];

    /// <summary>Total probes recorded.</summary>
    public int Sent { get; private set; }

    /// <summary>Probes that completed successfully.</summary>
    public int Received { get; private set; }

    /// <summary>Probes classified as timed out.</summary>
    public int TimedOut { get; private set; }

    /// <summary>Probes classified as destination unreachable.</summary>
    public int Unreachable { get; private set; }

    /// <summary>Probes that reported fragmentation needed.</summary>
    public int FragmentationFailures { get; private set; }

    /// <summary>Probes with any other failure status (including <see cref="IcmpEchoStatus.Error"/>).</summary>
    public int OtherFailures { get; private set; }

    /// <summary>
    /// Records one ICMP echo outcome and, on success, its round-trip sample.
    /// </summary>
    /// <param name="status">ICMP echo status from the client.</param>
    /// <param name="rtt">Round-trip time; used only when <paramref name="status"/> is success.</param>
    public void Record(IcmpEchoStatus status, TimeSpan rtt)
    {
        Sent++;
        switch (status)
        {
            case IcmpEchoStatus.Success:
                Received++;
                _successMs.Add(rtt.TotalMilliseconds);
                break;
            case IcmpEchoStatus.TimedOut:
                TimedOut++;
                break;
            case IcmpEchoStatus.DestinationUnreachable:
                Unreachable++;
                break;
            case IcmpEchoStatus.FragmentationNeeded:
                FragmentationFailures++;
                break;
            default:
                OtherFailures++;
                break;
        }
    }

    /// <summary>
    /// Records one connector session ping outcome.
    /// </summary>
    /// <param name="success"><see langword="true"/> when the connector responded successfully.</param>
    /// <param name="rtt">Round-trip time; recorded only on success.</param>
    /// <remarks>Failures increment <see cref="TimedOut"/> (not a separate session-failure bucket).</remarks>
    public void RecordSession(bool success, TimeSpan rtt)
    {
        Sent++;
        if (success)
        {
            Received++;
            _successMs.Add(rtt.TotalMilliseconds);
        }
        else
        {
            TimedOut++;
        }
    }

    /// <summary>
    /// Gets packet loss as a whole-number percent of <see cref="Sent"/>.
    /// </summary>
    /// <value>0 when nothing was sent; otherwise rounded <c>100 × (Sent − Received) / Sent</c>.</value>
    public int LossPercent =>
        Sent == 0 ? 0 : (int)Math.Round(100.0 * (Sent - Received) / Sent, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Gets min/avg/max successful RTT in whole milliseconds, or <see langword="null"/> if none succeeded.
    /// </summary>
    public (int Min, int Avg, int Max)? RoundTripMs
    {
        get
        {
            if (_successMs.Count == 0)
            {
                return null;
            }

            var min = (int)Math.Round(_successMs.Min(), MidpointRounding.AwayFromZero);
            var max = (int)Math.Round(_successMs.Max(), MidpointRounding.AwayFromZero);
            var avg = (int)Math.Round(_successMs.Average(), MidpointRounding.AwayFromZero);
            return (min, avg, max);
        }
    }
}
