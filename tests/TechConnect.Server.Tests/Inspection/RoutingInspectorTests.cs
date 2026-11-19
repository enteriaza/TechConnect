// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RoutingInspectorTests.cs
//
// Purpose:
// Verifies platform inspector selection and Windows/Linux/macOS route parsers with synthetic dumps.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Inspection;

namespace TechConnect.Server.Tests.Inspection;

#pragma warning disable CA1416 // Parser helpers are platform-agnostic; inspector ctors guarded in tests.

/// <summary>
/// Unit and platform tests for routing table inspectors and parsers.
/// </summary>
public sealed class RoutingInspectorTests
{
    /// <summary>
    /// <see cref="PlatformRoutingTableInspector.Create"/> selects the OS-specific
    /// inspector; on Windows it can read a non-empty live table without UnavailableReason.
    /// </summary>
    [Fact]
    public void PlatformRouting_SelectsWindowsInspector_OnWindows()
    {
        var inspector = PlatformRoutingTableInspector.Create(NullLoggerFactory.Instance);
        if (OperatingSystem.IsWindows())
        {
            inspector.Should().BeOfType<WindowsRoutingTableInspector>();
            var snapshot = inspector.GetRoutes();
            snapshot.UnavailableReason.Should().BeNull();
            snapshot.Routes.Should().NotBeEmpty();
            snapshot.Routes.Should().Contain(r => r.Family == AddressFamily.InterNetwork);
            snapshot.Routes.Should().Contain(r => r.IsDefault || r.PrefixLength == 0 || r.Family == AddressFamily.InterNetworkV6);
        }
        else if (OperatingSystem.IsLinux())
        {
            inspector.Should().BeOfType<LinuxRoutingTableInspector>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            inspector.Should().BeOfType<MacRoutingTableInspector>();
        }
        else
        {
            inspector.Should().BeOfType<UnsupportedRoutingTableInspector>();
            inspector.GetRoutes().UnavailableReason.Should().Contain("not available");
        }
    }

    /// <summary>
    /// Synthetic MIB row: IPv4 default via gateway vs on-link /24 (null next hop).
    /// </summary>
    [Fact]
    public void WindowsParseRow_Ipv4DefaultAndOnLink()
    {
        var row = new byte[WindowsIpForwardTableParser.RowSize];
        WriteInt16(row, WindowsIpForwardTableParser.DestinationOffset, (short)AddressFamily.InterNetwork);
        // dest 0.0.0.0 already zero
        row[WindowsIpForwardTableParser.PrefixLengthOffset] = 0;
        WriteInt16(row, WindowsIpForwardTableParser.NextHopOffset, (short)AddressFamily.InterNetwork);
        row[WindowsIpForwardTableParser.NextHopOffset + 4] = 192;
        row[WindowsIpForwardTableParser.NextHopOffset + 5] = 168;
        row[WindowsIpForwardTableParser.NextHopOffset + 6] = 1;
        row[WindowsIpForwardTableParser.NextHopOffset + 7] = 1;
        WriteInt32(row, WindowsIpForwardTableParser.InterfaceIndexOffset, 13);
        WriteInt32(row, WindowsIpForwardTableParser.MetricOffset, 25);
        WriteInt32(row, WindowsIpForwardTableParser.ProtocolOffset, 3);

        var handle = GCHandle.Alloc(row, GCHandleType.Pinned);
        try
        {
            var names = new Dictionary<int, string> { [13] = "Ethernet" };
            var entry = WindowsIpForwardTableParser.ParseRow(handle.AddrOfPinnedObject(), names)!;
            entry.IsDefault.Should().BeTrue();
            entry.IsOnLink.Should().BeFalse();
            entry.NextHop.Should().Be(IPAddress.Parse("192.168.1.1"));
            entry.InterfaceName.Should().Be("Ethernet");
            entry.Metric.Should().Be(25);
            entry.Protocol.Should().Be("netmgmt");
        }
        finally
        {
            handle.Free();
        }

        var onLink = new byte[WindowsIpForwardTableParser.RowSize];
        WriteInt16(onLink, WindowsIpForwardTableParser.DestinationOffset, (short)AddressFamily.InterNetwork);
        onLink[WindowsIpForwardTableParser.DestinationOffset + 4] = 192;
        onLink[WindowsIpForwardTableParser.DestinationOffset + 5] = 168;
        onLink[WindowsIpForwardTableParser.DestinationOffset + 6] = 1;
        onLink[WindowsIpForwardTableParser.DestinationOffset + 7] = 0;
        onLink[WindowsIpForwardTableParser.PrefixLengthOffset] = 24;
        WriteInt16(onLink, WindowsIpForwardTableParser.NextHopOffset, (short)AddressFamily.InterNetwork);
        WriteInt32(onLink, WindowsIpForwardTableParser.InterfaceIndexOffset, 13);
        WriteInt32(onLink, WindowsIpForwardTableParser.MetricOffset, 281);
        var handle2 = GCHandle.Alloc(onLink, GCHandleType.Pinned);
        try
        {
            var entry = WindowsIpForwardTableParser.ParseRow(
                handle2.AddrOfPinnedObject(),
                new Dictionary<int, string> { [13] = "Ethernet" })!;
            entry.IsOnLink.Should().BeTrue();
            entry.NextHop.Should().BeNull();
            entry.Destination.Should().Be(IPAddress.Parse("192.168.1.0"));
            entry.PrefixLength.Should().Be(24);
        }
        finally
        {
            handle2.Free();
        }
    }

    /// <summary>
    /// Synthetic MIB row parses an IPv6 default route with link-local next hop.
    /// </summary>
    [Fact]
    public void WindowsParseRow_Ipv6Default()
    {
        var row = new byte[WindowsIpForwardTableParser.RowSize];
        WriteInt16(row, WindowsIpForwardTableParser.DestinationOffset, (short)AddressFamily.InterNetworkV6);
        row[WindowsIpForwardTableParser.PrefixLengthOffset] = 0;
        WriteInt16(row, WindowsIpForwardTableParser.NextHopOffset, (short)AddressFamily.InterNetworkV6);
        // fe80::1
        row[WindowsIpForwardTableParser.NextHopOffset + 8] = 0xfe;
        row[WindowsIpForwardTableParser.NextHopOffset + 9] = 0x80;
        row[WindowsIpForwardTableParser.NextHopOffset + 23] = 0x01;
        WriteInt32(row, WindowsIpForwardTableParser.InterfaceIndexOffset, 13);
        WriteInt32(row, WindowsIpForwardTableParser.MetricOffset, 25);

        var handle = GCHandle.Alloc(row, GCHandleType.Pinned);
        try
        {
            var entry = WindowsIpForwardTableParser.ParseRow(
                handle.AddrOfPinnedObject(),
                new Dictionary<int, string> { [13] = "Ethernet" })!;
            entry.Family.Should().Be(AddressFamily.InterNetworkV6);
            entry.IsDefault.Should().BeTrue();
            entry.NextHop.Should().Be(IPAddress.Parse("fe80::1"));
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Two default rows with different next hops are both retained (ECMP-style dump).
    /// </summary>
    [Fact]
    public void WindowsParseTable_MultipleRoutesSameDestination_PreservesAll()
    {
        var table = new byte[WindowsIpForwardTableParser.TableHeaderSize + (2 * WindowsIpForwardTableParser.RowSize)];
        WriteInt32(table, 0, 2);
        for (var i = 0; i < 2; i++)
        {
            var rowOffset = WindowsIpForwardTableParser.TableHeaderSize + (i * WindowsIpForwardTableParser.RowSize);
            WriteInt16(table, rowOffset + WindowsIpForwardTableParser.DestinationOffset, (short)AddressFamily.InterNetwork);
            table[rowOffset + WindowsIpForwardTableParser.PrefixLengthOffset] = 0;
            WriteInt16(table, rowOffset + WindowsIpForwardTableParser.NextHopOffset, (short)AddressFamily.InterNetwork);
            table[rowOffset + WindowsIpForwardTableParser.NextHopOffset + 4] = 10;
            table[rowOffset + WindowsIpForwardTableParser.NextHopOffset + 5] = 0;
            table[rowOffset + WindowsIpForwardTableParser.NextHopOffset + 6] = 0;
            table[rowOffset + WindowsIpForwardTableParser.NextHopOffset + 7] = (byte)(i + 1);
            WriteInt32(table, rowOffset + WindowsIpForwardTableParser.InterfaceIndexOffset, 13);
            WriteInt32(table, rowOffset + WindowsIpForwardTableParser.MetricOffset, 10 + i);
        }

        var handle = GCHandle.Alloc(table, GCHandleType.Pinned);
        try
        {
            var routes = WindowsIpForwardTableParser.ParseTable(
                handle.AddrOfPinnedObject(),
                new Dictionary<int, string> { [13] = "Ethernet" });
            routes.Should().HaveCount(2);
            routes.Select(r => r.NextHop!.ToString()).Should().BeEquivalentTo(["10.0.0.1", "10.0.0.2"]);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Native GetIpForwardTable2 failure populates UnavailableReason and does not free a null table.
    /// </summary>
    [Fact]
    public void WindowsInspector_NativeApiError_ReportsReason_AndFreesTable()
    {
        var api = new FakeWindowsApi(error: 87, table: IntPtr.Zero);
        var inspector = new WindowsRoutingTableInspector(NullLogger<WindowsRoutingTableInspector>.Instance, api);
        var snapshot = inspector.GetRoutes();
        snapshot.UnavailableReason.Should().Contain("87");
        api.FreeCalls.Should().Be(0);
    }

    /// <summary>
    /// Successful native read always frees the MIB table via <c>FreeMibTable</c>.
    /// </summary>
    [Fact]
    public void WindowsInspector_FreesNativeTable()
    {
        var table = Marshal.AllocHGlobal(WindowsIpForwardTableParser.TableHeaderSize);
        try
        {
            Marshal.WriteInt32(table, 0);
            var api = new FakeWindowsApi(error: 0, table: table);
            var inspector = new WindowsRoutingTableInspector(NullLogger<WindowsRoutingTableInspector>.Instance, api);
            inspector.GetRoutes().Routes.Should().BeEmpty();
            api.FreeCalls.Should().Be(1);
            table = IntPtr.Zero; // ownership transferred / freed by fake
        }
        finally
        {
            if (table != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(table);
            }
        }
    }

    /// <summary>
    /// Crafted NETLINK dump yields IPv4 default-via-gateway and on-link /24 entries.
    /// </summary>
    [Fact]
    public void LinuxParse_DefaultAndOnLink()
    {
        // Minimal crafted NETLINK dump: one default IPv4 via gateway, one on-link /24.
        var dump = BuildLinuxDump(
            BuildLinuxRoute(family: 2, dstLen: 0, gateway: IPAddress.Parse("192.168.1.1"), oif: 2, priority: 100),
            BuildLinuxRoute(family: 2, dstLen: 24, destination: IPAddress.Parse("192.168.1.0"), oif: 2, priority: 200));

        var routes = LinuxNetlinkRouteParser.ParseNetlinkDump(
            dump,
            new Dictionary<int, string> { [2] = "eth0" });
        routes.Should().HaveCount(2);
        routes[0].IsDefault.Should().BeTrue();
        routes[0].NextHop.Should().Be(IPAddress.Parse("192.168.1.1"));
        routes[0].InterfaceName.Should().Be("eth0");
        routes[1].IsOnLink.Should().BeTrue();
        routes[1].PrefixLength.Should().Be(24);
    }

    /// <summary>
    /// Crafted NETLINK dump yields a single IPv6 default with fe80::1 next hop.
    /// </summary>
    [Fact]
    public void LinuxParse_Ipv6Default()
    {
        var dump = BuildLinuxDump(
            BuildLinuxRoute(
                family: 10,
                dstLen: 0,
                gateway: IPAddress.Parse("fe80::1"),
                oif: 2,
                priority: 100));
        var routes = LinuxNetlinkRouteParser.ParseNetlinkDump(
            dump,
            new Dictionary<int, string> { [2] = "eth0" });
        routes.Should().ContainSingle();
        routes[0].Family.Should().Be(AddressFamily.InterNetworkV6);
        routes[0].IsDefault.Should().BeTrue();
        routes[0].NextHop.Should().Be(IPAddress.Parse("fe80::1"));
    }

    /// <summary>
    /// <see cref="NocDisplayFormatter.FormatRoutes"/> splits IPv4/IPv6 sections and labels on-link hops.
    /// </summary>
    [Fact]
    public void FormatRoutes_SectionsAndOnLink()
    {
        var text = NocDisplayFormatter.FormatRoutes(new RoutingTableSnapshot
        {
            Routes =
            [
                new RouteEntry
                {
                    Family = AddressFamily.InterNetwork,
                    Destination = IPAddress.Any,
                    PrefixLength = 0,
                    NextHop = IPAddress.Parse("192.168.1.1"),
                    InterfaceName = "Ethernet",
                    Metric = 25,
                    IsDefault = true
                },
                new RouteEntry
                {
                    Family = AddressFamily.InterNetwork,
                    Destination = IPAddress.Parse("192.168.1.0"),
                    PrefixLength = 24,
                    InterfaceName = "Ethernet",
                    Metric = 281,
                    IsOnLink = true
                },
                new RouteEntry
                {
                    Family = AddressFamily.InterNetworkV6,
                    Destination = IPAddress.IPv6Any,
                    PrefixLength = 0,
                    NextHop = IPAddress.Parse("fe80::1"),
                    InterfaceName = "Ethernet",
                    Metric = 25,
                    IsDefault = true
                }
            ]
        });

        text.Should().Contain("IPv4 Routing Table");
        text.Should().Contain("IPv6 Routing Table");
        text.Should().Contain("On-link");
        text.Should().Contain("192.168.1.1");
        text.Should().Contain("fe80::1");
        text.Should().Contain("3 routes");
    }

    /// <summary>
    /// <see cref="UnsupportedRoutingTableInspector"/> always returns an unavailable reason string.
    /// </summary>
    [Fact]
    public void UnsupportedInspector_ReportsReason()
    {
        new UnsupportedRoutingTableInspector().GetRoutes().UnavailableReason
            .Should().Contain("not available");
    }
    /// <summary>
    /// Fake native API returning a configured error/table pointer and counting frees.
    /// </summary>
    private sealed class FakeWindowsApi(int error, IntPtr table) : IWindowsIpForwardTableApi
    {
        /// <summary>Number of times <see cref="FreeMibTable"/> was invoked.</summary>
        public int FreeCalls { get; private set; }

        /// <inheritdoc />
        public int GetIpForwardTable2(ushort family, out IntPtr result)
        {
            result = table;
            return error;
        }

        /// <inheritdoc />
        public void FreeMibTable(IntPtr memory)
        {
            FreeCalls++;
            if (memory != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(memory);
            }
        }
    }

    /// <summary>Concatenates route messages and appends <c>NLMSG_DONE</c>.</summary>
    private static byte[] BuildLinuxDump(params byte[][] routes)
    {
        using var ms = new MemoryStream();
        foreach (var route in routes)
        {
            ms.Write(route);
        }

        // NLMSG_DONE
        var done = new byte[16];
        BitConverter.GetBytes(16).CopyTo(done, 0);
        BitConverter.GetBytes((ushort)0x3).CopyTo(done, 4);
        ms.Write(done);
        return ms.ToArray();
    }
    /// <summary>Builds one RTM_NEWROUTE netlink message with optional RTA_DST/GATEWAY/OIF/PRIORITY.</summary>
    private static byte[] BuildLinuxRoute(
        int family,
        byte dstLen,
        IPAddress? destination = null,
        IPAddress? gateway = null,
        int oif = 1,
        int priority = 0)
    {
        using var attrs = new MemoryStream();
        if (destination is not null)
        {
            WriteRta(attrs, 1, destination.GetAddressBytes());
        }

        if (gateway is not null)
        {
            WriteRta(attrs, 5, gateway.GetAddressBytes());
        }

        WriteRta(attrs, 4, BitConverter.GetBytes(oif));
        WriteRta(attrs, 6, BitConverter.GetBytes(priority));

        var attrBytes = attrs.ToArray();
        var rtmsg = new byte[12];
        rtmsg[0] = (byte)family;
        rtmsg[1] = dstLen;
        rtmsg[6] = 1; // RTN_UNICAST

        var nlLen = 16 + 12 + attrBytes.Length;
        var msg = new byte[nlLen];
        BitConverter.GetBytes(nlLen).CopyTo(msg, 0);
        BitConverter.GetBytes((ushort)24).CopyTo(msg, 4); // RTM_NEWROUTE
        rtmsg.CopyTo(msg, 16);
        attrBytes.CopyTo(msg, 28);
        return msg;
    }
    /// <summary>Writes a 4-byte-aligned RTA attribute into the attribute stream.</summary>
    private static void WriteRta(MemoryStream ms, ushort type, byte[] data)
    {
        var len = (ushort)(4 + data.Length);
        ms.Write(BitConverter.GetBytes(len));
        ms.Write(BitConverter.GetBytes(type));
        ms.Write(data);
        while (ms.Length % 4 != 0)
        {
            ms.WriteByte(0);
        }
    }
    /// <summary>Little-endian Int16 poke into a synthetic MIB row buffer.</summary>
    private static void WriteInt16(byte[] buffer, int offset, short value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    /// <summary>Little-endian Int32 poke into a synthetic MIB row/table buffer.</summary>
    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}

#pragma warning restore CA1416
