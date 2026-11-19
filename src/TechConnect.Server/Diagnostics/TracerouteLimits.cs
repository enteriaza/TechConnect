// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteLimits.cs
//
// Purpose:
// Declares validated numeric bounds and duration/port helpers for traceroute
// CLI requests (timeout, probes, TTL, port, payload, and wall-time estimate).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Validated bounds and helpers for traceroute request construction and validation.
/// </summary>
/// <remarks>
/// <para>
/// CLI validation still accepts <c>port</c> and UDP-oriented payload sizing for
/// syntax compatibility. The live probe client
/// (<see cref="RawSocketIcmpTracerouteClient"/>) sends ICMP Echo with TTL/Hop Limit
/// and ignores a non-default initial port (logged at debug).
/// </para>
/// <para>
/// <see cref="EstimateDurationSeconds"/> is a worst-case wall-time upper bound
/// (<c>maxTtl × probesPerHop × timeoutSeconds</c>), not measured RTT.
/// </para>
/// </remarks>
internal static class TracerouteLimits
{
    /// <summary>Default per-probe timeout in seconds when <c>timeout</c> is omitted.</summary>
    public const int DefaultTimeoutSeconds = 3;

    /// <summary>Minimum allowed per-probe timeout in seconds.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Maximum allowed per-probe timeout in seconds.</summary>
    public const int MaxTimeoutSeconds = 60;

    /// <summary>Default probes per hop when <c>probe</c> is omitted.</summary>
    public const int DefaultProbesPerHop = 3;

    /// <summary>Minimum allowed probes per hop.</summary>
    public const int MinProbesPerHop = 1;

    /// <summary>Maximum allowed probes per hop.</summary>
    public const int MaxProbesPerHop = 10;

    /// <summary>Default maximum TTL / hop limit when <c>ttl</c> is omitted.</summary>
    public const int DefaultMaxTtl = 30;

    /// <summary>Minimum allowed TTL / hop limit.</summary>
    public const int MinTtl = 1;

    /// <summary>Maximum allowed TTL / hop limit.</summary>
    public const int MaxTtl = 64;

    /// <summary>
    /// Default initial UDP destination port retained for CLI compatibility
    /// (ICMP Echo probes do not use this port).
    /// </summary>
    public const int DefaultInitialPort = 33434;

    /// <summary>Minimum allowed initial port value for CLI validation.</summary>
    public const int MinPort = 1024;

    /// <summary>Maximum allowed UDP/TCP port number for CLI validation.</summary>
    public const int MaxPort = 65535;

    /// <summary>Default probe payload bytes when <c>size</c> is omitted.</summary>
    public const int DefaultPayloadBytes = 40;

    /// <summary>Minimum allowed probe payload bytes.</summary>
    public const int MinPayloadBytes = 0;

    /// <summary>Maximum allowed probe payload bytes.</summary>
    public const int MaxPayloadBytes = 1472;

    /// <summary>Reject traces whose worst-case wall time exceeds this many seconds.</summary>
    public const int MaxEstimatedDurationSeconds = 600;

    /// <summary>IPv4 header (20) + UDP header (8) overhead used by size helpers.</summary>
    public const int Ipv4UdpOverheadBytes = 28;

    /// <summary>IPv6 header (40) + UDP header (8) overhead used by size helpers.</summary>
    public const int Ipv6UdpOverheadBytes = 48;

    /// <summary>
    /// Estimates IPv4 packet size as UDP payload plus <see cref="Ipv4UdpOverheadBytes"/>.
    /// </summary>
    /// <param name="payloadBytes">UDP-oriented payload length in bytes.</param>
    /// <returns>Estimated IP packet length in bytes.</returns>
    public static int EstimateIpv4PacketBytes(int payloadBytes) =>
        Ipv4UdpOverheadBytes + payloadBytes;

    /// <summary>
    /// Estimates IPv6 packet size as UDP payload plus <see cref="Ipv6UdpOverheadBytes"/>.
    /// </summary>
    /// <param name="payloadBytes">UDP-oriented payload length in bytes.</param>
    /// <returns>Estimated IP packet length in bytes.</returns>
    public static int EstimateIpv6PacketBytes(int payloadBytes) =>
        Ipv6UdpOverheadBytes + payloadBytes;

    /// <summary>
    /// Computes worst-case duration as <c>maxTtl × probesPerHop × timeoutSeconds</c>.
    /// </summary>
    /// <param name="maxTtl">Maximum TTL / hop limit.</param>
    /// <param name="probesPerHop">Probes sent at each hop.</param>
    /// <param name="timeoutSeconds">Per-probe timeout in seconds.</param>
    /// <returns>Estimated maximum wall time in seconds.</returns>
    /// <exception cref="OverflowException">The product overflows <see cref="int"/>.</exception>
    public static int EstimateDurationSeconds(int maxTtl, int probesPerHop, int timeoutSeconds) =>
        checked(maxTtl * probesPerHop * timeoutSeconds);

    /// <summary>
    /// Computes the last destination port in a UDP-style progression
    /// <c>initialPort + (maxTtl × probesPerHop) − 1</c> for CLI validation.
    /// </summary>
    /// <param name="initialPort">Starting destination port.</param>
    /// <param name="maxTtl">Maximum TTL / hop limit.</param>
    /// <param name="probesPerHop">Probes per hop.</param>
    /// <returns>Last port number in the progression.</returns>
    /// <exception cref="OverflowException">The progression overflows <see cref="int"/>.</exception>
    public static int LastDestinationPort(int initialPort, int maxTtl, int probesPerHop) =>
        checked(initialPort + (maxTtl * probesPerHop) - 1);
}
