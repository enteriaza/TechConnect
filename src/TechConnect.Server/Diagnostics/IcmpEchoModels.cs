// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IcmpEchoModels.cs
//
// Purpose:
// Defines ICMP echo request/reply models, status codes, and the IIcmpEchoClient
// contract used by raw-socket ping probing.
//
// -----------------------------------------------------------------------------

using System.Net;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Outcome classification for a single ICMP / ICMPv6 echo exchange.
/// </summary>
internal enum IcmpEchoStatus
{
    /// <summary>Matching Echo Reply received.</summary>
    Success = 0,

    /// <summary>No matching reply within the per-probe timeout.</summary>
    TimedOut = 1,

    /// <summary>Destination Unreachable (or similar) matched to this probe.</summary>
    DestinationUnreachable = 2,

    /// <summary>IPv4 Fragmentation Needed and DF set (ICMP type 3 code 4).</summary>
    FragmentationNeeded = 3,

    /// <summary>Socket, privilege, or other non-timeout failure.</summary>
    Error = 4
}

/// <summary>
/// Parameters for one raw-socket ICMP / ICMPv6 Echo Request.
/// </summary>
internal sealed class IcmpEchoRequest
{
    /// <summary>Remote address to probe.</summary>
    public required IPAddress Destination { get; init; }

    /// <summary>Optional local bind address; must match destination family when set.</summary>
    public IPAddress? Source { get; init; }

    /// <summary>ICMP payload length in bytes.</summary>
    public int PayloadBytes { get; init; } = PingLimits.DefaultPayloadBytes;

    /// <summary>Per-probe receive timeout (linked with the caller's cancellation token).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(PingLimits.DefaultTimeoutSeconds);

    /// <summary>
    /// When <see langword="true"/>, sets Don't Fragment on IPv4 sockets.
    /// Unsupported for IPv6 (client returns <see cref="IcmpEchoStatus.Error"/>).
    /// </summary>
    public bool DontFragment { get; init; }

    /// <summary>ICMP Echo identifier field used to match replies.</summary>
    public ushort Identifier { get; init; }

    /// <summary>ICMP Echo sequence field used to match replies.</summary>
    public ushort Sequence { get; init; }
}

/// <summary>
/// Result of one ICMP / ICMPv6 echo exchange.
/// </summary>
internal sealed class IcmpEchoReply
{
    /// <summary>Outcome status for the probe.</summary>
    public required IcmpEchoStatus Status { get; init; }

    /// <summary>Measured round-trip time when a matching packet was received; otherwise default.</summary>
    public TimeSpan RoundTripTime { get; init; }

    /// <summary>Human-readable error detail for failure statuses.</summary>
    public string? Error { get; init; }

    /// <summary>Source address of the matching ICMP packet when available.</summary>
    public IPAddress? Responder { get; init; }
}

/// <summary>
/// Sends a single ICMP / ICMPv6 Echo Request and waits for a matching reply or timeout.
/// </summary>
internal interface IIcmpEchoClient
{
    /// <summary>
    /// Sends one echo probe and returns the matched reply, timeout, or error status.
    /// </summary>
    /// <param name="request">Probe parameters including destination, timeout, and identifiers.</param>
    /// <param name="cancellationToken">
    /// Token that cancels send/receive. Distinct from the per-probe timeout, which is applied
    /// via a linked token source inside the implementation.
    /// </param>
    /// <returns>The echo outcome; privilege/platform failures are returned as <see cref="IcmpEchoStatus.Error"/> rather than thrown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or its destination is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Payload size is outside <see cref="PingLimits"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    Task<IcmpEchoReply> SendAsync(IcmpEchoRequest request, CancellationToken cancellationToken);
}
