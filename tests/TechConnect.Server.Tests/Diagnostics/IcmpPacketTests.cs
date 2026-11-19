// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    IcmpPacketTests.cs
//
// Purpose:
// Verifies ICMP Echo packet builders and internet checksum behaviour for ping/traceroute.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for ICMP Echo request construction and checksum helpers.
/// </summary>
public sealed class IcmpPacketTests
{
    /// <summary>
    /// Build Icmpv4Echo Request Has Valid Checksum And Header.
    /// </summary>
    [Fact]
    public void BuildIcmpv4EchoRequest_HasValidChecksumAndHeader()
    {
        var packet = RawSocketIcmpEchoClient.BuildIcmpv4EchoRequest(0x1234, 7, payloadBytes: 56);
        packet.Length.Should().Be(64);
        packet[0].Should().Be(8);
        packet[1].Should().Be(0);
        RawSocketIcmpEchoClient.ComputeInternetChecksum(packet).Should().Be(0);
    }

    /// <summary>
    /// Estimate Packet Sizes Account For Headers.
    /// </summary>
    [Fact]
    public void EstimatePacketSizes_AccountForHeaders()
    {
        PingLimits.EstimateIpv4PacketBytes(56).Should().Be(84);
        PingLimits.EstimateIpv6PacketBytes(56).Should().Be(104);
    }

    /// <summary>
    /// Global Unicast Detection.
    /// </summary>
    [Theory]
    [InlineData("2001:db8::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    public void GlobalUnicast_Detection(string address, bool expected)
    {
        SystemIPv6NetworkAvailability.IsGlobalUnicast(System.Net.IPAddress.Parse(address))
            .Should().Be(expected);
    }
}
