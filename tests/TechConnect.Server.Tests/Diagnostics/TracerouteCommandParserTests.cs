// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TracerouteCommandParserTests.cs
//
// Purpose:
// Verifies traceroute CLI token parsing, bounds, port progression, and duration estimates.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for TracerouteCommandParser.
/// </summary>
public sealed class TracerouteCommandParserTests
{
    /// <summary>
    /// Parse Ipv4 Defaults.
    /// </summary>
    [Fact]
    public void Parse_Ipv4_Defaults()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8"],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.Family.Should().Be(TracerouteAddressFamily.Ipv4);
        request.Destination.Should().Be(IPAddress.Parse("8.8.8.8"));
        request.RequiresResolution.Should().BeFalse();
        request.TimeoutSeconds.Should().Be(3);
        request.ProbesPerHop.Should().Be(3);
        request.MaxTtl.Should().Be(30);
        request.InitialPort.Should().Be(33434);
        request.PayloadBytes.Should().Be(40);
    }

    /// <summary>
    /// Parse Ipv4 With Options.
    /// </summary>
    [Fact]
    public void Parse_Ipv4_WithOptions()
    {
        TracerouteCommandParser.TryParse(
                [
                    "traceroute", "1.1.1.1", "timeout", "2", "probe", "5", "ttl", "40",
                    "port", "34000", "size", "60", "source", "10.0.0.5"
                ],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.TimeoutSeconds.Should().Be(2);
        request.ProbesPerHop.Should().Be(5);
        request.MaxTtl.Should().Be(40);
        request.InitialPort.Should().Be(34000);
        request.PayloadBytes.Should().Be(60);
        request.Source.Should().Be(IPAddress.Parse("10.0.0.5"));
    }

    /// <summary>
    /// Parse Hostname Defers Resolution.
    /// </summary>
    [Fact]
    public void Parse_Hostname_DefersResolution()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "dns.google"],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.RequiresResolution.Should().BeTrue();
        request.DestinationInput.Should().Be("dns.google");
        request.Destination.Should().Be(IPAddress.None);
    }

    /// <summary>
    /// Parse Legacy Ipv6Subcommand Rejected.
    /// </summary>
    [Fact]
    public void Parse_LegacyIpv6Subcommand_Rejected()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "ipv6", "2001:db8::1"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("ipv6");
    }

    /// <summary>
    /// Parse Ipv6Literal Defaults.
    /// </summary>
    [Fact]
    public void Parse_Ipv6Literal_Defaults()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "2001:db8::1", "ttl", "20"],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.Family.Should().Be(TracerouteAddressFamily.Ipv6);
        request.RequiresResolution.Should().BeFalse();
        request.MaxTtl.Should().Be(20);
        request.InitialPort.Should().Be(33434);
    }

    /// <summary>
    /// Parse Rejects Zero Values.
    /// </summary>
    [Theory]
    [InlineData("timeout", "0")]
    [InlineData("probe", "0")]
    [InlineData("ttl", "0")]
    [InlineData("port", "0")]
    public void Parse_RejectsZeroValues(string key, string value)
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", key, value],
                out _,
                out _)
            .Should().BeFalse();
    }

    /// <summary>
    /// Parse Rejects Out Of Range Timeout.
    /// </summary>
    [Fact]
    public void Parse_RejectsOutOfRangeTimeout()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", "timeout", "61"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("Timeout");
    }

    /// <summary>
    /// Parse Rejects Port Overflow.
    /// </summary>
    [Fact]
    public void Parse_RejectsPortOverflow()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", "port", "65500", "ttl", "64", "probe", "10"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("Port progression");
    }

    /// <summary>
    /// Parse Rejects Excessive Duration.
    /// </summary>
    [Fact]
    public void Parse_RejectsExcessiveDuration()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", "ttl", "64", "probe", "10", "timeout", "60"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("duration");
    }

    /// <summary>
    /// Parse Rejects Wrong Source Family.
    /// </summary>
    [Fact]
    public void Parse_RejectsWrongSourceFamily()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", "source", "2001:db8::1"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("IPv4");
    }

    /// <summary>
    /// Parse Incomplete Parameter Fails.
    /// </summary>
    [Fact]
    public void Parse_IncompleteParameter_Fails()
    {
        TracerouteCommandParser.TryParse(
                ["traceroute", "8.8.8.8", "timeout"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("Incomplete");
    }
}
