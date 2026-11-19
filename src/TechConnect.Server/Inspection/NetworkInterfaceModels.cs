// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    NetworkInterfaceModels.cs
//
// Purpose:
// Defines network interface and address models plus INetworkInterfaceInspector
// for NOC interface listing.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TechConnect.Server.Inspection;

/// <summary>
/// One unicast address with optional prefix length from the OS interface inventory.
/// </summary>
internal sealed class NetworkAddressInfo
{
    /// <summary>Unicast IP address.</summary>
    public required IPAddress Address { get; init; }

    /// <summary>Prefix length when reported by the OS; may be null.</summary>
    public int? PrefixLength { get; init; }
}

/// <summary>
/// Snapshot of a single network interface and its unicast addresses.
/// </summary>
internal sealed class NetworkInterfaceInfo
{
    /// <summary>OS interface name.</summary>
    public required string Name { get; init; }

    /// <summary>Optional OS description.</summary>
    public string? Description { get; init; }

    /// <summary>Operational status from <see cref="NetworkInterface.OperationalStatus"/>.</summary>
    public required OperationalStatus Status { get; init; }

    /// <summary>Interface type from <see cref="NetworkInterface.NetworkInterfaceType"/>.</summary>
    public required NetworkInterfaceType Type { get; init; }

    /// <summary>IPv4 unicast addresses on this interface.</summary>
    public IReadOnlyList<NetworkAddressInfo> Ipv4Addresses { get; init; } = [];

    /// <summary>IPv6 unicast addresses on this interface.</summary>
    public IReadOnlyList<NetworkAddressInfo> Ipv6Addresses { get; init; } = [];
}

/// <summary>
/// Enumerates host network interfaces for NOC display.
/// </summary>
internal interface INetworkInterfaceInspector
{
    /// <summary>
    /// Returns all network interfaces ordered by name (case-insensitive), skipping
    /// interfaces whose IP properties cannot be read.
    /// </summary>
    /// <returns>Interface snapshots (may be empty).</returns>
    IReadOnlyList<NetworkInterfaceInfo> GetInterfaces();
}
