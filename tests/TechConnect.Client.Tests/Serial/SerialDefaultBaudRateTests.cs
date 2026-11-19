// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialDefaultBaudRateTests.cs
//
// Purpose:
// Verifies default serial configuration is 115200 8N1 and can be overridden after open.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Serial;

/// <summary>
/// Unit tests for default baud/line settings and post-open configuration overrides.
/// </summary>
public sealed class SerialDefaultBaudRateTests
{
    /// <summary>
    /// Scenario: <see cref="SerialPortConfiguration.CreateDefault"/> is called with no overrides.
    /// Expected: 115200 baud, 8 data bits, no parity, one stop bit, handshake none.
    /// </summary>
    [Fact]
    public void CreateDefault_ResolvesTo115200_8N1_HandshakeNone()
    {
        var config = SerialPortConfiguration.CreateDefault();

        config.BaudRate.Should().Be(115200);
        config.DataBits.Should().Be(8);
        config.Parity.Should().Be(Parity.None);
        config.StopBits.Should().Be(StopBits.One);
        config.Handshake.Should().Be(Handshake.None);
    }

    /// <summary>
    /// Scenario: serial service opens with null initial configuration.
    /// Expected: fake port and service report the default 115200 baud before use.
    /// </summary>
    [Fact]
    public async Task Open_AppliesDefaultBaudRate_BeforePortIsUsable()
    {
        var factory = new FakeSerialPortFactory();
        await using var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new FakeTimeProvider(),
            factory);

        await service.OpenAsync("COM4", null, CancellationToken.None);

        factory.Ports["COM4"].BaudRate.Should().Be(115200);
        service.CurrentConfiguration.BaudRate.Should().Be(115200);
        service.GetEffectiveSettings()!.BaudRate.Should().Be(115200);
    }

    /// <summary>
    /// Scenario: after open with defaults, ApplyConfiguration sets baud to 9600.
    /// Expected: port and CurrentConfiguration baud become 9600 while other 8N1 fields remain.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_CanOverrideDefaultBaudRate()
    {
        var factory = new FakeSerialPortFactory();
        await using var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new FakeTimeProvider(),
            factory);

        await service.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        await service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 9600 },
            CancellationToken.None);

        factory.Ports["COM4"].BaudRate.Should().Be(9600, because: "remote/explicit config must override the default");
        service.CurrentConfiguration.BaudRate.Should().Be(9600);
        service.CurrentConfiguration.DataBits.Should().Be(8);
        service.CurrentConfiguration.Parity.Should().Be(Parity.None);
        service.CurrentConfiguration.StopBits.Should().Be(StopBits.One);
    }

    /// <summary>
    /// Scenario: validator is given baud rate 1 (out of range).
    /// Expected: <see cref="ArgumentOutOfRangeException"/> is thrown.
    /// </summary>
    [Fact]
    public void Validation_StillRejectsOutOfRangeBaud()
    {
        var act = () => SerialPortConfigurationValidator.Validate(
            new SerialPortConfiguration { BaudRate = 1 });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}