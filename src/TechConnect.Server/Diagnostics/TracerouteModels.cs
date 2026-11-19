// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteModels.cs
//
// Purpose:
// Defines traceroute address-family and probe-status enums, request/result
// models, and the IIcmpTracerouteClient streaming contract.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Address family selected for a traceroute request.
/// </summary>
internal enum TracerouteAddressFamily
{
    /// <summary>IPv4 / ICMP probes.</summary>
    Ipv4 = 1,

    /// <summary>IPv6 / ICMPv6 probes.</summary>
    Ipv6 = 2
}

/// <summary>
/// Classification of a single traceroute probe outcome.
/// </summary>
internal enum TracerouteProbeStatus
{
    /// <summary>TTL / Hop Limit exceeded (intermediate hop).</summary>
    TimeExceeded = 0,

    /// <summary>UDP-era destination reached (Port Unreachable). Retained for compatibility.</summary>
    PortUnreachable = 1,

    /// <summary>Destination unreachable (non-admin).</summary>
    DestinationUnreachable = 2,

    /// <summary>Administratively prohibited.</summary>
    AdministrativelyProhibited = 3,

    /// <summary>No matching ICMP response within the per-probe timeout.</summary>
    TimedOut = 4,

    /// <summary>Socket, privilege, or other terminal probe failure.</summary>
    Error = 5,

    /// <summary>ICMP Echo Reply — destination reached for Echo-based traceroute.</summary>
    EchoReply = 6
}

/// <summary>
/// Immutable traceroute parameters produced by <see cref="TracerouteCommandParser"/>.
/// </summary>
/// <remarks>
/// For hostnames, <see cref="RequiresResolution"/> is <see langword="true"/> and
/// <see cref="Destination"/> may be <see cref="IPAddress.None"/> until resolution.
/// <see cref="InitialPort"/> is validated for CLI compatibility; the ICMP Echo
/// client ignores non-default values.
/// </remarks>
internal sealed class TracerouteRequest
{
    /// <summary>Selected address family for probing.</summary>
    public required TracerouteAddressFamily Family { get; init; }

    /// <summary>Original destination text (literal or hostname).</summary>
    public required string DestinationInput { get; init; }

    /// <summary>Resolved or literal destination; may be placeholder until resolution.</summary>
    public required IPAddress Destination { get; init; }

    /// <summary>True when DestinationInput is a hostname that must be resolved before tracing.</summary>
    public bool RequiresResolution { get; init; }

    /// <summary>Optional local source bind address.</summary>
    public IPAddress? Source { get; init; }

    /// <summary>Per-probe timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = TracerouteLimits.DefaultTimeoutSeconds;

    /// <summary>Number of probes sent at each TTL / hop limit.</summary>
    public int ProbesPerHop { get; init; } = TracerouteLimits.DefaultProbesPerHop;

    /// <summary>Maximum TTL (IPv4) or hop limit (IPv6).</summary>
    public int MaxTtl { get; init; } = TracerouteLimits.DefaultMaxTtl;

    /// <summary>
    /// Initial UDP-style destination port retained for CLI validation;
    /// not used by ICMP Echo probes.
    /// </summary>
    public int InitialPort { get; init; } = TracerouteLimits.DefaultInitialPort;

    /// <summary>Echo payload length in bytes.</summary>
    public int PayloadBytes { get; init; } = TracerouteLimits.DefaultPayloadBytes;

    /// <summary>
    /// Gets the <see cref="AddressFamily"/> corresponding to <see cref="Family"/>.
    /// </summary>
    public AddressFamily SocketAddressFamily =>
        Family == TracerouteAddressFamily.Ipv6
            ? AddressFamily.InterNetworkV6
            : AddressFamily.InterNetwork;
}

/// <summary>
/// Outcome of one traceroute probe at a given TTL and probe index.
/// </summary>
internal sealed class TracerouteProbeResult
{
    /// <summary>TTL (IPv4) or hop limit (IPv6) used for this probe.</summary>
    public required int Ttl { get; init; }

    /// <summary>1-based probe index within the hop.</summary>
    public required int ProbeIndex { get; init; }

    /// <summary>Probe outcome status.</summary>
    public required TracerouteProbeStatus Status { get; init; }

    /// <summary>Responding hop address when known.</summary>
    public IPAddress? Responder { get; init; }

    /// <summary>Measured round-trip time when a matching packet was received.</summary>
    public TimeSpan? RoundTripTime { get; init; }

    /// <summary>Optional human-readable detail (errors, packet-too-big, etc.).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// When <see langword="true"/>, an Echo Reply (or compatible reachability status)
    /// indicated the destination was reached.
    /// </summary>
    public bool DestinationReached { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the client should stop the entire trace
    /// (unreachable/prohibited/error), even if the destination was not reached.
    /// </summary>
    public bool StopTracing { get; init; }
}

/// <summary>
/// Streams traceroute probe results for a request using ICMP Echo with incrementing TTL.
/// </summary>
internal interface IIcmpTracerouteClient
{
    /// <summary>
    /// Executes a traceroute and yields one <see cref="TracerouteProbeResult"/> per probe.
    /// </summary>
    /// <param name="request">Validated traceroute parameters including destination and limits.</param>
    /// <param name="cancellationToken">
    /// Token observed between probes and on send/receive. Per-probe timeouts use a linked token.
    /// </param>
    /// <returns>
    /// An asynchronous sequence of probe results. Socket-init failures yield a single
    /// <see cref="TracerouteProbeStatus.Error"/> result with <see cref="TracerouteProbeResult.StopTracing"/> set.
    /// </returns>
    IAsyncEnumerable<TracerouteProbeResult> TraceAsync(
        TracerouteRequest request,
        CancellationToken cancellationToken);
}
