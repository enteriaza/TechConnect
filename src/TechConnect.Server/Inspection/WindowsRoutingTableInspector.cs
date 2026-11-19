// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    WindowsRoutingTableInspector.cs
//
// Purpose:
// Reads the Windows IPv4/IPv6 forwarding table via GetIpForwardTable2 /
// FreeMibTable, owning the native MIB table until FreeMibTable in finally.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Inspection;

/// <summary>
/// Reads the Windows IPv4/IPv6 forwarding table via <c>GetIpForwardTable2</c> / <c>FreeMibTable</c>.
/// </summary>
/// <remarks>
/// Windows-only (<see cref="SupportedOSPlatformAttribute"/>). On non-Windows hosts the
/// method returns an unavailable reason without calling native APIs. The native MIB table
/// pointer is owned until <see cref="IWindowsIpForwardTableApi.FreeMibTable"/> in a
/// <c>finally</c> block. Parser layout is verified for Windows MIB row stride 104.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRoutingTableInspector : IRoutingTableInspector
{
    /// <summary>Logger for unexpected failures.</summary>
    private readonly ILogger<WindowsRoutingTableInspector> _logger;

    /// <summary>Native / fake IP forward table API.</summary>
    private readonly IWindowsIpForwardTableApi _api;

    /// <summary>
    /// Initializes with the system <c>iphlpapi</c> API.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public WindowsRoutingTableInspector(ILogger<WindowsRoutingTableInspector> logger)
        : this(logger, SystemWindowsIpForwardTableApi.Instance)
    {
    }

    /// <summary>
    /// Initializes with an injectable API (tests).
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="api">Forward table API.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="api"/> is null.</exception>
    internal WindowsRoutingTableInspector(
        ILogger<WindowsRoutingTableInspector> logger,
        IWindowsIpForwardTableApi api)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <inheritdoc />
    public RoutingTableSnapshot GetRoutes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RoutingTableSnapshot
            {
                UnavailableReason = "Routing table inspection is only implemented on Windows."
            };
        }

        try
        {
            var err = _api.GetIpForwardTable2(0 /* AF_UNSPEC */, out var table);
            if (err != 0 || table == IntPtr.Zero)
            {
                return new RoutingTableSnapshot
                {
                    UnavailableReason = $"GetIpForwardTable2 failed with error {err}."
                };
            }

            try
            {
                var indexNames = WindowsIpForwardTableParser.BuildInterfaceIndexMap();
                var routes = WindowsIpForwardTableParser.ParseTable(table, indexNames);
                return new RoutingTableSnapshot
                {
                    Routes = RouteEntryOrdering.Sort(routes)
                };
            }
            finally
            {
                _api.FreeMibTable(table);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Windows routing table");
            return new RoutingTableSnapshot
            {
                UnavailableReason = "Routing table is unavailable: " + ex.Message
            };
        }
    }
}

/// <summary>
/// Managed parser for <c>MIB_IPFORWARD_TABLE2</c> rows. Platform-agnostic so unit tests can run anywhere.
/// </summary>
/// <remarks>
/// Layout verified on Windows: header 4 bytes, row stride 104, destination family at row+16.
/// Does not free native memory; the caller owns the table pointer.
/// </remarks>
internal static class WindowsIpForwardTableParser
{
    /// <summary>Size of the <c>NumEntries</c> table header.</summary>
    internal const int TableHeaderSize = 4;

    /// <summary>Stride of each <c>MIB_IPFORWARD_ROW2</c> as used by this parser.</summary>
    internal const int RowSize = 104;

    /// <summary>Offset of interface index within a row.</summary>
    internal const int InterfaceIndexOffset = 12;

    /// <summary>Offset of destination <c>SOCKADDR_INET</c> within a row.</summary>
    internal const int DestinationOffset = 16;

    /// <summary>Offset of prefix length within a row.</summary>
    internal const int PrefixLengthOffset = 44;

    /// <summary>Offset of next-hop <c>SOCKADDR_INET</c> within a row.</summary>
    internal const int NextHopOffset = 48;

    /// <summary>Offset of metric within a row.</summary>
    internal const int MetricOffset = 88;

    /// <summary>Offset of protocol within a row.</summary>
    internal const int ProtocolOffset = 92;

    /// <summary>
    /// Parses all rows from a native <c>MIB_IPFORWARD_TABLE2</c> pointer.
    /// </summary>
    /// <param name="table">Native table pointer (not freed here).</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>Parsed routes (may be empty).</returns>
    internal static List<RouteEntry> ParseTable(
        IntPtr table,
        IReadOnlyDictionary<int, string> indexNames)
    {
        var list = new List<RouteEntry>();
        if (table == IntPtr.Zero)
        {
            return list;
        }

        var numEntries = Marshal.ReadInt32(table);
        if (numEntries <= 0)
        {
            return list;
        }

        for (var i = 0; i < numEntries; i++)
        {
            var row = table + TableHeaderSize + (i * RowSize);
            var entry = ParseRow(row, indexNames);
            if (entry is not null)
            {
                list.Add(entry);
            }
        }

        return list;
    }

    /// <summary>
    /// Parses one forward-table row into a <see cref="RouteEntry"/>, or null for unsupported families.
    /// </summary>
    /// <param name="row">Pointer to the row start.</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>Parsed entry, or null when the destination family is unsupported.</returns>
    internal static RouteEntry? ParseRow(IntPtr row, IReadOnlyDictionary<int, string> indexNames)
    {
        var destFamily = (AddressFamily)Marshal.ReadInt16(row + DestinationOffset);
        if (destFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return null;
        }

        var destination = ReadSockAddrInet(row + DestinationOffset);
        if (destination is null)
        {
            return null;
        }

        var prefixLength = Marshal.ReadByte(row + PrefixLengthOffset);
        var nextHop = ReadSockAddrInet(row + NextHopOffset);
        var ifIndex = Marshal.ReadInt32(row + InterfaceIndexOffset);
        var metric = Marshal.ReadInt32(row + MetricOffset);
        var protocol = Marshal.ReadInt32(row + ProtocolOffset);

        var onLink = IsUnspecified(nextHop);
        var isDefault = prefixLength == 0 && IsUnspecified(destination);

        indexNames.TryGetValue(ifIndex, out var ifName);

        return new RouteEntry
        {
            Family = destFamily,
            Destination = destination,
            PrefixLength = prefixLength,
            NextHop = onLink ? null : nextHop,
            InterfaceIndex = ifIndex,
            InterfaceName = ifName,
            Metric = metric,
            Protocol = DescribeProtocol(protocol),
            IsDefault = isDefault,
            IsOnLink = onLink
        };
    }

    /// <summary>
    /// Builds an interface index → name map from <see cref="NetworkInterface"/> IPv4/IPv6 properties.
    /// </summary>
    /// <returns>Index map (may be empty; property failures are skipped).</returns>
    internal static Dictionary<int, string> BuildInterfaceIndexMap()
    {
        var map = new Dictionary<int, string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = nic.GetIPProperties();
                var v4 = props.GetIPv4Properties()?.Index;
                if (v4 is int v4Index)
                {
                    map[v4Index] = nic.Name;
                }

                var v6 = props.GetIPv6Properties()?.Index;
                if (v6 is int v6Index)
                {
                    map[v6Index] = nic.Name;
                }
            }
            catch (NetworkInformationException)
            {
            }
        }

        return map;
    }

    /// <summary>Reads an IPv4 or IPv6 address from a <c>SOCKADDR_INET</c>-shaped buffer.</summary>
    /// <param name="sock">Pointer to the sockaddr structure.</param>
    /// <returns>Parsed address, or null for unsupported families.</returns>
    private static IPAddress? ReadSockAddrInet(IntPtr sock)
    {
        var family = (AddressFamily)Marshal.ReadInt16(sock);
        if (family == AddressFamily.InterNetwork)
        {
            var bytes = new byte[4];
            Marshal.Copy(sock + 4, bytes, 0, 4);
            return new IPAddress(bytes);
        }

        if (family == AddressFamily.InterNetworkV6)
        {
            var bytes = new byte[16];
            Marshal.Copy(sock + 8, bytes, 0, 16);
            return new IPAddress(bytes);
        }

        return null;
    }

    /// <summary>Returns whether <paramref name="address"/> is null or IPv4/IPv6 unspecified.</summary>
    /// <param name="address">Address to test.</param>
    /// <returns><see langword="true"/> when unspecified / on-link next hop.</returns>
    private static bool IsUnspecified(IPAddress? address) =>
        address is null ||
        address.Equals(IPAddress.Any) ||
        address.Equals(IPAddress.IPv6Any);

    /// <summary>Maps a MIB protocol integer to a short label, or the numeric string.</summary>
    /// <param name="protocol">MIB protocol value.</param>
    /// <returns>Protocol label.</returns>
    private static string DescribeProtocol(int protocol) => protocol switch
    {
        1 => "other",
        2 => "local",
        3 => "netmgmt",
        4 => "icmp",
        5 => "egp",
        6 => "ggp",
        7 => "hello",
        8 => "rip",
        9 => "is-is",
        10 => "es-is",
        11 => "cisco",
        12 => "bbn",
        13 => "ospf",
        14 => "bgp",
        15 => "idpr",
        16 => "eigrp",
        17 => "dvmrp",
        18 => "rpl",
        19 => "dhcp",
        _ => protocol.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// Abstraction over <c>GetIpForwardTable2</c> / <c>FreeMibTable</c> for testability.
/// </summary>
/// <remarks>
/// Callers that receive a non-zero table pointer must call <see cref="FreeMibTable"/> exactly once.
/// </remarks>
internal interface IWindowsIpForwardTableApi
{
    /// <summary>
    /// Retrieves the IP forward table for <paramref name="family"/> (0 = AF_UNSPEC).
    /// </summary>
    /// <param name="family">Address family filter.</param>
    /// <param name="table">Native table pointer on success.</param>
    /// <returns>Win32 error code (0 = success).</returns>
    int GetIpForwardTable2(ushort family, out IntPtr table);

    /// <summary>Frees a table previously returned by <see cref="GetIpForwardTable2"/>.</summary>
    /// <param name="memory">Native table pointer.</param>
    void FreeMibTable(IntPtr memory);
}

/// <summary>
/// Production P/Invoke binding to <c>iphlpapi.dll</c>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SystemWindowsIpForwardTableApi : IWindowsIpForwardTableApi
{
    /// <summary>Shared singleton instance.</summary>
    public static SystemWindowsIpForwardTableApi Instance { get; } = new();

    /// <inheritdoc />
    public int GetIpForwardTable2(ushort family, out IntPtr table) =>
        NativeGetIpForwardTable2(family, out table);

    /// <inheritdoc />
    public void FreeMibTable(IntPtr memory) => NativeFreeMibTable(memory);
    /// <summary>
    /// P/Invoke for <c>GetIpForwardTable2</c>: allocates a <c>MIB_IPFORWARD_TABLE2</c>.
    /// </summary>
    /// <param name="family">Address family filter (0 = <c>AF_UNSPEC</c> for IPv4 and IPv6).</param>
    /// <param name="table">Receives the native table pointer on success; must be freed with <c>FreeMibTable</c>.</param>
    /// <returns>Win32 error code (0 = <c>NO_ERROR</c>).</returns>
    [DllImport("iphlpapi.dll", EntryPoint = "GetIpForwardTable2", ExactSpelling = true)]
    private static extern int NativeGetIpForwardTable2(ushort family, out IntPtr table);

    /// <summary>
    /// P/Invoke for <c>FreeMibTable</c>: releases memory returned by <c>GetIpForwardTable2</c>.
    /// </summary>
    /// <param name="memory">Native table pointer previously returned by <c>GetIpForwardTable2</c>.</param>
    [DllImport("iphlpapi.dll", EntryPoint = "FreeMibTable", ExactSpelling = true)]
    private static extern void NativeFreeMibTable(IntPtr memory);
}
