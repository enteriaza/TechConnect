// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    IPv6NetworkAvailability.cs
//
// Purpose:
// Detects whether the host has usable global IPv6 (address + non-link-local
// default gateway) and enumerates local IPv4 / global IPv6 unicast addresses.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Detects whether the host has a usable global IPv6 address and an IPv6 default gateway.
/// </summary>
/// <remarks>
/// Link-local alone is insufficient. Unique-local (<c>fc00::/7</c>) is not treated as global.
/// Used by destination selection so hostnames do not silently pick IPv6 when the host cannot use it.
/// </remarks>
internal interface IIPv6NetworkAvailability
{
    /// <summary>
    /// Gets whether at least one up, non-loopback/non-tunnel interface has a global
    /// unicast IPv6 address and a non-link-local IPv6 default gateway.
    /// </summary>
    bool IsIpv6PingAvailable { get; }

    /// <summary>
    /// Returns distinct local IPv4 unicast addresses on up, non-loopback/non-tunnel interfaces,
    /// ordered by ordinal string form.
    /// </summary>
    /// <returns>Sorted distinct IPv4 addresses (may be empty).</returns>
    IReadOnlyList<IPAddress> GetLocalIpv4Addresses();

    /// <summary>
    /// Returns distinct local global IPv6 unicast addresses that meet the usable-endpoint criteria,
    /// ordered by ordinal string form.
    /// </summary>
    /// <returns>Sorted distinct global IPv6 addresses (may be empty).</returns>
    IReadOnlyList<IPAddress> GetLocalGlobalIpv6Addresses();
}

/// <summary>
/// <see cref="IIPv6NetworkAvailability"/> implementation using <see cref="NetworkInterface"/>.
/// </summary>
/// <remarks>
/// <see cref="IsIpv6PingAvailable"/> catches all exceptions from enumeration and returns
/// <see langword="false"/> rather than throwing.
/// </remarks>
internal sealed class SystemIPv6NetworkAvailability : IIPv6NetworkAvailability
{
    /// <inheritdoc />
    public bool IsIpv6PingAvailable
    {
        get
        {
            try
            {
                return GetUsableIpv6Endpoints().Count > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetLocalIpv4Addresses()
    {
        var list = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(uni.Address))
                {
                    list.Add(uni.Address);
                }
            }
        }

        return list
            .Distinct()
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetLocalGlobalIpv6Addresses() =>
        GetUsableIpv6Endpoints()
            .Select(e => e.Address)
            .Distinct()
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Enumerates usable IPv6 endpoints: up NIC with IPv6 gateway (non-link-local)
    /// and global unicast address in <c>2000::/3</c>.
    /// </summary>
    /// <returns>List of address/NIC pairs (may be empty).</returns>
    private static List<(IPAddress Address, NetworkInterface Nic)> GetUsableIpv6Endpoints()
    {
        var result = new List<(IPAddress, NetworkInterface)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            var props = nic.GetIPProperties();
            var hasIpv6Gateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !g.Address.IsIPv6LinkLocal);

            if (!hasIpv6Gateway)
            {
                continue;
            }

            foreach (var uni in props.UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetworkV6)
                {
                    continue;
                }

                if (uni.Address.IsIPv6LinkLocal ||
                    uni.Address.IsIPv6SiteLocal ||
                    IPAddress.IsLoopback(uni.Address) ||
                    uni.Address.IsIPv6Multicast)
                {
                    continue;
                }

                // Require global unicast (2000::/3). Unique-local (fc00::/7) is not "global".
                if (!IsGlobalUnicast(uni.Address))
                {
                    continue;
                }

                result.Add((uni.Address, nic));
            }
        }

        return result;
    }

    /// <summary>
    /// Returns whether <paramref name="address"/> is IPv6 global unicast (<c>2000::/3</c>).
    /// </summary>
    /// <param name="address">Address to test.</param>
    /// <returns><see langword="true"/> when the first three bits are <c>001</c>.</returns>
    internal static bool IsGlobalUnicast(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        // Global unicast: 2000::/3 → first three bits 001
        return (bytes[0] & 0xE0) == 0x20;
    }
}
