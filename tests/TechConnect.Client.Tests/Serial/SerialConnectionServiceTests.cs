// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialConnectionServiceTests.cs
//
// Purpose:
// Verifies SerialConnectionService open/close, write, RX publish, config apply, fault, and lifecycle behaviour against FakeSerialPort.
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
/// Core SerialConnectionService open/close/write/config/fault lifecycle tests against FakeSerialPort.
/// </summary>
public sealed class SerialConnectionServiceTests
{
    /// <summary>
    /// Creates a SerialConnectionService test tuple with fake port factory, logger, and time.
    /// </summary>
    private static (SerialConnectionService Service, FakeSerialPortFactory Factory, CollectingLogger<SerialConnectionService> Logger, FakeTimeProvider Time)
        Create()
    {
        var factory = new FakeSerialPortFactory();
        var logger = new CollectingLogger<SerialConnectionService>();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        var service = new SerialConnectionService(logger, time, factory);
        return (service, factory, logger, time);
    }

    /// <summary>
    /// Scenario: successful OpenAsync.
    /// Expected: port open and reader active.
    /// </summary>
    [Fact]
    public async Task Open_Success_StartsReader()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;

        await service.OpenAsync("COM1", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        service.IsOpen.Should().BeTrue();
        service.PortName.Should().Be("COM1");
        factory.Ports["COM1"].OpenCount.Should().Be(1);
        factory.CreateCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: Open throws from fake port.
    /// Expected: exception propagates; service remains closed.
    /// </summary>
    [Fact]
    public async Task Open_Failure_PropagatesAndLeavesClosed()
    {
        var factory = new FakeSerialPortFactory(name => new FakeSerialPort(
            name,
            new FakeSerialPortOptions { OpenException = new UnauthorizedAccessException("busy") }));
        var logger = new CollectingLogger<SerialConnectionService>();
        var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);
        await using var _ = service;

        var act = async () => await service.OpenAsync("COM9", null, CancellationToken.None);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        service.IsOpen.Should().BeFalse();
        factory.Ports["COM9"].DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: Open while already open.
    /// Expected: throws.
    /// </summary>
    [Fact]
    public async Task Open_Duplicate_Throws()
    {
        var (service, _, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var act = async () => await service.OpenAsync("COM2", null, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already open*");
    }

    /// <summary>
    /// Scenario: CloseAsync while open.
    /// Expected: reader stops; IsOpen false.
    /// </summary>
    [Fact]
    public async Task Close_WhileOpen_StopsReader()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);
        await service.CloseAsync(CancellationToken.None);

        service.IsOpen.Should().BeFalse();
        service.PortName.Should().BeNull();
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: successful WriteAsync.
    /// Expected: TX DataTransferred and stats increment.
    /// </summary>
    [Fact]
    public async Task Write_Success_PublishesTxAndStats()
    {
        var (service, factory, _, time) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        SerialDataChunk? observed = null;
        service.DataTransferred += (_, chunk) => observed = chunk;

        await service.WriteAsync("hi"u8.ToArray(), CancellationToken.None);

        factory.Ports["COM1"].WrittenChunks.Should().ContainSingle()
            .Which.Should().Equal("hi"u8.ToArray());
        observed.Should().NotBeNull();
        observed!.Direction.Should().Be(SerialDataDirection.Tx);
        observed.TimestampUtc.Should().Be(time.GetUtcNow());
        service.GetStats().BytesTransmitted.Should().Be(2);
    }

    /// <summary>
    /// Scenario: WriteAsync after close.
    /// Expected: throws.
    /// </summary>
    [Fact]
    public async Task Write_AfterClose_Throws()
    {
        var (service, _, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);
        await service.CloseAsync(CancellationToken.None);

        var act = async () => await service.WriteAsync("x"u8.ToArray(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not open*");
    }

    /// <summary>
    /// Scenario: concurrent WriteAsync calls.
    /// Expected: writes are serialized without overlap corruption.
    /// </summary>
    [Fact]
    public async Task ConcurrentWrites_AreSerialized()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var payloads = Enumerable.Range(0, 20)
            .Select(i => Enumerable.Repeat((byte)i, 64).ToArray())
            .ToArray();

        await Task.WhenAll(payloads.Select(p => service.WriteAsync(p, CancellationToken.None)));

        var written = factory.Ports["COM1"].WrittenChunks.ToArray();
        written.Should().HaveCount(20);
        // Each chunk intact (no interleaving within a write).
        for (var i = 0; i < 20; i++)
        {
            written.Should().ContainEquivalentOf(payloads[i]);
        }

        service.GetStats().BytesTransmitted.Should().Be(20 * 64);
    }

    /// <summary>
    /// Scenario: ApplyConfiguration with varied framing InlineData.
    /// Expected: all framing fields applied to the port.
    /// </summary>
    [Theory]
    [InlineData(9600, 8, Parity.None, StopBits.One)]
    [InlineData(115200, 8, Parity.None, StopBits.One)]
    [InlineData(57600, 7, Parity.Even, StopBits.Two)]
    [InlineData(19200, 8, Parity.Odd, StopBits.One)]
    [InlineData(38400, 8, Parity.None, StopBits.OnePointFive)]
    public async Task ApplyConfiguration_AppliesCompleteFraming(
        int baud,
        int dataBits,
        Parity parity,
        StopBits stopBits)
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        var next = new SerialPortConfiguration
        {
            BaudRate = baud,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits,
            Handshake = Handshake.None
        };

        await service.ApplyConfigurationAsync(next, CancellationToken.None);

        service.IsOpen.Should().BeTrue();
        service.CurrentConfiguration.BaudRate.Should().Be(baud);
        service.CurrentConfiguration.DataBits.Should().Be(dataBits);
        service.CurrentConfiguration.Parity.Should().Be(parity);
        service.CurrentConfiguration.StopBits.Should().Be(stopBits);

        var port = factory.Ports["COM1"];
        port.IsOpen.Should().BeTrue();
        port.BaudRate.Should().Be(baud);
        port.DataBits.Should().Be(dataBits);
        port.Parity.Should().Be(parity);
        port.StopBits.Should().Be(stopBits);
        port.OpenCount.Should().Be(2, because: "reconfigure closes and reopens the same port instance");
        service.GetReceiveDiagnostics().IsReaderActive.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: ApplyConfiguration changes baud/line settings.
    /// Expected: fake port reflects new values.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_UpdatesPort()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", SerialPortConfiguration.CreateDefault(), CancellationToken.None);

        service.CurrentConfiguration.BaudRate.Should().Be(115200, because: "open uses CreateDefault()");

        var next = new SerialPortConfiguration
        {
            BaudRate = 57600,
            DataBits = 7,
            Parity = Parity.Even,
            StopBits = StopBits.Two,
            Handshake = Handshake.RequestToSend
        };

        await service.ApplyConfigurationAsync(next, CancellationToken.None);

        service.CurrentConfiguration.BaudRate.Should().Be(57600);
        var port = factory.Ports["COM1"];
        port.BaudRate.Should().Be(57600);
        port.DataBits.Should().Be(7);
        port.Parity.Should().Be(Parity.Even);
        port.StopBits.Should().Be(StopBits.Two);
        port.Handshake.Should().Be(Handshake.RequestToSend);
        port.RtsEnable.Should().BeTrue();
        port.OpenCount.Should().Be(2);
    }

    /// <summary>
    /// Scenario: apply config while reader ignores cancellation.
    /// Expected: close/reopen path resumes RX.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_WhileReaderIgnoresCancellation_ClosesAndResumesRx()
    {
        var factory = new FakeSerialPortFactory(name => new FakeSerialPort(
            name,
            new FakeSerialPortOptions { IgnoreReadCancellation = true }));
        var logger = new CollectingLogger<SerialConnectionService>();
        var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);
        await Task.Delay(30);

        string? fault = null;
        service.Faulted += (_, message) => fault = message;

        await service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 9600 },
            CancellationToken.None);

        service.IsOpen.Should().BeTrue();
        service.CurrentConfiguration.BaudRate.Should().Be(9600);
        factory.Ports["COM1"].OpenCount.Should().Be(2);
        fault.Should().BeNull(because: "intentional close during reconfigure must not raise Faulted");

        factory.Ports["COM1"].InjectRx("after-reconfig");
        await TestHelpers.WaitUntilAsync(
            () => service.GetStats().BytesReceived >= 14,
            because: "RX must resume on the reopened port");
    }

    /// <summary>
    /// Scenario: config change concurrent with write.
    /// Expected: operations serialize safely.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_SerializesAgainstConcurrentWrite()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var port = factory.Ports["COM1"];
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new ManualResetEventSlim(false);
        var writeSawClosedPort = false;

        port.Options.OnBaudRateSet = _ =>
        {
            applyEntered.TrySetResult();
            releaseApply.Wait(TimeSpan.FromSeconds(2));
        };

        var apply = service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 9600 },
            CancellationToken.None);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var write = Task.Run(async () =>
        {
            try
            {
                await service.WriteAsync("x"u8.ToArray(), CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                writeSawClosedPort = true;
            }
        });

        await Task.Delay(50);
        write.IsCompleted.Should().BeFalse(because: "write must wait for the reconfigure gate");
        releaseApply.Set();
        await Task.WhenAll(apply, write);

        writeSawClosedPort.Should().BeFalse();
        port.WrittenChunks.Should().ContainSingle().Which.Should().Equal("x"u8.ToArray());
        service.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: many successive config changes.
    /// Expected: reader count does not accumulate.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_RepeatedChanges_DoNotAccumulateReaders()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await service.ApplyConfigurationAsync(
                new SerialPortConfiguration { BaudRate = i % 2 == 0 ? 9600 : 115200 },
                CancellationToken.None);
        }

        service.IsOpen.Should().BeTrue();
        service.GetReceiveDiagnostics().IsReaderActive.Should().BeTrue();
        factory.Ports["COM1"].OpenCount.Should().Be(6);
        factory.Ports["COM1"].DisposeCount.Should().Be(0);

        factory.Ports["COM1"].InjectRx("ok");
        await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 2);
    }

    /// <summary>
    /// Scenario: reopen fails after config close.
    /// Expected: port abandoned/error path.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_ReopenFailure_AbandonsPort()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        string? fault = null;
        service.Faulted += (_, message) => fault = message;

        factory.Ports["COM1"].Options.OpenException = new UnauthorizedAccessException("port busy");

        var act = async () => await service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 9600 },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reopened*");
        service.IsOpen.Should().BeFalse();
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
        fault.Should().BeNull(because: "abandon path disposes without treating it as an unexpected RX fault");
    }

    /// <summary>
    /// Scenario: invalid configuration values.
    /// Expected: throws before mutating the port.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_Invalid_ThrowsBeforeTouchingPort()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var invalid = new SerialPortConfiguration { BaudRate = 1, DataBits = 8 };
        var act = async () => await service.ApplyConfigurationAsync(invalid, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        factory.Ports["COM1"].BaudRate.Should().Be(115200);
    }

    /// <summary>
    /// Scenario: driver rejects configuration.
    /// Expected: wrapped error; reader restarted.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_DriverReject_WrapsAndRestartsReader()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);
        factory.Ports["COM1"].Options.ConfigurationException = new ArgumentException("driver says no");

        var act = async () => await service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 57600 },
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*rejected*");
        ex.Which.InnerException.Should().BeOfType<ArgumentException>();

        // Reader should still accept RX after rejection path restarts it.
        factory.Ports["COM1"].Options.ConfigurationException = null;
        factory.Ports["COM1"].InjectRx("still-alive");
        await TestHelpers.WaitUntilAsync(
            () => service.GetStats().BytesReceived > 0,
            because: "reader should resume after rejected config");
    }

    /// <summary>
    /// Scenario: concurrent ApplyConfiguration calls.
    /// Expected: serialized application.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_ConcurrentRequests_AreSerialized()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var port = factory.Ports["COM1"];
        var gate = new ManualResetEventSlim(false);
        var entered = 0;
        var maxConcurrent = 0;
        port.Options.OnBaudRateSet = _ =>
        {
            var now = Interlocked.Increment(ref entered);
            while (true)
            {
                var observed = Volatile.Read(ref maxConcurrent);
                if (now <= observed ||
                    Interlocked.CompareExchange(ref maxConcurrent, now, observed) == observed)
                {
                    break;
                }
            }

            gate.Wait(TimeSpan.FromSeconds(2));
            Interlocked.Decrement(ref entered);
        };

        var first = service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 57600 },
            CancellationToken.None);
        await Task.Delay(30);
        var second = service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 9600 },
            CancellationToken.None);

        gate.Set();
        await Task.WhenAll(first, second);

        maxConcurrent.Should().Be(1);
        service.CurrentConfiguration.BaudRate.Should().Be(9600);
        port.BaudRate.Should().Be(9600);
        service.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: Close while config change in flight.
    /// Expected: waits then disconnects cleanly.
    /// </summary>
    [Fact]
    public async Task ApplyConfiguration_CloseWaitsForInFlightChange_ThenDisconnectsCleanly()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Ports["COM1"].Options.OnBaudRateSet = _ =>
        {
            started.TrySetResult();
            Thread.Sleep(80);
        };

        var apply = service.ApplyConfigurationAsync(
            new SerialPortConfiguration { BaudRate = 57600 },
            CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // Close serializes on the same gate; it must not tear down mid-apply.
        await service.CloseAsync(CancellationToken.None);
        await apply;

        service.IsOpen.Should().BeFalse();
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: inject ordered RX chunks.
    /// Expected: order preserved; receive stats match.
    /// </summary>
    [Fact]
    public async Task RxInjection_PreservesOrderAndStats()
    {
        var (service, factory, _, time) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var received = new List<string>();
        service.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                received.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
            }
        };

        factory.Ports["COM1"].InjectRx("one");
        factory.Ports["COM1"].InjectRx("two");
        factory.Ports["COM1"].InjectRx("three");

        await TestHelpers.WaitUntilAsync(() => received.Count >= 3);

        received.Should().Equal("one", "two", "three");
        service.GetStats().BytesReceived.Should().Be(3 + 3 + 5);
        service.GetStats().LastRxUtc.Should().Be(time.GetUtcNow());

        var fromChannel = new List<string>();
        while (service.Incoming.TryRead(out var chunk))
        {
            fromChannel.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
        }

        fromChannel.Should().Equal("one", "two", "three");
    }

    /// <summary>
    /// Scenario: SimulateDeviceRemoval while open.
    /// Expected: Faulted is raised.
    /// </summary>
    [Fact]
    public async Task SimulateDeviceRemoval_RaisesFault()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        string? fault = null;
        service.Faulted += (_, message) => fault = message;

        factory.Ports["COM1"].SimulateDeviceRemoval();

        await TestHelpers.WaitUntilAsync(() => fault is not null, because: "USB removal should fault the reader");
        fault.Should().Contain("COM1");
    }

    /// <summary>
    /// Scenario: Dispose during active RX/TX.
    /// Expected: completes without hang.
    /// </summary>
    [Fact]
    public async Task Dispose_WhileReadingAndWriting_CompletesCleanly()
    {
        var (service, factory, _, _) = Create();
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var writeLoop = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await service.WriteAsync("ping"u8.ToArray(), CancellationToken.None);
                    await Task.Delay(5);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });

        factory.Ports["COM1"].InjectRx(new byte[256]);
        await service.DisposeAsync();

        service.IsOpen.Should().BeFalse();
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
        await writeLoop.WaitAsync(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Scenario: repeated open/close cycles.
    /// Expected: each cycle succeeds.
    /// </summary>
    [Fact]
    public async Task RepeatedConnectCycles_Work()
    {
        var (service, factory, _, _) = Create();
        await using var _ = service;

        for (var i = 0; i < 5; i++)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            await service.WriteAsync("x"u8.ToArray(), CancellationToken.None);
            factory.Ports["COM1"].InjectRx("y");
            await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 1);
            await service.CloseAsync(CancellationToken.None);
        }

        factory.CreateCount.Should().Be(5);
        // Each Create returns a fresh port; the dictionary retains only the latest instance.
        factory.Ports["COM1"].OpenCount.Should().Be(1);
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: Dispose while reader running.
    /// Expected: background reader stops.
    /// </summary>
    [Fact]
    public async Task Dispose_StopsBackgroundReader()
    {
        var (service, factory, logger, _) = Create();
        await service.OpenAsync("COM1", null, CancellationToken.None);
        var port = factory.Ports["COM1"];
        await service.DisposeAsync();

        service.IsOpen.Should().BeFalse();
        port.DisposeCount.Should().Be(1);
        port.IsOpen.Should().BeFalse();
        logger.Entries.Any(e => e.Message.Contains("Serial reader terminated", StringComparison.Ordinal) ||
                                e.Message.Contains("Serial reader stopped", StringComparison.Ordinal))
            .Should().BeTrue();
        // Incoming channel is completed — no further RX consumption possible.
        service.Incoming.Completion.IsCompleted.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: Close while read ignores cancellation.
    /// Expected: completes quickly via SignalClosed.
    /// </summary>
    [Fact]
    public async Task Close_WhileBlockedReadIgnoringCancellation_CompletesQuickly()
    {
        // Mirrors real SerialPort.ReadAsync ignoring CancellationToken: close/dispose must unblock.
        var factory = new FakeSerialPortFactory(name => new FakeSerialPort(
            name,
            new FakeSerialPortOptions { IgnoreReadCancellation = true }));
        var logger = new CollectingLogger<SerialConnectionService>();
        var service = new SerialConnectionService(logger, new FakeTimeProvider(), factory);
        await service.OpenAsync("COM1", null, CancellationToken.None);

        // Give the reader time to block on ReadAsync.
        await Task.Delay(50);

        var close = service.CloseAsync(CancellationToken.None);
        await close.WaitAsync(TimeSpan.FromSeconds(2));

        service.IsOpen.Should().BeFalse();
        factory.Ports["COM1"].DisposeCount.Should().Be(1);
        logger.Entries.Should().NotContain(e =>
            e.Message.Contains("did not terminate within 3s", StringComparison.OrdinalIgnoreCase));
    }
}
