// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    LinuxRoutingTableInspector.cs
//
// Purpose:
// Reads Linux IPv4/IPv6 routes via rtnetlink (NETLINK_ROUTE / RTM_GETROUTE),
// owning the netlink socket for the duration of DumpRoutes.
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
/// Reads Linux IPv4/IPv6 routes via rtnetlink (<c>NETLINK_ROUTE</c> / <c>RTM_GETROUTE</c>).
/// </summary>
/// <remarks>
/// Linux-only. On non-Linux hosts returns an unavailable reason without opening netlink.
/// The production API owns the netlink socket fd (opened and <c>close</c>d inside
/// <see cref="ILinuxNetlinkRouteApi.DumpRoutes"/>). Broadcast/multicast/blackhole/
/// unreachable/prohibit route types are filtered out by the parser.
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed class LinuxRoutingTableInspector : IRoutingTableInspector
{
    /// <summary>Logger for unexpected failures.</summary>
    private readonly ILogger<LinuxRoutingTableInspector> _logger;

    /// <summary>Netlink dump API (system or fake).</summary>
    private readonly ILinuxNetlinkRouteApi _api;

    /// <summary>Initializes with the system netlink API.</summary>
    /// <param name="logger">Logger instance.</param>
    public LinuxRoutingTableInspector(ILogger<LinuxRoutingTableInspector> logger)
        : this(logger, SystemLinuxNetlinkRouteApi.Instance)
    {
    }

    /// <summary>Initializes with an injectable API (tests).</summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="api">Netlink route API.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> or <paramref name="api"/> is null.</exception>
    internal LinuxRoutingTableInspector(
        ILogger<LinuxRoutingTableInspector> logger,
        ILinuxNetlinkRouteApi api)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <inheritdoc />
    public RoutingTableSnapshot GetRoutes()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new RoutingTableSnapshot
            {
                UnavailableReason = "Linux routing inspection is only available on Linux."
            };
        }

        try
        {
            var payload = _api.DumpRoutes();
            var indexNames = LinuxNetlinkRouteParser.BuildInterfaceIndexMap();
            var routes = LinuxNetlinkRouteParser.ParseNetlinkDump(payload, indexNames);
            return new RoutingTableSnapshot
            {
                Routes = RouteEntryOrdering.Sort(routes)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Linux routing table");
            return new RoutingTableSnapshot
            {
                UnavailableReason = "Routing table is unavailable: " + ex.Message
            };
        }
    }

}

/// <summary>Platform-agnostic rtnetlink route dump parser (testable on any host).</summary>
/// <remarks>Does not own native resources; operates on a managed byte dump.</remarks>
internal static class LinuxNetlinkRouteParser
{
    /// <summary>
    /// Netlink message type <c>NLMSG_DONE</c> (3): end-of-multipart dump; stop parsing.
    /// </summary>
    private const int NLMSG_DONE = 0x3;

    /// <summary>
    /// Netlink message type <c>NLMSG_ERROR</c> (2): error acknowledgment; treated as dump end.
    /// </summary>
    private const int NLMSG_ERROR = 0x2;

    /// <summary>
    /// Rtnetlink message type <c>RTM_NEWROUTE</c> (24): a route entry in dump replies.
    /// </summary>
    private const int RTM_NEWROUTE = 24;

    /// <summary>
    /// Route attribute <c>RTA_DST</c> (1): destination prefix address bytes.
    /// </summary>
    private const int RTA_DST = 1;

    /// <summary>
    /// Route attribute <c>RTA_OIF</c> (4): output interface index (<c>int</c>).
    /// </summary>
    private const int RTA_OIF = 4;

    /// <summary>
    /// Route attribute <c>RTA_GATEWAY</c> (5): next-hop gateway address bytes.
    /// </summary>
    private const int RTA_GATEWAY = 5;

    /// <summary>
    /// Route attribute <c>RTA_PRIORITY</c> (6): route metric / priority (<c>int</c>).
    /// </summary>
    private const int RTA_PRIORITY = 6;

    /// <summary>
    /// Route type <c>RTN_BROADCAST</c> (3): filtered out of the snapshot.
    /// </summary>
    private const int RTN_BROADCAST = 3;

    /// <summary>
    /// Route type <c>RTN_MULTICAST</c> (5): filtered out of the snapshot.
    /// </summary>
    private const int RTN_MULTICAST = 5;

    /// <summary>
    /// Route type <c>RTN_BLACKHOLE</c> (6): filtered out of the snapshot.
    /// </summary>
    private const int RTN_BLACKHOLE = 6;

    /// <summary>
    /// Route type <c>RTN_UNREACHABLE</c> (7): filtered out of the snapshot.
    /// </summary>
    private const int RTN_UNREACHABLE = 7;

    /// <summary>
    /// Route type <c>RTN_PROHIBIT</c> (8): filtered out of the snapshot.
    /// </summary>
    private const int RTN_PROHIBIT = 8;

    /// <summary>
    /// Parses a concatenated rtnetlink dump, stopping at <c>NLMSG_DONE</c> / <c>NLMSG_ERROR</c>.
    /// </summary>
    /// <param name="buffer">Raw netlink messages.</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>Parsed routes (filtered special types omitted).</returns>
    internal static List<RouteEntry> ParseNetlinkDump(
        ReadOnlySpan<byte> buffer,
        IReadOnlyDictionary<int, string> indexNames)
    {
        var list = new List<RouteEntry>();
        var offset = 0;
        while (offset + 16 <= buffer.Length)
        {
            var nlLen = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (nlLen < 16 || offset + nlLen > buffer.Length)
            {
                break;
            }

            var nlType = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 4)..]);
            if (nlType is NLMSG_DONE or NLMSG_ERROR)
            {
                break;
            }

            if (nlType == RTM_NEWROUTE && nlLen >= 16 + 12)
            {
                var entry = ParseRouteMessage(buffer.Slice(offset + 16, nlLen - 16), indexNames);
                if (entry is not null)
                {
                    list.Add(entry);
                }
            }

            offset += Align(nlLen);
        }

        return list;
    }

    /// <summary>Builds an interface index → name map from <see cref="NetworkInterface"/>.</summary>
    /// <returns>Index map (property failures skipped).</returns>
    internal static Dictionary<int, string> BuildInterfaceIndexMap()
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

    /// <summary>Parses one <c>RTM_NEWROUTE</c> payload into a <see cref="RouteEntry"/>.</summary>
    /// <param name="msg">Message body after the nlmsghdr.</param>
    /// <param name="indexNames">Interface index → name map.</param>
    /// <returns>Parsed entry, or null when filtered/unsupported.</returns>
    private static RouteEntry? ParseRouteMessage(
        ReadOnlySpan<byte> msg,
        IReadOnlyDictionary<int, string> indexNames)
    {
        if (msg.Length < 12)
        {
            return null;
        }

        var family = MapLinuxFamily(msg[0]);
        if (family is null)
        {
            return null;
        }

        var dstLen = msg[1];
        var type = msg[6];
        if (type is RTN_BROADCAST or RTN_MULTICAST or RTN_BLACKHOLE or RTN_UNREACHABLE or RTN_PROHIBIT)
        {
            return null;
        }

        IPAddress? destination = null;
        IPAddress? gateway = null;
        int? oif = null;
        int? priority = null;

        var attrOffset = 12;
        while (attrOffset + 4 <= msg.Length)
        {
            var rtaLen = BinaryPrimitives.ReadUInt16LittleEndian(msg[attrOffset..]);
            var rtaType = BinaryPrimitives.ReadUInt16LittleEndian(msg[(attrOffset + 2)..]);
            if (rtaLen < 4 || attrOffset + rtaLen > msg.Length)
            {
                break;
            }

            var data = msg.Slice(attrOffset + 4, rtaLen - 4);
            switch (rtaType)
            {
                case RTA_DST:
                    destination = ReadAddress(family.Value, data);
                    break;
                case RTA_GATEWAY:
                    gateway = ReadAddress(family.Value, data);
                    break;
                case RTA_OIF when data.Length >= 4:
                    oif = BinaryPrimitives.ReadInt32LittleEndian(data);
                    break;
                case RTA_PRIORITY when data.Length >= 4:
                    priority = BinaryPrimitives.ReadInt32LittleEndian(data);
                    break;
            }

            attrOffset += Align(rtaLen);
        }

        var addressFamily = family.Value;
        destination ??= addressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;
        var onLink = gateway is null ||
                     gateway.Equals(IPAddress.Any) ||
                     gateway.Equals(IPAddress.IPv6Any);
        var isDefault = dstLen == 0 &&
                        (destination.Equals(IPAddress.Any) || destination.Equals(IPAddress.IPv6Any));

        string? ifName = null;
        if (oif is int idx)
        {
            indexNames.TryGetValue(idx, out ifName);
        }

        return new RouteEntry
        {
            Family = addressFamily,
            Destination = destination,
            PrefixLength = dstLen,
            NextHop = onLink ? null : gateway,
            InterfaceIndex = oif,
            InterfaceName = ifName,
            Metric = priority,
            Protocol = null,
            IsDefault = isDefault,
            IsOnLink = onLink
        };
    }

    /// <summary>Maps Linux AF_INET (2) / AF_INET6 (10) to <see cref="AddressFamily"/>.</summary>
    /// <param name="family">Linux address family byte.</param>
    /// <returns>Mapped family, or null when unsupported.</returns>
    private static AddressFamily? MapLinuxFamily(byte family) => family switch
    {
        2 => AddressFamily.InterNetwork,
        10 => AddressFamily.InterNetworkV6,
        _ => null
    };

    /// <summary>Reads an address of <paramref name="family"/> from netlink attribute data.</summary>
    /// <param name="family">Expected family.</param>
    /// <param name="data">Attribute payload.</param>
    /// <returns>Parsed address, or null when truncated.</returns>
    private static IPAddress? ReadAddress(AddressFamily family, ReadOnlySpan<byte> data)
    {
        if (family == AddressFamily.InterNetwork && data.Length >= 4)
        {
            return new IPAddress(data[..4]);
        }

        if (family == AddressFamily.InterNetworkV6 && data.Length >= 16)
        {
            return new IPAddress(data[..16]);
        }

        return null;
    }

    /// <summary>Aligns a netlink length up to a 4-byte boundary.</summary>
    /// <param name="length">Unaligned length.</param>
    /// <returns>Aligned length.</returns>
    private static int Align(int length) => (length + 3) & ~3;
}

/// <summary>
/// Abstraction over a Linux rtnetlink route dump for testability.
/// </summary>
internal interface ILinuxNetlinkRouteApi
{
    /// <summary>
    /// Dumps routes via netlink and returns the raw message bytes.
    /// </summary>
    /// <returns>Concatenated netlink messages.</returns>
    /// <remarks>Implementations must close any native socket/fd they open.</remarks>
    byte[] DumpRoutes();
}

/// <summary>
/// Production netlink dump using libc <c>socket</c>/<c>send</c>/<c>recv</c>/<c>close</c>.
/// </summary>
/// <remarks>Owns the netlink fd for the duration of <see cref="DumpRoutes"/>.</remarks>
[SupportedOSPlatform("linux")]
internal sealed class SystemLinuxNetlinkRouteApi : ILinuxNetlinkRouteApi
{
    /// <summary>Shared singleton instance.</summary>
    public static SystemLinuxNetlinkRouteApi Instance { get; } = new();

    /// <inheritdoc />
    public byte[] DumpRoutes()
    {
        var fd = socket(AF_NETLINK, (int)SocketType.Raw, NETLINK_ROUTE);
        if (fd < 0)
        {
            throw new InvalidOperationException("socket(AF_NETLINK) failed: " + Marshal.GetLastPInvokeError());
        }

        try
        {
            // nlmsghdr (16) + rtmsg (12)
            var request = new byte[28];
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(0), 28);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), RTM_GETROUTE);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), (ushort)(NLM_F_REQUEST | NLM_F_DUMP));
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 1); // seq
            // rtmsg family = AF_UNSPEC (0) already zeroed

            var sent = send(fd, request, request.Length, 0);
            if (sent < 0)
            {
                throw new InvalidOperationException("netlink send failed: " + Marshal.GetLastPInvokeError());
            }

            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var received = recv(fd, buffer, buffer.Length, 0);
                if (received < 0)
                {
                    throw new InvalidOperationException("netlink recv failed: " + Marshal.GetLastPInvokeError());
                }

                if (received == 0)
                {
                    break;
                }

                ms.Write(buffer, 0, received);

                // Stop when this datagram contains NLMSG_DONE.
                if (ContainsDone(buffer.AsSpan(0, received)) || ms.Length > 4 * 1024 * 1024)
                {
                    break;
                }
            }

            return ms.ToArray();
        }
        finally
        {
            close(fd);
        }
    }
    /// <summary>
    /// Returns whether a single recv datagram contains <c>NLMSG_DONE</c> or <c>NLMSG_ERROR</c>.
    /// </summary>
    /// <param name="buffer">One netlink datagram (may hold multiple aligned <c>nlmsghdr</c>s).</param>
    /// <returns>
    /// <see langword="true"/> when a terminal dump message is present so the recv loop can stop;
    /// also used with a soft size cap of 4 MiB on the concatenated stream.
    /// </returns>
    private static bool ContainsDone(ReadOnlySpan<byte> buffer)
    {
        var offset = 0;
        while (offset + 16 <= buffer.Length)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(buffer[offset..]);
            if (len < 16 || offset + len > buffer.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(offset + 4)..]);
            if (type == NLMSG_DONE || type == NLMSG_ERROR)
            {
                return true;
            }

            offset += (len + 3) & ~3;
        }

        return false;
    }
    /// <summary>
    /// Socket domain <c>AF_NETLINK</c> (16): Linux netlink address family.
    /// </summary>
    private const int AF_NETLINK = 16;

    /// <summary>
    /// Netlink protocol <c>NETLINK_ROUTE</c> (0): routing/link/neigh messages (<c>rtnetlink</c>).
    /// </summary>
    private const int NETLINK_ROUTE = 0;

    /// <summary>
    /// Rtnetlink request type <c>RTM_GETROUTE</c> (26): dump or get routes.
    /// </summary>
    private const int RTM_GETROUTE = 26;

    /// <summary>
    /// Netlink flag <c>NLM_F_REQUEST</c> (0x01): message is a request to the kernel.
    /// </summary>
    private const int NLM_F_REQUEST = 0x01;

    /// <summary>
    /// Netlink flag <c>NLM_F_DUMP</c> (0x300): request a multipart table dump.
    /// </summary>
    private const int NLM_F_DUMP = 0x300;

    /// <summary>
    /// Netlink message type <c>NLMSG_DONE</c> (3): end of a multipart dump.
    /// </summary>
    private const int NLMSG_DONE = 0x3;

    /// <summary>
    /// Netlink message type <c>NLMSG_ERROR</c> (2): error reply; ends the dump loop here.
    /// </summary>
    private const int NLMSG_ERROR = 0x2;

    /// <summary>
    /// Opens a netlink socket (<c>socket(2)</c>).
    /// </summary>
    /// <param name="domain">Address family; must be <c>AF_NETLINK</c> (16).</param>
    /// <param name="type">Socket type; raw for netlink.</param>
    /// <param name="protocol">Netlink protocol; <c>NETLINK_ROUTE</c> (0) for routes.</param>
    /// <returns>File descriptor on success; negative on failure (<c>errno</c> via P/Invoke last error).</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    /// <summary>
    /// Closes a file descriptor (<c>close(2)</c>); always called from <see cref="DumpRoutes"/> finally.
    /// </summary>
    /// <param name="fd">Netlink socket fd from <see cref="socket"/>.</param>
    /// <returns>0 on success; negative on failure.</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    /// <summary>
    /// Sends the <c>RTM_GETROUTE</c> dump request (<c>send(2)</c>).
    /// </summary>
    /// <param name="sockfd">Netlink socket fd.</param>
    /// <param name="buf">Request buffer (<c>nlmsghdr</c> + <c>rtmsg</c>).</param>
    /// <param name="len">Byte length of <paramref name="buf"/>.</param>
    /// <param name="flags">Send flags (0).</param>
    /// <returns>Bytes sent on success; negative on failure.</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int send(int sockfd, byte[] buf, int len, int flags);

    /// <summary>
    /// Receives one netlink datagram of route dump messages (<c>recv(2)</c>).
    /// </summary>
    /// <param name="sockfd">Netlink socket fd.</param>
    /// <param name="buf">Receive buffer.</param>
    /// <param name="len">Capacity of <paramref name="buf"/>.</param>
    /// <param name="flags">Recv flags (0).</param>
    /// <returns>Bytes received; 0 on orderly shutdown; negative on failure.</returns>
    [DllImport("libc", SetLastError = true)]
    private static extern int recv(int sockfd, byte[] buf, int len, int flags);
}
