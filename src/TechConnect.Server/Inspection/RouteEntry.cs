// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RouteEntry.cs
//
// Purpose:
// Defines routing table entry/snapshot models, IRoutingTableInspector, and
// deterministic RouteEntryOrdering for NOC route display.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Inspection;

/// <summary>
/// One IPv4 or IPv6 route from a platform routing table inspector.
/// </summary>
internal sealed class RouteEntry
{
    /// <summary>Address family of the destination prefix.</summary>
    public required AddressFamily Family { get; init; }

    /// <summary>Destination prefix address.</summary>
    public required IPAddress Destination { get; init; }

    /// <summary>Destination prefix length in bits.</summary>
    public required byte PrefixLength { get; init; }

    /// <summary>Next-hop gateway; null when on-link.</summary>
    public IPAddress? NextHop { get; init; }

    /// <summary>OS interface index when known.</summary>
    public int? InterfaceIndex { get; init; }

    /// <summary>Resolved interface name when the index map provides one.</summary>
    public string? InterfaceName { get; init; }

    /// <summary>Route metric / priority when the platform reports one.</summary>
    public int? Metric { get; init; }

    /// <summary>Platform protocol label when available (Windows MIB protocol names).</summary>
    public string? Protocol { get; init; }

    /// <summary>Whether this is a default route (prefix length 0 to an unspecified destination).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Whether the route is on-link (no IP next hop).</summary>
    public bool IsOnLink { get; init; }
}

/// <summary>
/// Result of a routing table inspection: routes or an unavailable reason.
/// </summary>
internal sealed class RoutingTableSnapshot
{
    /// <summary>Ordered routes when inspection succeeded.</summary>
    public IReadOnlyList<RouteEntry> Routes { get; init; } = [];

    /// <summary>
    /// When set, inspection failed or is unsupported; <see cref="Routes"/> should be treated as empty.
    /// </summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// Reads the host routing table via a platform-specific implementation.
/// </summary>
internal interface IRoutingTableInspector
{
    /// <summary>
    /// Returns the current routing table snapshot, or an unavailable reason.
    /// </summary>
    /// <returns>Snapshot with routes or <see cref="RoutingTableSnapshot.UnavailableReason"/>.</returns>
    RoutingTableSnapshot GetRoutes();
}

/// <summary>
/// Deterministic ordering for route display: IPv4 then IPv6, destination, prefix, metric, next hop.
/// </summary>
internal static class RouteEntryOrdering
{
    /// <summary>
    /// Sorts routes for stable NOC output.
    /// </summary>
    /// <param name="routes">Routes to order.</param>
    /// <returns>New ordered array.</returns>
    public static IReadOnlyList<RouteEntry> Sort(IEnumerable<RouteEntry> routes) =>
        routes
            .OrderBy(r => r.Family == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(r => r.Destination.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r.PrefixLength)
            .ThenBy(r => r.Metric ?? int.MaxValue)
            .ThenBy(r => r.NextHop?.ToString() ?? string.Empty, StringComparer.Ordinal)
            .ToArray();
}
