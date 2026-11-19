// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteDeliveryStats.cs
//
// Purpose:
// Observability counters for the serial→remote delivery pipeline, including the
// BytesAccepted accounting invariant and reconnect downtime.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Observability for the serial→remote delivery pipeline.
/// </summary>
/// <remarks>
/// Payloads are never logged. Accounting invariant at every observation point:
/// <c>BytesAccepted = BytesSent + BytesDropped + BytesQueued + BytesInFlight</c>.
/// </remarks>
public sealed class RemoteDeliveryStats
{
    /// <summary>
    /// Bytes currently buffered awaiting remote transmission.
    /// </summary>
    public long BytesQueued { get; init; }

    /// <summary>
    /// Cumulative bytes accepted into the outbound pipeline from serial RX.
    /// </summary>
    public long BytesAccepted { get; init; }

    /// <summary>
    /// Cumulative bytes accepted by the transport send API for transmission.
    /// </summary>
    /// <remarks>
    /// Not an acknowledgment that a remote NOC/operator client received the bytes.
    /// </remarks>
    public long BytesSent { get; init; }

    /// <summary>
    /// Cumulative bytes dropped (overflow, outage discard, complete/dispose, or abandoned in-flight).
    /// </summary>
    public long BytesDropped { get; init; }

    /// <summary>
    /// Bytes dequeued for transmission that have not yet been marked sent or requeued.
    /// </summary>
    public long BytesInFlight { get; init; }

    /// <summary>
    /// Number of remote connect/reconnect attempts observed for this session cycle.
    /// </summary>
    public long ConnectAttempts { get; init; }

    /// <summary>
    /// Cumulative time the remote path was unavailable while the technician session remained active.
    /// </summary>
    public TimeSpan CumulativeRemoteDowntime { get; init; }
}
