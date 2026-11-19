// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    MacRoutingTableInspector.cs
//
// Purpose:
// Reads macOS IPv4/IPv6 routes via sysctl NET_RT_DUMP, owning the temporary
// sysctl buffer allocated for the dump.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Inspection;

/// <summary>
/// Reads macOS IPv4/IPv6 routes via <c>sysctl</c> <c>NET_RT_DUMP</c>.
/// </summary>
/// <remarks>
/// macOS-only. On non-macOS hosts returns an unavailable reason without calling sysctl.
/// The production API allocates a temporary unmanaged buffer for the dump and frees it
/// before returning managed bytes. Metric/protocol are not populated (left null).
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class MacRoutingTableInspector : IRoutingTableInspector
{
    /// <summary>
    /// Address family <c>AF_ROUTE</c> (17); same numeric value as <c>PF_ROUTE</c> on macOS.
    /// Present for documentation; the sysctl MIB uses <c>PF_ROUTE</c>.
    /// </summary>
    private const int AF_ROUTE = 17;

    /// <summary>
    /// Protocol family <c>PF_ROUTE</c> (17): second MIB component for routing-table sysctl.
    /// </summary>
    private const int PF_ROUTE = 17;

    /// <summary>
    /// Sysctl name component <c>NET_RT_DUMP</c> (1): dump the entire routing table.
    /// </summary>
    private const int NET_RT_DUMP = 1;

    /// <summary>
    /// Sysctl top-level name <c>CTL_NET</c> (4): network subsystem.
    /// </summary>
    private const int CTL_NET = 4;

    /// <summary>
    /// Expected <c>rt_msghdr.rtm_version</c> (5); other versions are skipped.
    /// </summary>
    private const int RTM_VERSION = 5;

    /// <summary>
    /// <c>rtm_addrs</c> bit <c>RTA_DST</c> (0x1): destination <c>sockaddr</c> follows the header.
    /// </summary>
    private const int RTA_DST = 0x1;

    /// <summary>
    /// <c>rtm_addrs</c> bit <c>RTA_GATEWAY</c> (0x2): gateway / next-hop <c>sockaddr</c>.
    /// </summary>
    private const int RTA_GATEWAY = 0x2;

    /// <summary>
    /// <c>rtm_addrs</c> bit <c>RTA_NETMASK</c> (0x4): netmask used to derive prefix length.
    /// </summary>
    private const int RTA_NETMASK = 0x4;

    /// <summary>
    /// <c>rtm_addrs</c> bit <c>RTA_IFP</c> (0x10): interface name sockaddr; skipped when walking addrs.
    /// Interface index comes from <c>rt_msghdr.rtm_index</c> instead.
    /// </summary>
    private const int RTA_IFP = 0x10;

    /// <summary>Logger for unexpected failures.</summary>
    private readonly ILogger<MacRoutingTableInspector> _logger;

    /// <summary>sysctl dump API (system or fake).</summary>
    private readonly IMacRouteSysctlApi _api;

    /// <summary>Initializes with the system sysctl API.</summary>
    /// <param name="logger">Logger instance.</param>
    public MacRoutingTableInspector(ILogger<MacRoutingTableInspector> logger)
        : this(logger, SystemMacRouteSysctlApi.Instance)
    {
    }

    /// <summary>Initializes with an injectable API (tests).</summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="api">sysctl route API.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="api"/> is null.</exception>
    internal MacRoutingTableInspector(
        ILogger<MacRoutingTableInspector> logger,
        IMacRouteSysctlApi api)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <inheritdoc />
    public RoutingTableSnapshot GetRoutes()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new RoutingTableSnapshot
            {
                UnavailableReason = "macOS routing inspection is only available on macOS."
            };
        }

        try
        {
            var payload = _api.DumpRoutes();
            var indexNames = BuildInterfaceIndexMap();
            var routes = ParseRouteDump(payload, indexNames);
            return new RoutingTableSnapshot
            {
                Routes = RouteEntryOrdering.Sort(routes)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read macOS routing table");
            return new RoutingTableSnapshot
            {
                UnavailableReason = "Routing table is unavailable: " + ex.Message
            };
        }
    }

    /// <summary>
    /// Parses a <c>NET_RT_DUMP</c> buffer of <c>rt_msghdr</c> messages into route entries.
    /// </summary>
    /// <param name="buffer">Raw sysctl dump bytes.</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>Parsed routes (may be empty).</returns>
    internal static List<RouteEntry> ParseRouteDump(
        ReadOnlySpan<byte> buffer,
        IReadOnlyDictionary<int, string> indexNames)
    {
        var list = new List<RouteEntry>();
        var offset = 0;
        while (offset + 92 <= buffer.Length)
        {
            // struct rt_msghdr
            var msglen = BinaryPrimitives.ReadUInt16LittleEndian(buffer[offset..]);
            if (msglen < 92 || offset + msglen > buffer.Length)
            {
                break;
            }

            var version = buffer[offset + 2];
            var type = buffer[offset + 3];
            if (version == RTM_VERSION && type == 0x1 /* RTM_ADD/GET dump entries use RTM_GET=0x4 or ADD */)
            {
                // Accept RTM_ADD (0x1), RTM_GET (0x4), RTM_CHANGE (0x2) style messages in dumps.
            }

            if (version == RTM_VERSION)
            {
                var addrs = BinaryPrimitives.ReadInt32LittleEndian(buffer[(offset + 12)..]);
                var index = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 8)..]);
                var payload = buffer.Slice(offset + 92, msglen - 92);
                var entry = ParseAddresses(payload, addrs, index, indexNames);
                if (entry is not null)
                {
                    list.Add(entry);
                }
            }

            offset += msglen;
            if (msglen == 0)
            {
                break;
            }
        }

        return list;
    }
    /// <summary>
    /// Walks packed <c>sockaddr</c>s after <c>rt_msghdr</c> according to <paramref name="addrsMask"/>.
    /// </summary>
    /// <param name="payload">Bytes immediately after the 92-byte <c>rt_msghdr</c>.</param>
    /// <param name="addrsMask"><c>rtm_addrs</c> bitmask indicating which RTA_* sockaddrs are present.</param>
    /// <param name="ifIndex"><c>rtm_index</c> interface index for name lookup.</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>
    /// A <see cref="RouteEntry"/> for IPv4/IPv6 destinations; <see langword="null"/> when the
    /// family is unsupported. Link-layer gateways are treated as on-link (next hop cleared).
    /// </returns>
    private static RouteEntry? ParseAddresses(
        ReadOnlySpan<byte> payload,
        int addrsMask,
        int ifIndex,
        IReadOnlyDictionary<int, string> indexNames)
    {
        IPAddress? destination = null;
        IPAddress? gateway = null;
        byte? prefixLength = null;
        AddressFamily? family = null;
        var cursor = 0;

        for (var bit = 0; bit < 8; bit++)
        {
            var flag = 1 << bit;
            if ((addrsMask & flag) == 0)
            {
                continue;
            }

            if (cursor >= payload.Length)
            {
                break;
            }

            var saLen = payload[cursor];
            if (saLen == 0)
            {
                saLen = 4; // sockaddr minimum rounded
            }

            if (cursor + saLen > payload.Length)
            {
                break;
            }

            var sa = payload.Slice(cursor, saLen);
            var saFamily = sa.Length > 1 ? MapBsdFamily(sa[1]) : null;

            if (flag == RTA_DST)
            {
                destination = ReadSockAddr(sa, saFamily);
                family = destination?.AddressFamily;
            }
            else if (flag == RTA_GATEWAY)
            {
                gateway = ReadSockAddr(sa, saFamily);
            }
            else if (flag == RTA_NETMASK)
            {
                prefixLength = MaskToPrefix(sa, saFamily);
            }

            cursor += AlignSockaddr(saLen);
        }

        family ??= destination?.AddressFamily;
        if (family is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return null;
        }

        destination ??= family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        var prefix = prefixLength ?? (IsUnspecified(destination) ? (byte)0 : family == AddressFamily.InterNetwork ? (byte)32 : (byte)128);
        const AddressFamily AfLink = (AddressFamily)18; // AF_LINK on BSD/macOS
        var onLink = gateway is null || IsUnspecified(gateway) || gateway.AddressFamily == AfLink;
        // Link-layer gateways are treated as on-link.
        if (gateway is not null && gateway.AddressFamily != family)
        {
            onLink = true;
            gateway = null;
        }

        var isDefault = prefix == 0 && IsUnspecified(destination);
        indexNames.TryGetValue(ifIndex, out var ifName);

        return new RouteEntry
        {
            Family = family.Value,
            Destination = destination,
            PrefixLength = prefix,
            NextHop = onLink ? null : gateway,
            InterfaceIndex = ifIndex,
            InterfaceName = ifName,
            Metric = null,
            Protocol = null,
            IsDefault = isDefault,
            IsOnLink = onLink
        };
    }

    /// <summary>
    /// Maps BSD/macOS <c>sa_family</c> values: <c>AF_INET</c>=2, <c>AF_INET6</c>=30, <c>AF_LINK</c>=18.
    /// </summary>
    /// <param name="family">Raw <c>sockaddr.sa_family</c> byte.</param>
    /// <returns>Mapped <see cref="AddressFamily"/>, or <see langword="null"/> when unsupported.</returns>
    private static AddressFamily? MapBsdFamily(byte family) => family switch
    {
        2 => AddressFamily.InterNetwork,
        30 => AddressFamily.InterNetworkV6,
        18 => (AddressFamily)18,
        _ => null
    };

    /// <summary>
    /// Reads an IPv4 or IPv6 address from a BSD <c>sockaddr</c> layout.
    /// </summary>
    /// <param name="sa">Sockaddr bytes including <c>sa_len</c> / <c>sa_family</c>.</param>
    /// <param name="family">Interpreted address family from <c>sa_family</c>.</param>
    /// <returns>
    /// Parsed address, or <see langword="null"/> when truncated or not IPv4/IPv6
    /// (for example <c>AF_LINK</c> gateways).
    /// </returns>
    private static IPAddress? ReadSockAddr(ReadOnlySpan<byte> sa, AddressFamily? family)
    {
        if (family == AddressFamily.InterNetwork && sa.Length >= 8)
        {
            return new IPAddress(sa.Slice(4, 4));
        }

        if (family == AddressFamily.InterNetworkV6 && sa.Length >= 24)
        {
            return new IPAddress(sa.Slice(8, 16));
        }

        return null;
    }
    /// <summary>
    /// Converts a netmask <c>sockaddr</c> into a CIDR prefix length by counting set bits.
    /// </summary>
    /// <param name="sa">Netmask sockaddr from <c>RTA_NETMASK</c>.</param>
    /// <param name="family">Address family of the mask.</param>
    /// <returns>
    /// Prefix length in bits; 0 when the sockaddr is too short or encodes length only.
    /// </returns>
    private static byte MaskToPrefix(ReadOnlySpan<byte> sa, AddressFamily? family)
    {
        ReadOnlySpan<byte> maskBytes;
        if (family == AddressFamily.InterNetwork && sa.Length >= 8)
        {
            maskBytes = sa.Slice(4, 4);
        }
        else if (family == AddressFamily.InterNetworkV6 && sa.Length >= 24)
        {
            maskBytes = sa.Slice(8, 16);
        }
        else if (sa.Length > 2)
        {
            // Some dumps encode netmask length in sa_len only.
            return 0;
        }
        else
        {
            return 0;
        }

        var bits = 0;
        foreach (var b in maskBytes)
        {
            bits += System.Numerics.BitOperations.PopCount(b);
        }

        return (byte)bits;
    }
    /// <summary>
    /// Aligns a sockaddr length to the BSD 4-byte boundary (minimum 4).
    /// </summary>
    /// <param name="length"><c>sa_len</c> or substituted minimum length.</param>
    /// <returns>Aligned stride to advance the address cursor.</returns>
    private static int AlignSockaddr(int length) => Math.Max(4, (length + 3) & ~3);

    /// <summary>
    /// Returns whether <paramref name="address"/> is IPv4 or IPv6 unspecified.
    /// </summary>
    /// <param name="address">Address to test.</param>
    /// <returns><see langword="true"/> for 0.0.0.0 or ::.</returns>
    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// Builds an interface index → name map from <see cref="NetworkInterface"/>.
    /// </summary>
    /// <returns>Index map; property failures are skipped.</returns>
    private static Dictionary<int, string> BuildInterfaceIndexMap()
    {
        var map = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var index = nic.GetIPProperties().GetIPv4Properties()?.Index
                            ?? nic.GetIPProperties().GetIPv6Properties()?.Index;
                if (index is int value)
                {
                    map[value] = nic.Name;
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return map;
    }
}

/// <summary>
/// Abstraction over macOS <c>sysctl(NET_RT_DUMP)</c> for testability.
/// </summary>
internal interface IMacRouteSysctlApi
{
    /// <summary>
    /// Dumps the routing table and returns managed bytes.
    /// </summary>
    /// <returns>Raw <c>rt_msghdr</c> dump.</returns>
    /// <remarks>Implementations must free any temporary unmanaged buffers they allocate.</remarks>
    byte[] DumpRoutes();
}

/// <summary>
/// Production sysctl dump using libc; owns the temporary <c>AllocHGlobal</c> buffer.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class SystemMacRouteSysctlApi : IMacRouteSysctlApi
{
    /// <summary>Shared singleton instance.</summary>
    public static SystemMacRouteSysctlApi Instance { get; } = new();

    /// <inheritdoc />
    public byte[] DumpRoutes()
    {
        int[] mib = [CTL_NET, PF_ROUTE, 0, 0, NET_RT_DUMP, 0];
        var len = IntPtr.Zero;
        var rc = sysctl(mib, mib.Length, IntPtr.Zero, ref len, IntPtr.Zero, 0);
        if (rc != 0 || len == IntPtr.Zero)
        {
            throw new InvalidOperationException("sysctl(NET_RT_DUMP) size query failed: " + Marshal.GetLastPInvokeError());
        }

        var size = len.ToInt32();
        if (size <= 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var len2 = len;
            rc = sysctl(mib, mib.Length, buffer, ref len2, IntPtr.Zero, 0);
            if (rc != 0)
            {
                throw new InvalidOperationException("sysctl(NET_RT_DUMP) failed: " + Marshal.GetLastPInvokeError());
            }

            var bytes = new byte[len2.ToInt32()];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
    /// <summary>
    /// Sysctl top-level name <c>CTL_NET</c> (4): network subsystem MIB root.
    /// </summary>
    private const int CTL_NET = 4;

    /// <summary>
    /// Protocol family <c>PF_ROUTE</c> (17): routing-table sysctl MIB component.
    /// </summary>
    private const int PF_ROUTE = 17;

    /// <summary>
    /// Sysctl name component <c>NET_RT_DUMP</c> (1): dump all routes.
    /// </summary>
    private const int NET_RT_DUMP = 1;

    /// <summary>
    /// Queries or reads a sysctl MIB (<c>sysctl(3)</c>); used twice for size then data.
    /// </summary>
    /// <param name="name">MIB name array (<c>CTL_NET</c>, <c>PF_ROUTE</c>, …, <c>NET_RT_DUMP</c>).</param>
    /// <param name="namelen">Number of elements in <paramref name="name"/>.</param>
    /// <param name="oldp">Output buffer, or <see cref="IntPtr.Zero"/> for size-only query.</param>
    /// <param name="oldlenp">In/out length of <paramref name="oldp"/>.</param>
    /// <param name="newp">Unused write pointer (<see cref="IntPtr.Zero"/>).</param>
    /// <param name="newlen">Unused write length (0).</param>
    /// <returns>0 on success; non-zero on failure (<c>errno</c> via P/Invoke last error).</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int sysctl(
        int[] name,
        int namelen,
        IntPtr oldp,
        ref IntPtr oldlenp,
        IntPtr newp,
        int newlen);
}
