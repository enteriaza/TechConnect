// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialLoopbackProbeResult.cs
//
// Purpose:
// Structured result of an explicitly invoked TX→RX loopback probe, including
// match/timeout flags and optional live settings/diagnostics.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Result of an explicitly invoked TX→RX loopback probe.
/// </summary>
/// <remarks>
/// Does not claim hardware RX validation unless <see cref="ProbeMatched"/> is true.
/// Probe payloads must never be written to application logs.
/// </remarks>
public sealed class SerialLoopbackProbeResult
{
    /// <summary>
    /// COM port name on which the probe ran.
    /// </summary>
    public required string PortName { get; init; }

    /// <summary>
    /// Number of probe bytes written to TX.
    /// </summary>
    public int BytesWritten { get; init; }

    /// <summary>
    /// Number of RX bytes observed while waiting for a match.
    /// </summary>
    public int BytesReceivedDuringProbe { get; init; }

    /// <summary>
    /// Whether the expected probe sequence was observed on RX.
    /// </summary>
    public bool ProbeMatched { get; init; }

    /// <summary>
    /// Whether the wait ended because the timeout elapsed without a match.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Wall-clock duration of the probe attempt.
    /// </summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Human-readable summary suitable for UI/status (no payload bytes).
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Effective port settings captured around the probe, when available.
    /// </summary>
    public SerialPortEffectiveSettings? EffectiveSettings { get; init; }

    /// <summary>
    /// RX diagnostics captured after the probe, when available.
    /// </summary>
    public SerialReceiveDiagnostics? ReceiveDiagnosticsAfter { get; init; }
}
