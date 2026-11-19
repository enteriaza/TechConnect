// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialTrafficStats.cs
//
// Purpose:
// Immutable serial traffic counters for UI/diagnostics, including last RX/TX
// timestamps and careful TX semantics after driver output drain.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Serial traffic counters.
/// </summary>
/// <remarks>
/// <see cref="BytesTransmitted"/> is bytes accepted through the local serial write
/// path after the driver output queue drained for each write — not proof of
/// device-level acceptance. Snapshots are point-in-time copies; they are not
/// live-updating views.
/// </remarks>
public sealed class SerialTrafficStats
{
    /// <summary>
    /// Cumulative bytes received from the physical serial device (RX).
    /// </summary>
    public long BytesReceived { get; init; }

    /// <summary>
    /// Cumulative bytes counted as transmitted after local write + driver drain (TX).
    /// </summary>
    public long BytesTransmitted { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent RX publication, or <see langword="null"/> if none.
    /// </summary>
    public DateTimeOffset? LastRxUtc { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent successful TX write path, or <see langword="null"/> if none.
    /// </summary>
    public DateTimeOffset? LastTxUtc { get; init; }
}
