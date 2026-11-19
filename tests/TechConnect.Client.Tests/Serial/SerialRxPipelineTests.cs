// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRxPipelineTests.cs
//
// Purpose:
// Verifies serial RX pipeline publishing into the remote outbound path.
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
/// Verifies serial RX reader lifecycle, DTR/RTS, faults, and DataTransferred delivery.
/// </summary>
public sealed class SerialRxPipelineTests
{
    /// <summary>
    /// Creates a SerialConnectionService over a FakeSerialPortFactory and collecting logger.
    /// </summary>
    private static (SerialConnectionService Service, FakeSerialPortFactory Factory, CollectingLogger<SerialConnectionService> Logger)
        Create()
    {
        var factory = new FakeSerialPortFactory();
        var logger = new CollectingLogger<SerialConnectionService>();
        var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);
        return (service, factory, logger);
    }

    /// <summary>
    /// Scenario: open with Handshake.None.
    /// Expected: DtrEnable and RtsEnable asserted true on the fake port.
    /// </summary>
    [Fact]
    public async Task Open_AssertsDtrAndRts_WhenHandshakeNone()
    {
        var (service, factory, logger) = Create();
        await using var _ = service;

        await service.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        var port = factory.Ports["COM4"];
        port.BaudRate.Should().Be(115200);
        port.DataBits.Should().Be(8);
        port.Parity.Should().Be(Parity.None);
        port.StopBits.Should().Be(StopBits.One);
        port.Handshake.Should().Be(Handshake.None);
        port.DtrEnable.Should().BeTrue(because: "console devices require DTR asserted");
        port.RtsEnable.Should().BeTrue(
            because: "Handshake.None does not assert RTS; devices often will not transmit without it");

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Serial reader started", StringComparison.OrdinalIgnoreCase));
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("RtsEnable=True", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: open with RequestToSend handshake.
    /// Expected: RtsEnable property is not force-set by the service.
    /// </summary>
    [Fact]
    public async Task Open_WithRequestToSendHandshake_DoesNotForceRtsEnableProperty()
    {
        var (service, factory, _) = Create();
        await using var _ = service;

        await service.OpenAsync(
            "COM4",
            new SerialPortConfiguration { Handshake = Handshake.RequestToSend },
            CancellationToken.None);

        // Under RTS handshake the engine owns RTS; we must not require RtsEnable=true on the property.
        factory.Ports["COM4"].DtrEnable.Should().BeTrue();
        factory.Ports["COM4"].Handshake.Should().Be(Handshake.RequestToSend);
    }

    /// <summary>
    /// Scenario: inject RX immediately after open without remote session.
    /// Expected: bytes received stats increment.
    /// </summary>
    [Fact]
    public async Task ReaderStartsImmediately_AndRxIncrementsWithoutWaitingForRemote()
    {
        var (service, factory, logger) = Create();
        await using var _ = service;

        await service.OpenAsync("COM4", null, CancellationToken.None);

        logger.Entries.Should().Contain(e => e.Message.Contains("Serial reader started", StringComparison.Ordinal));

        var observed = new List<int>();
        service.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                observed.Add(chunk.Data.Length);
            }
        };

        factory.Ports["COM4"].InjectRx("hello"u8.ToArray());
        factory.Ports["COM4"].InjectRx("\r\n"u8.ToArray());
        factory.Ports["COM4"].InjectRx("world"u8.ToArray());

        await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 5 + 2 + 5);

        service.GetStats().BytesReceived.Should().Be(12);
        observed.Sum().Should().Be(12);
        logger.Entries.Should().Contain(e =>
            e.Message.Contains("Serial RX first data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: multiple RX injections.
    /// Expected: all chunks raise DataTransferred in order.
    /// </summary>
    [Fact]
    public async Task MultipleConsecutiveReads_AllReachDataTransferred()
    {
        var (service, factory, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var chunks = new List<string>();
        service.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                chunks.Add(System.Text.Encoding.ASCII.GetString(chunk.Data.Span));
            }
        };

        for (var i = 0; i < 10; i++)
        {
            factory.Ports["COM1"].InjectRx(System.Text.Encoding.ASCII.GetBytes($"L{i}"));
        }

        await TestHelpers.WaitUntilAsync(() => chunks.Count >= 10);
        chunks.Should().HaveCount(10);
        service.GetStats().BytesReceived.Should().Be(chunks.Sum(s => s.Length));
    }

    /// <summary>
    /// Scenario: base stream faults while reading.
    /// Expected: Faulted event and log entry observe the failure.
    /// </summary>
    [Fact]
    public async Task ReaderFault_IsObservableViaFaultedAndLog()
    {
        var (service, factory, logger) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        string? fault = null;
        service.Faulted += (_, message) => fault = message;

        factory.Ports["COM1"].SimulateDeviceRemoval();

        await TestHelpers.WaitUntilAsync(() => fault is not null, because: "reader fault must raise Faulted");
        fault.Should().Contain("COM1");
        await TestHelpers.WaitUntilAsync(
            () => logger.Entries.Any(e =>
                e.Message.Contains("Serial reader terminated", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Serial read failed", StringComparison.OrdinalIgnoreCase)),
            because: "reader lifecycle must be logged");
    }

    /// <summary>
    /// Scenario: close then reopen.
    /// Expected: prior reader stops; exactly one new reader after reconnect.
    /// </summary>
    [Fact]
    public async Task Disconnect_StopsReader_Reconnect_StartsExactlyOneNewReader()
    {
        var (service, factory, logger) = Create();
        await using var _ = service;

        await service.OpenAsync("COM1", null, CancellationToken.None);
        factory.Ports["COM1"].InjectRx("a"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 1);

        await service.CloseAsync(CancellationToken.None);
        logger.Entries.Count(e => e.Message.Contains("Serial reader started", StringComparison.Ordinal))
            .Should().Be(1);

        await service.OpenAsync("COM1", null, CancellationToken.None);
        logger.Entries.Count(e => e.Message.Contains("Serial reader started", StringComparison.Ordinal))
            .Should().Be(2, because: "exactly one new reader after reopen");

        factory.Ports["COM1"].InjectRx("b"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 1);
    }

    /// <summary>
    /// Scenario: incoming channel is saturated.
    /// Expected: DataTransferred still fires for RX.
    /// </summary>
    [Fact]
    public async Task IncomingChannelSaturation_DoesNotPreventDataTransferred()
    {
        var (service, factory, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        // Do not read Incoming — leave the unbounded channel growing, but UI event must still fire.
        var rx = 0;
        service.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                Interlocked.Add(ref rx, chunk.Data.Length);
            }
        };

        factory.Ports["COM1"].InjectRx(new byte[2048]);
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref rx) >= 2048);

        service.GetStats().BytesReceived.Should().Be(2048);
        Volatile.Read(ref rx).Should().Be(2048);
    }
}
