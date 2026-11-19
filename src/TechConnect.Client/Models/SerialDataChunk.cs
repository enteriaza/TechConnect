// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialDataChunk.cs
//
// Purpose:
// Bounded serial traffic chunk for UI display and channel publication, carrying
// timestamp, direction, and payload bytes that must never be logged.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// A bounded serial traffic chunk for UI display and RX channel publication.
/// </summary>
/// <remarks>
/// Payloads must never be written to application logs. Callers that retain
/// <see cref="Data"/> beyond the raising event should treat ownership as a copy
/// already made by the publisher when applicable.
/// </remarks>
public sealed class SerialDataChunk
{
    /// <summary>
    /// UTC timestamp when the chunk was observed/published.
    /// </summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>
    /// Whether the chunk is device→host (RX) or host→device (TX).
    /// </summary>
    public required SerialDataDirection Direction { get; init; }

    /// <summary>
    /// Raw serial octets for this chunk.
    /// </summary>
    /// <remarks>Must never be logged.</remarks>
    public required ReadOnlyMemory<byte> Data { get; init; }
}
