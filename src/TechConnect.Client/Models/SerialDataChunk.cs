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
/// Payloads must never be written to application logs.
/// <para>
/// <b>Ownership is not encoded in the type.</b> Callers must honour the publisher’s contract:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>TX</b> and <b>RX with remote ingress enabled</b>: <see cref="Data"/> references a
/// durable <see cref="byte"/> array that may be retained by <c>Incoming</c> / outbound queues.
/// </item>
/// <item>
/// <b>RX with remote ingress disabled</b>: <see cref="Data"/> may be an ephemeral view over
/// the serial reader’s reusable buffer. It is valid only for the synchronous
/// <c>DataTransferred</c> callback. Handlers must copy any bytes they need before returning
/// and must not store the memory, queue it, or await while holding it.
/// </item>
/// </list>
/// The CLR event model cannot enforce ephemeral lifetime; incorrect retention is a contract
/// violation that can corrupt subsequent reads.
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
    /// <remarks>
    /// Must never be logged. Lifetime depends on how the chunk was published — see type remarks.
    /// </remarks>
    public required ReadOnlyMemory<byte> Data { get; init; }
}
