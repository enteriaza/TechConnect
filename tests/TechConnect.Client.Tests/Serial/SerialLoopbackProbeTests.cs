// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialLoopbackProbeTests.cs
//
// Purpose:
// Verifies simulated loopback probe match/miss behaviour and effective-settings logging (fake port only).
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Serial;

/// <summary>
/// Simulated loopback only (fake port echo). Does not claim physical hardware verification.
/// </summary>
public sealed class SerialLoopbackProbeTests
{
    /// <summary>
    /// Canonical probe payload used by loopback match/miss scenarios.
    /// </summary>
    private static readonly byte[] Probe = Encoding.ASCII.GetBytes("TECHCONNECTOR-RX-TEST-12345");

    /// <summary>
    /// Scenario: open a fake port with default configuration.
    /// Expected: effective settings expose COM4 @ 115200, reader is active, and open diagnostics are logged.
    /// </summary>
    [Fact]
    public async Task Open_LogsEffectiveSettings_AndReaderActive()
    {
        var factory = new FakeSerialPortFactory();
        var logger = new CollectingLogger<SerialConnectionService>();
        await using var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);

        await service.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        var effective = service.GetEffectiveSettings();
        effective.Should().NotBeNull();
        effective!.PortName.Should().Be("COM4");
        effective.IsOpen.Should().BeTrue();
        effective.BaudRate.Should().Be(115200);
        effective.ToDiagnosticString().Should().Contain("BaudRate=115200");

        service.GetReceiveDiagnostics().IsReaderActive.Should().BeTrue();
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("effective settings after open", StringComparison.OrdinalIgnoreCase));
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("reader active=True", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: loopback-enabled fake port echoes TX into RX during a probe.
    /// Expected: probe reports MATCHED with written/received byte counts and no timeout.
    /// </summary>
    [Fact]
    public async Task LoopbackProbe_WithEchoingPort_Matches()
    {
        var factory = new FakeSerialPortFactory(name => new FakeSerialPort(
            name,
            new FakeSerialPortOptions { Loopback = true }));
        var logger = new CollectingLogger<SerialConnectionService>();
        await using var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);

        await service.OpenAsync("COM4", null, CancellationToken.None);

        var result = await service.RunLoopbackProbeAsync(Probe, TimeSpan.FromSeconds(2), CancellationToken.None);

        result.ProbeMatched.Should().BeTrue();
        result.BytesWritten.Should().Be(Probe.Length);
        result.BytesReceivedDuringProbe.Should().BeGreaterThanOrEqualTo(Probe.Length);
        result.TimedOut.Should().BeFalse();
        result.Summary.Should().Contain("MATCHED");
        service.GetStats().BytesTransmitted.Should().Be(Probe.Length);
        service.GetStats().BytesReceived.Should().BeGreaterThanOrEqualTo(Probe.Length);
    }

    /// <summary>
    /// Scenario: probe runs on a fake port without loopback (no echo).
    /// Expected: no match, zero RX during probe, timed out, summary does not claim hardware proof.
    /// </summary>
    [Fact]
    public async Task LoopbackProbe_WithoutEcho_DoesNotClaimMatch()
    {
        var factory = new FakeSerialPortFactory(); // no Loopback
        await using var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new FakeTimeProvider(),
            factory);

        await service.OpenAsync("COM4", null, CancellationToken.None);

        var result = await service.RunLoopbackProbeAsync(Probe, TimeSpan.FromMilliseconds(200), CancellationToken.None);

        result.ProbeMatched.Should().BeFalse();
        result.BytesWritten.Should().Be(Probe.Length);
        result.BytesReceivedDuringProbe.Should().Be(0);
        result.TimedOut.Should().BeTrue();
        result.Summary.Should().Contain("does not prove");
    }

    /// <summary>
    /// Scenario: probe is requested while the serial service is not open.
    /// Expected: clean failure with zero bytes written and summary mentioning not open.
    /// </summary>
    [Fact]
    public async Task LoopbackProbe_WhenClosed_FailsCleanly()
    {
        await using var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new FakeTimeProvider(),
            new FakeSerialPortFactory());

        var result = await service.RunLoopbackProbeAsync(Probe, TimeSpan.FromSeconds(1), CancellationToken.None);

        result.ProbeMatched.Should().BeFalse();
        result.BytesWritten.Should().Be(0);
        result.Summary.Should().Contain("not open");
    }

    /// <summary>
    /// Scenario: concurrent write and injected RX while no probe is running on a loopback port.
    /// Expected: TX and RX byte counters advance normally outside probe mode.
    /// </summary>
    [Fact]
    public async Task ConcurrentTxRx_WhileProbeNotRunning_StillWorks()
    {
        var factory = new FakeSerialPortFactory(name => new FakeSerialPort(
            name,
            new FakeSerialPortOptions { Loopback = true }));
        await using var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new FakeTimeProvider(),
            factory);

        await service.OpenAsync("COM1", null, CancellationToken.None);

        var write = service.WriteAsync("ping"u8.ToArray(), CancellationToken.None);
        factory.Ports["COM1"].InjectRx("pong"u8.ToArray());
        await write;

        await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 4);
        service.GetStats().BytesTransmitted.Should().Be(4);
    }
}
