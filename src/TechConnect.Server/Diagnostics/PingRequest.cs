// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingRequest.cs
//
// Purpose:
// Defines the parsed ping target kind and immutable PingRequest model used by
// the CLI parser and ICMP / session ping execution paths.
//
// -----------------------------------------------------------------------------

using System.Net;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Classification of a parsed <c>ping</c> destination.
/// </summary>
internal enum PingTargetKind
{
    /// <summary>IPv4 literal destination.</summary>
    Ipv4 = 1,

    /// <summary>IPv6 literal destination.</summary>
    Ipv6 = 2,

    /// <summary>Connector session ping (<c>ping session &lt;id&gt;</c>).</summary>
    Session = 3,

    /// <summary>Hostname or unresolved destination; resolved before probe construction.</summary>
    Hostname = 4
}

/// <summary>
/// Immutable ping parameters produced by <see cref="PingCommandParser"/>.
/// </summary>
/// <remarks>
/// For hostnames, <see cref="Destination"/> may be <see langword="null"/> until
/// <see cref="IDestinationResolver"/> selects an address. Session pings omit
/// destination and carry <see cref="SessionId"/> instead.
/// </remarks>
internal sealed class PingRequest
{
    /// <summary>Parsed target classification.</summary>
    public required PingTargetKind Kind { get; init; }

    /// <summary>Original destination text (literal or hostname). Null for session ping.</summary>
    public string? DestinationInput { get; init; }

    /// <summary>
    /// Resolved or literal destination address; null for session ping or until hostname resolution.
    /// </summary>
    public IPAddress? Destination { get; init; }

    /// <summary>Connector session identifier when <see cref="Kind"/> is <see cref="PingTargetKind.Session"/>.</summary>
    public string? SessionId { get; init; }

    /// <summary>Number of probes to send (or per sweep size when sweeping).</summary>
    public int Repeat { get; init; } = PingLimits.DefaultRepeat;

    /// <summary>Per-probe timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = PingLimits.DefaultTimeoutSeconds;

    /// <summary>ICMP payload size in bytes for non-sweep probes.</summary>
    public int PayloadBytes { get; init; } = PingLimits.DefaultPayloadBytes;

    /// <summary>Optional local source address bind for ICMP probes.</summary>
    public IPAddress? Source { get; init; }

    /// <summary>When <see langword="true"/>, sets Don't Fragment on IPv4 probes only.</summary>
    public bool DontFragment { get; init; }

    /// <summary>When <see langword="true"/>, sweeps payload sizes instead of a fixed size.</summary>
    public bool Sweep { get; init; }

    /// <summary>Sweep minimum ICMP payload in bytes.</summary>
    public int SweepMinBytes { get; init; } = PingLimits.DefaultSweepMinBytes;

    /// <summary>Sweep maximum ICMP payload in bytes.</summary>
    public int SweepMaxBytes { get; init; } = PingLimits.DefaultSweepMaxBytes;

    /// <summary>Sweep payload step in bytes.</summary>
    public int SweepIntervalBytes { get; init; } = PingLimits.DefaultSweepIntervalBytes;

    /// <summary>
    /// Gets whether destination resolution is still required before ICMP probing.
    /// </summary>
    /// <value>
    /// <see langword="true"/> for hostname targets, or for IPv4/IPv6 kinds whose
    /// <see cref="Destination"/> is still <see langword="null"/>.
    /// </value>
    public bool RequiresResolution =>
        Kind == PingTargetKind.Hostname ||
        (Kind is PingTargetKind.Ipv4 or PingTargetKind.Ipv6 && Destination is null);
}
