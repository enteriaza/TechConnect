// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    LoggingSafetyTests.cs
//
// Purpose:
// Verifies serial traffic payloads never appear in SerialConnectionService log messages.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Logging;

/// <summary>
/// Regression tests ensuring serial TX/RX payloads are not leaked into structured logs.
/// </summary>
public sealed class LoggingSafetyTests
{
    /// <summary>
    /// Scenario: distinctive binary is written and injected as RX through <see cref="SerialConnectionService"/>.
    /// Expected: logger entries never contain hex/Latin-1 forms of that payload, while operational open messages still appear.
    /// </summary>
    [Fact]
    public async Task SerialTraffic_NeverAppearsInLoggerMessages()
    {
        var factory = new FakeSerialPortFactory();
        var logger = new CollectingLogger<SerialConnectionService>();
        var time = new FakeTimeProvider();
        await using var service = new SerialConnectionService(logger, time, factory);

        await service.OpenAsync("COM1", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        // Distinctive binary markers unlikely to appear in framework messages.
        var distinctive = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0xCA, 0xFE, (byte)'Q', (byte)'Z', 0x7F };
        var distinctiveText = Convert.ToHexString(distinctive);

        await service.WriteAsync(distinctive, CancellationToken.None);
        factory.Ports["COM1"].InjectRx(distinctive);

        await TestHelpers.WaitUntilAsync(
            () => service.GetStats().BytesReceived >= distinctive.Length,
            because: "RX should be processed");

        logger.Entries.Should().NotBeEmpty();
        foreach (var entry in logger.Entries)
        {
            entry.Message.Should().NotContain(distinctiveText, because: "hex payload must not be logged");
            entry.Message.Should().NotContain("DEADBEEF", because: "payload bytes must not be logged");
            entry.Message.Should().NotContain("\u00de\u00ad\u00be\u00ef");
            // Raw binary as Latin-1 / UTF-8 mis-decode
            foreach (var b in distinctive)
            {
                // Avoid asserting single ASCII letters that may appear in words; check multi-byte sequence.
            }

            entry.Message.Should().NotContain("QZ", because: "contiguous payload ASCII must not appear in logs");
            entry.Message.Should().NotContain(System.Text.Encoding.Latin1.GetString(distinctive));
        }

        // Positive control: operational messages still logged.
        logger.Entries.Any(e => e.Message.Contains("Opening serial port", StringComparison.OrdinalIgnoreCase) ||
                                e.Message.Contains("Serial port", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue();
    }
}