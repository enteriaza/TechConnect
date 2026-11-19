// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SystemNetworkInterfaceInspector.cs
//
// Purpose:
// Enumerates host network interfaces via NetworkInterface.GetAllNetworkInterfaces
// for NOC display, skipping interfaces whose IP properties cannot be read.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TechConnect.Server.Inspection;

/// <summary>
/// <see cref="INetworkInterfaceInspector"/> backed by <see cref="NetworkInterface"/>.
/// </summary>
/// <remarks>
/// Interfaces that throw <see cref="NetworkInformationException"/> when reading
/// IP properties are skipped entirely. Results are ordered by interface name
/// (ordinal ignore-case).
/// </remarks>
internal sealed class SystemNetworkInterfaceInspector : INetworkInterfaceInspector
{
    /// <inheritdoc />
    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces()
    {
        var list = new List<NetworkInterfaceInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                     .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var ipv4 = new List<NetworkAddressInfo>();
            var ipv6 = new List<NetworkAddressInfo>();
            foreach (var uni in props.UnicastAddresses)
            {
                var info = new NetworkAddressInfo
                {
                    Address = uni.Address,
                    PrefixLength = uni.PrefixLength
                };
                if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    ipv4.Add(info);
                }
                else if (uni.Address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    ipv6.Add(info);
                }
            }

            list.Add(new NetworkInterfaceInfo
            {
                Name = nic.Name,
                Description = string.IsNullOrWhiteSpace(nic.Description) ? null : nic.Description,
                Status = nic.OperationalStatus,
                Type = nic.NetworkInterfaceType,
                Ipv4Addresses = ipv4,
                Ipv6Addresses = ipv6
            });
        }

        return list;
    }
}
