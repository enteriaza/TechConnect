// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectSerialSettingsTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (ConnectSerialSettingsTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Ssh.Cli;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for connect serial settings parsing and defaults.
/// </summary>
public sealed class ConnectSerialSettingsTests
{
    /// <summary>
    /// Verifies: Defaults Are96008N1.
    /// </summary>
    [Fact]
    public void Defaults_Are96008N1()
    {
        var d = ConnectSerialSettings.Defaults;
        d.BaudRate.Should().Be(9600);
        d.DataBits.Should().Be(8);
        d.Parity.Should().Be("None");
        d.StopBits.Should().Be("One");
        d.Handshake.Should().Be("None");
    }

    /// <summary>
    /// Verifies: Empty Tokens Use Defaults.
    /// </summary>
    [Fact]
    public void EmptyTokens_UseDefaults()
    {
        ConnectSerialSettings.TryParse([], out var settings, out var error).Should().BeTrue();
        error.Should().BeNull();
        settings.Should().Be(ConnectSerialSettings.Defaults);
    }

    /// <summary>
    /// Verifies: Partial Baud Keeps Other Defaults.
    /// </summary>
    [Fact]
    public void PartialBaud_KeepsOtherDefaults()
    {
        ConnectSerialSettings.TryParse(["baud", "115200"], out var settings, out _).Should().BeTrue();
        settings.BaudRate.Should().Be(115200);
        settings.DataBits.Should().Be(8);
        settings.Parity.Should().Be("None");
        settings.StopBits.Should().Be("One");
    }

    /// <summary>
    /// Verifies: Explicit Data Bits Applied.
    /// </summary>
    [Theory]
    [InlineData("databits", "7", 7)]
    [InlineData("databits", "5", 5)]
    public void ExplicitDataBits_Applied(string key, string value, int expected)
    {
        ConnectSerialSettings.TryParse([key, value], out var settings, out _).Should().BeTrue();
        settings.DataBits.Should().Be(expected);
        settings.BaudRate.Should().Be(9600);
    }

    /// <summary>
    /// Verifies: Explicit Parity Applied.
    /// </summary>
    [Theory]
    [InlineData("none", "None")]
    [InlineData("odd", "Odd")]
    [InlineData("even", "Even")]
    [InlineData("mark", "Mark")]
    [InlineData("space", "Space")]
    public void ExplicitParity_Applied(string input, string expected)
    {
        ConnectSerialSettings.TryParse(["parity", input], out var settings, out _).Should().BeTrue();
        settings.Parity.Should().Be(expected);
    }

    /// <summary>
    /// Verifies: Explicit Stop Bits Applied.
    /// </summary>
    [Theory]
    [InlineData("1", "One")]
    [InlineData("2", "Two")]
    [InlineData("1.5", "OnePointFive")]
    public void ExplicitStopBits_Applied(string input, string expected)
    {
        ConnectSerialSettings.TryParse(["stopbits", input], out var settings, out _).Should().BeTrue();
        settings.StopBits.Should().Be(expected);
    }

    /// <summary>
    /// Verifies: Full Parameter Set Applied.
    /// </summary>
    [Fact]
    public void FullParameterSet_Applied()
    {
        ConnectSerialSettings.TryParse(
                ["baud", "9600", "databits", "8", "parity", "none", "stopbits", "1"],
                out var settings,
                out _)
            .Should().BeTrue();
        settings.Should().Be(ConnectSerialSettings.Defaults);
    }

    /// <summary>
    /// Verifies: Invalid Values Rejected.
    /// </summary>
    [Theory]
    [InlineData("baud", "12345")]
    [InlineData("databits", "9")]
    [InlineData("parity", "foo")]
    [InlineData("stopbits", "3")]
    public void InvalidValues_Rejected(string key, string value)
    {
        ConnectSerialSettings.TryParse([key, value], out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Verifies: Duplicate Parameters Rejected.
    /// </summary>
    [Fact]
    public void DuplicateParameters_Rejected()
    {
        ConnectSerialSettings.TryParse(["baud", "9600", "baud", "115200"], out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("Duplicate");
    }

    /// <summary>
    /// Verifies: Incomplete Parameter Rejected.
    /// </summary>
    [Fact]
    public void IncompleteParameter_Rejected()
    {
        ConnectSerialSettings.TryParse(["baud"], out _, out var error).Should().BeFalse();
        error.Should().Contain("Incomplete");
    }

    /// <summary>
    /// Verifies: Unknown Parameter Rejected.
    /// </summary>
    [Fact]
    public void UnknownParameter_Rejected()
    {
        ConnectSerialSettings.TryParse(["handshake", "None"], out _, out var error).Should().BeFalse();
        error.Should().Contain("Unknown");
    }
}
