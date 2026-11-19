// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PingLimits.cs
//
// Purpose:
// Declares validated numeric bounds and IP/ICMP overhead helpers for ICMP and
// session ping diagnostics (repeat, timeout, payload, and sweep probes).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Validated bounds and packet-size helpers for ICMP and session ping diagnostics.
/// </summary>
/// <remarks>
/// Sweep probe counts are capped by <see cref="MaxSweepProbes"/> as
/// <c>((max − min) / interval + 1) × repeat</c>. Overhead constants estimate
/// IP + ICMP header bytes only; they are not path-MTU claims.
/// </remarks>
internal static class PingLimits
{
    /// <summary>Default number of echo probes when <c>repeat</c> is omitted.</summary>
    public const int DefaultRepeat = 5;

    /// <summary>Minimum allowed <c>repeat</c> value.</summary>
    public const int MinRepeat = 1;

    /// <summary>Maximum allowed <c>repeat</c> value.</summary>
    public const int MaxRepeat = 100;

    /// <summary>Default per-probe timeout in seconds when <c>timeout</c> is omitted.</summary>
    public const int DefaultTimeoutSeconds = 1;

    /// <summary>Minimum allowed per-probe timeout in seconds.</summary>
    public const int MinTimeoutSeconds = 1;

    /// <summary>Maximum allowed per-probe timeout in seconds.</summary>
    public const int MaxTimeoutSeconds = 60;

    /// <summary>Default ICMP payload size in bytes when <c>size</c> is omitted.</summary>
    public const int DefaultPayloadBytes = 56;

    /// <summary>Minimum allowed ICMP payload size in bytes.</summary>
    public const int MinPayloadBytes = 0;

    /// <summary>Maximum allowed ICMP payload size in bytes.</summary>
    public const int MaxPayloadBytes = 9000;

    /// <summary>Default sweep minimum payload when <c>sweep</c> omits <c>min</c>.</summary>
    public const int DefaultSweepMinBytes = 100;

    /// <summary>Default sweep maximum payload when <c>sweep</c> omits <c>max</c>.</summary>
    public const int DefaultSweepMaxBytes = 1500;

    /// <summary>Default sweep step when <c>sweep</c> omits <c>interval</c>.</summary>
    public const int DefaultSweepIntervalBytes = 100;

    /// <summary>
    /// Maximum total probes for a sweep session (<c>steps × repeat</c>).
    /// </summary>
    public const int MaxSweepProbes = 200;

    /// <summary>IPv4 header (20) + ICMP header (8) overhead used for size estimates.</summary>
    public const int Ipv4IcmpOverheadBytes = 28;

    /// <summary>IPv6 header (40) + ICMPv6 header (8) overhead used for size estimates.</summary>
    public const int Ipv6IcmpOverheadBytes = 48;

    /// <summary>
    /// Estimates IPv4 packet size as ICMP payload plus <see cref="Ipv4IcmpOverheadBytes"/>.
    /// </summary>
    /// <param name="payloadBytes">ICMP payload length in bytes.</param>
    /// <returns>Estimated IP packet length in bytes.</returns>
    public static int EstimateIpv4PacketBytes(int payloadBytes) =>
        Ipv4IcmpOverheadBytes + payloadBytes;

    /// <summary>
    /// Estimates IPv6 packet size as ICMPv6 payload plus <see cref="Ipv6IcmpOverheadBytes"/>.
    /// </summary>
    /// <param name="payloadBytes">ICMPv6 payload length in bytes.</param>
    /// <returns>Estimated IP packet length in bytes.</returns>
    public static int EstimateIpv6PacketBytes(int payloadBytes) =>
        Ipv6IcmpOverheadBytes + payloadBytes;
}
