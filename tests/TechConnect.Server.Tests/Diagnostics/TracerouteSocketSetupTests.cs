// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteSocketSetupTests.cs
//
// Purpose:
// Verifies ICMP bind endpoint construction and bind preconditions for traceroute sockets.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for TracerouteSocketSetup.
/// </summary>
public sealed class TracerouteSocketSetupTests
{
    /// <summary>
    /// Create Icmp Bind Endpoint Ipv4 Automatic And Explicit.
    /// </summary>
    [Fact]
    public void CreateIcmpBindEndpoint_Ipv4_AutomaticAndExplicit()
    {
        TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetwork, source: null)
            .Address.Should().Be(IPAddress.Any);

        var source = IPAddress.Parse("10.0.0.5");
        TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetwork, source)
            .Address.Should().Be(source);
    }

    /// <summary>
    /// Create Icmp Bind Endpoint Ipv6 Automatic And Explicit.
    /// </summary>
    [Fact]
    public void CreateIcmpBindEndpoint_Ipv6_AutomaticAndExplicit()
    {
        TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetworkV6, source: null)
            .Address.Should().Be(IPAddress.IPv6Any);

        var source = IPAddress.Parse("2001:db8::5");
        TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetworkV6, source)
            .Address.Should().Be(source);
    }

    /// <summary>
    /// Create Icmp Bind Endpoint Rejects Family Mismatch.
    /// </summary>
    [Fact]
    public void CreateIcmpBindEndpoint_RejectsFamilyMismatch()
    {
        var act = () => TracerouteSocketSetup.CreateIcmpBindEndpoint(
            AddressFamily.InterNetwork,
            IPAddress.Parse("2001:db8::1"));
        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Bind Icmp Socket Binds Before Receive Would Be Legal.
    /// </summary>
    [Fact]
    public void BindIcmpSocket_BindsBeforeReceiveWouldBeLegal()
    {
        using var icmpStandIn = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        icmpStandIn.IsBound.Should().BeFalse();
        var endpoint = TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetwork, source: null);
        TracerouteSocketSetup.BindIcmpSocket(icmpStandIn, endpoint);
        icmpStandIn.IsBound.Should().BeTrue();
        icmpStandIn.LocalEndPoint.Should().NotBeNull();
    }

    /// <summary>
    /// Bind Icmp Socket Ipv6 Correct Family.
    /// </summary>
    [Fact]
    public void BindIcmpSocket_Ipv6_CorrectFamily()
    {
        using var icmpStandIn = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
        var endpoint = TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetworkV6, source: null);
        TracerouteSocketSetup.BindIcmpSocket(icmpStandIn, endpoint);
        ((IPEndPoint)icmpStandIn.LocalEndPoint!).AddressFamily.Should().Be(AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// Bind Icmp Socket Rejects Already Bound.
    /// </summary>
    [Fact]
    public void BindIcmpSocket_RejectsAlreadyBound()
    {
        using var icmpStandIn = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        icmpStandIn.Bind(new IPEndPoint(IPAddress.Any, 0));
        var endpoint = TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetwork, source: null);
        var act = () => TracerouteSocketSetup.BindIcmpSocket(icmpStandIn, endpoint);
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Bind Icmp Socket Repeated Fresh Sockets Initialize Cleanly.
    /// </summary>
    [Fact]
    public void BindIcmpSocket_RepeatedFreshSockets_InitializeCleanly()
    {
        for (var i = 0; i < 3; i++)
        {
            using var icmpStandIn = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            var endpoint = TracerouteSocketSetup.CreateIcmpBindEndpoint(AddressFamily.InterNetwork, source: null);
            TracerouteSocketSetup.BindIcmpSocket(icmpStandIn, endpoint);
            icmpStandIn.IsBound.Should().BeTrue();
        }
    }
}
