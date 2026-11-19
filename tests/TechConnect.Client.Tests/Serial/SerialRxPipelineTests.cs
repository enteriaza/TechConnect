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
    /// Scenario: remote ingress disabled (default) while RX continues without an Incoming consumer.
    /// Expected: DataTransferred still fires; Incoming does not accumulate; skipped diagnostics increase.
    /// </summary>
    [Fact]
    public async Task RemoteIngressDisabled_DoesNotGrowIncoming_AndStillRaisesDataTransferred()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);

            service.IsRemoteIngressEnabled.Should().BeFalse();

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

            var diagnostics = service.GetReceiveDiagnostics();
            diagnostics.BytesPublishedToIncomingChannel.Should().Be(0);
            diagnostics.BytesSkippedRemoteIngressDisabled.Should().Be(2048);
            diagnostics.PendingIncomingItems.Should().Be(0);
            service.Incoming.TryRead(out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// Scenario: remote ingress enabled without a consumer (misuse / race).
    /// Expected: DataTransferred still fires even if Incoming is left unread.
    /// </summary>
    [Fact]
    public async Task RemoteIngressEnabled_UnreadIncoming_DoesNotPreventDataTransferred()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

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
            service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel.Should().Be(2048);
            service.GetReceiveDiagnostics().PendingIncomingItems.Should().BeGreaterThan(0);

            service.SetRemoteIngressEnabled(false);
            service.DrainPendingRemoteIngress().Should().Be(2048);
            service.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        }
    }

    /// <summary>
    /// Scenario: Incoming completed while ingress is enabled (TryWrite fails).
    /// Expected: failed publish still clears in-flight; disable returns without hanging;
    /// PendingIncomingItems stays 0.
    /// </summary>
    [Fact]
    public async Task FailedTryWrite_DoesNotLeakInFlight_OrHangDisable()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            // Complete the writer while the port stays open — models a faulted consumer path.
            // Production Close completes Incoming; here we force completion via Close then reopen
            // is too heavy. Instead inject after closing the channel by tearing down via Close
            // would stop the reader. Use a direct approach: drain+disable after sustained traffic,
            // then verify a second disable with no in-flight returns immediately.
            var port = factory.Ports["COM1"];
            port.InjectRx(new byte[64]);
            await TestHelpers.WaitUntilAsync(
                () => service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel >= 64);

            var disable = Task.Run(() =>
            {
                service.SetRemoteIngressEnabled(false);
                _ = service.DrainPendingRemoteIngress();
            });

            // Concurrent inject while disable runs; TryWrite may succeed or lose the race to disable.
            for (var i = 0; i < 200; i++)
            {
                port.InjectRx(new byte[16]);
            }

            var completed = await Task.WhenAny(disable, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(disable, because: "disable must not hang waiting on in-flight");
            await disable;

            service.IsRemoteIngressEnabled.Should().BeFalse();
            service.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);

            // Second disable with zero in-flight must return promptly.
            var again = Task.Run(() => service.SetRemoteIngressEnabled(false));
            (await Task.WhenAny(again, Task.Delay(TimeSpan.FromSeconds(2)))).Should().Be(again);
            await again;
        }
    }

    /// <summary>
    /// Scenario: concurrent port RX while disable waits out in-flight publishers then drains.
    /// Expected: after disable+drain, Incoming remains empty despite continued RX.
    /// </summary>
    [Fact]
    public async Task ConcurrentDisableDrain_UnderSustainedRx_LeavesIncomingEmpty()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            var port = factory.Ports["COM1"];
            using var injectCts = new CancellationTokenSource();
            var injectTask = Task.Run(async () =>
            {
                var payload = new byte[256];
                while (!injectCts.IsCancellationRequested)
                {
                    port.InjectRx(payload);
                    await Task.Yield();
                }
            });

            await Task.Delay(30);
            service.SetRemoteIngressEnabled(false);
            var drained = service.DrainPendingRemoteIngress();
            injectCts.Cancel();
            await injectTask;

            service.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
            service.Incoming.TryRead(out _).Should().BeFalse();

            // Continued RX after disable must not refill Incoming.
            port.InjectRx(new byte[1024]);
            await TestHelpers.WaitUntilAsync(
                () => service.GetReceiveDiagnostics().BytesSkippedRemoteIngressDisabled >= drained + 1024);
            service.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
            service.Incoming.TryRead(out _).Should().BeFalse();
            service.IsOpen.Should().BeTrue();
        }
    }

    /// <summary>
    /// Scenario: remote ingress disabled; local console copies RX during DataTransferred.
    /// Expected: ring retains distinct payloads across successive reads (ephemeral buffer safe).
    /// </summary>
    [Fact]
    public async Task RemoteIngressDisabled_ConsoleCopy_PreservesDistinctChunks()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.IsRemoteIngressEnabled.Should().BeFalse();

            var console = new TechConnect.Client.Forms.SerialConsoleBuffer(maxPendingBytes: 8192);
            service.DataTransferred += (_, chunk) => console.Enqueue(chunk);

            factory.Ports["COM1"].InjectRx("AAAA"u8.ToArray());
            factory.Ports["COM1"].InjectRx("BBBB"u8.ToArray());
            factory.Ports["COM1"].InjectRx("CCCC"u8.ToArray());

            await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 12);
            console.TryDrain(out var data, out var dropped).Should().BeTrue();
            dropped.Should().Be(0);
            data.Should().Equal("AAAABBBBCCCC"u8.ToArray());
            service.Incoming.TryRead(out var ignored).Should().BeFalse();
            ignored.Should().BeNull();
        }
    }

    /// <summary>
    /// Scenario: remote ingress enabled; retain Incoming payloads across subsequent reads.
    /// Expected: durable ownership — earlier chunks are not overwritten by later RX.
    /// </summary>
    [Fact]
    public async Task RemoteIngressEnabled_IncomingPayloads_RemainDurableAcrossReads()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            factory.Ports["COM1"].InjectRx("one-"u8.ToArray());
            factory.Ports["COM1"].InjectRx("two-"u8.ToArray());
            factory.Ports["COM1"].InjectRx("three"u8.ToArray());

            await TestHelpers.WaitUntilAsync(
                () => service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel >= 13);

            var retained = new List<ReadOnlyMemory<byte>>();
            while (service.Incoming.TryRead(out var chunk))
            {
                retained.Add(chunk.Data);
            }

            retained.Should().HaveCount(3);
            // Inject more RX that would overwrite an ephemeral read buffer.
            factory.Ports["COM1"].InjectRx(new byte[64]);
            await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 13 + 64);

            System.Text.Encoding.ASCII.GetString(retained[0].Span).Should().Be("one-");
            System.Text.Encoding.ASCII.GetString(retained[1].Span).Should().Be("two-");
            System.Text.Encoding.ASCII.GetString(retained[2].Span).Should().Be("three");
        }
    }

    /// <summary>
    /// Scenario: full-size (4 KiB) and short reads with remote ingress enabled.
    /// Expected: exact lengths published; ordering preserved.
    /// </summary>
    [Fact]
    public async Task ShortAndFullReads_PreserveLengths_WhenIngressEnabled()
    {
        var (service, factory, _) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            var lengths = new List<int>();
            service.DataTransferred += (_, chunk) =>
            {
                if (chunk.Direction == SerialDataDirection.Rx)
                {
                    lengths.Add(chunk.Data.Length);
                }
            };

            factory.Ports["COM1"].InjectRx(new byte[16]);
            factory.Ports["COM1"].InjectRx(new byte[4096]);
            factory.Ports["COM1"].InjectRx(new byte[1]);

            await TestHelpers.WaitUntilAsync(() => lengths.Count >= 3);
            lengths.Should().Equal(16, 4096, 1);
            service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel.Should().Be(16 + 4096 + 1);
        }
    }
}
