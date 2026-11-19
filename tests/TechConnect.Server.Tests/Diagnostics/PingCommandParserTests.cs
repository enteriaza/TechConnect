// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PingCommandParserTests.cs
//
// Purpose:
// Verifies ping CLI token parsing, option bounds, sweep validation, and session form restrictions.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for PingCommandParser.
/// </summary>
public sealed class PingCommandParserTests
{
    /// <summary>
    /// Parse Ipv4 Defaults.
    /// </summary>
    [Fact]
    public void Parse_Ipv4_Defaults()
    {
        PingCommandParser.TryParse(["ping", "8.8.8.8"], out var request, out var error)
            .Should().BeTrue(error);
        request!.Kind.Should().Be(PingTargetKind.Ipv4);
        request.Destination.Should().Be(IPAddress.Parse("8.8.8.8"));
        request.DestinationInput.Should().Be("8.8.8.8");
        request.Repeat.Should().Be(5);
        request.TimeoutSeconds.Should().Be(1);
        request.PayloadBytes.Should().Be(56);
        request.DontFragment.Should().BeFalse();
        request.RequiresResolution.Should().BeFalse();
    }

    /// <summary>
    /// Parse Ipv4 With Options.
    /// </summary>
    [Fact]
    public void Parse_Ipv4_WithOptions()
    {
        PingCommandParser.TryParse(
                ["ping", "1.1.1.1", "repeat", "10", "size", "1400", "timeout", "2", "df"],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.Repeat.Should().Be(10);
        request.PayloadBytes.Should().Be(1400);
        request.TimeoutSeconds.Should().Be(2);
        request.DontFragment.Should().BeTrue();
    }

    /// <summary>
    /// Parse Non Ip Is Hostname.
    /// </summary>
    [Fact]
    public void Parse_NonIp_IsHostname()
    {
        PingCommandParser.TryParse(["ping", "dns.google"], out var request, out var error)
            .Should().BeTrue(error);
        request!.Kind.Should().Be(PingTargetKind.Hostname);
        request.Destination.Should().BeNull();
        request.DestinationInput.Should().Be("dns.google");
        request.RequiresResolution.Should().BeTrue();
    }

    /// <summary>
    /// Parse Legacy Ipv6Keyword Rejected.
    /// </summary>
    [Fact]
    public void Parse_LegacyIpv6Keyword_Rejected()
    {
        PingCommandParser.TryParse(["ping", "ipv6", "8.8.8.8"], out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("ipv6");
        error.Should().Contain("destination");
    }

    /// <summary>
    /// Parse Ipv6Literal Defaults.
    /// </summary>
    [Fact]
    public void Parse_Ipv6Literal_Defaults()
    {
        PingCommandParser.TryParse(["ping", "2001:db8::1"], out var request, out var error)
            .Should().BeTrue(error);
        request!.Kind.Should().Be(PingTargetKind.Ipv6);
        request.Destination.Should().Be(IPAddress.Parse("2001:db8::1"));
        request.RequiresResolution.Should().BeFalse();
    }

    /// <summary>
    /// Parse Ipv6 Df Rejected.
    /// </summary>
    [Fact]
    public void Parse_Ipv6_Df_Rejected()
    {
        PingCommandParser.TryParse(
                ["ping", "2001:db8::1", "df"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("df");
    }

    /// <summary>
    /// Parse Session.
    /// </summary>
    [Fact]
    public void Parse_Session()
    {
        PingCommandParser.TryParse(
                ["ping", "session", "e92b5fa2", "repeat", "3", "timeout", "2"],
                out var request,
                out var error)
            .Should().BeTrue(error);
        request!.Kind.Should().Be(PingTargetKind.Session);
        request.SessionId.Should().Be("e92b5fa2");
        request.Repeat.Should().Be(3);
    }

    /// <summary>
    /// Parse Sweep Defaults And Validation.
    /// </summary>
    [Fact]
    public void Parse_Sweep_DefaultsAndValidation()
    {
        PingCommandParser.TryParse(["ping", "8.8.8.8", "sweep"], out var request, out var error)
            .Should().BeTrue(error);
        request!.Sweep.Should().BeTrue();
        request.SweepMinBytes.Should().Be(100);
        request.SweepMaxBytes.Should().Be(1500);
        request.SweepIntervalBytes.Should().Be(100);

        PingCommandParser.TryParse(
                ["ping", "8.8.8.8", "sweep", "min", "200", "max", "100"],
                out _,
                out error)
            .Should().BeFalse();
        error.Should().Contain("sweep");
    }

    /// <summary>
    /// Parse Sweep Exceeds Probe Budget.
    /// </summary>
    [Fact]
    public void Parse_Sweep_ExceedsProbeBudget()
    {
        PingCommandParser.TryParse(
                ["ping", "8.8.8.8", "repeat", "100", "sweep", "min", "1", "max", "9000", "interval", "1"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("probes");
    }

    /// <summary>
    /// Parse Wrong Source Family Rejected For Ipv4.
    /// </summary>
    [Fact]
    public void Parse_WrongSourceFamily_RejectedForIpv4()
    {
        PingCommandParser.TryParse(
                ["ping", "8.8.8.8", "source", "2001:db8::1"],
                out _,
                out var error)
            .Should().BeFalse();
        error.Should().Contain("IPv4");
    }
}
