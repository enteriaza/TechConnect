// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialReceiveDiagnostics.cs
//
// Purpose:
// Observability counters for the serial RX path (reader activity, zero-reads,
// publication counts, last error) without including payloads.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Observability for the serial RX path.
/// </summary>
/// <remarks>
/// Never includes payloads. Counters are cumulative for the current open session
/// unless the implementation resets them on open.
/// </remarks>
public sealed class SerialReceiveDiagnostics
{
    /// <summary>
    /// Whether the background RX reader task is currently running.
    /// </summary>
    public bool IsReaderActive { get; init; }

    /// <summary>
    /// Number of read calls issued against the port stream.
    /// </summary>
    public long ReadCallCount { get; init; }

    /// <summary>
    /// Number of read calls that returned zero bytes.
    /// </summary>
    public long ZeroReadCount { get; init; }

    /// <summary>
    /// Bytes accepted from the port into the RX path (aligned with traffic RX stats).
    /// </summary>
    public long BytesReceivedFromPort { get; init; }

    /// <summary>
    /// Bytes published onto the <c>Incoming</c> channel for remote consumers.
    /// </summary>
    public long BytesPublishedToIncomingChannel { get; init; }

    /// <summary>
    /// RX bytes that did not remain available for remote bridging via <c>Incoming</c>.
    /// </summary>
    /// <remarks>
    /// Includes bytes never published because remote ingress was disabled (or raced to disable),
    /// bytes rejected by a failed <c>TryWrite</c>, and bytes removed by
    /// <c>DrainPendingRemoteIngress</c> after the bridge stopped. Local console delivery via
    /// <c>DataTransferred</c> is independent and still occurs for port RX.
    /// </remarks>
    public long BytesSkippedRemoteIngressDisabled { get; init; }

    /// <summary>
    /// Whether remote ingress publication onto <c>Incoming</c> is currently enabled.
    /// </summary>
    public bool IsRemoteIngressEnabled { get; init; }

    /// <summary>
    /// Items currently buffered in <c>Incoming</c> when the channel supports counting;
    /// otherwise <c>-1</c>.
    /// </summary>
    public int PendingIncomingItems { get; init; }

    /// <summary>
    /// Bytes delivered via the UI <c>DataTransferred</c> event for RX.
    /// </summary>
    public long BytesDeliveredToDataTransferred { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent RX byte acceptance, or null if none.
    /// </summary>
    public DateTimeOffset? LastRxUtc { get; init; }

    /// <summary>
    /// Last RX error message suitable for diagnostics (no payload), or null.
    /// </summary>
    public string? LastReadError { get; init; }

    /// <summary>
    /// UTC timestamp of <see cref="LastReadError"/>, or null if none.
    /// </summary>
    public DateTimeOffset? LastReadErrorUtc { get; init; }
}
