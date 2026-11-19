// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRemoteTxPathTests.cs
//
// Purpose:
// Verifies remote-to-serial TX path delivery and backpressure behaviour.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Serial;

/// <summary>
/// Focused coverage for remote <c>SerialData</c> → <see cref="SerialConnectionService.WriteAsync"/>
/// including binary integrity, backpressure, drain, and failure propagation.
/// </summary>
public sealed class SerialRemoteTxPathTests
{
    /// <summary>
    /// Creates a SerialConnectionService over FakeSerialPort for direct write-path tests.
    /// </summary>
    private static (SerialConnectionService Service, FakeSerialPortFactory Factory, FakeTimeProvider Time)
        CreateService(FakeSerialPortOptions? options = null)
    {
        var factory = options is null
            ? new FakeSerialPortFactory()
            : new FakeSerialPortFactory(name => new FakeSerialPort(name, options));
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T08:00:00Z"));
        var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            time,
            factory);
        return (service, factory, time);
    }

    /// <summary>
    /// Scenario: transport emits serial data to a connected session.
    /// Expected: serial writer receives exact bytes.
    /// </summary>
    [Fact]
    public async Task RemoteSerialData_ReachesSerialWriter_ExactBytes()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();

        var payload = Encoding.UTF8.GetBytes("index.html-body");
        await harness.Transport.EmitSerialDataAsync(payload);

        harness.Serial.Writes.Should().ContainSingle().Which.Should().Equal(payload);
        harness.Serial.GetStats().BytesTransmitted.Should().Be(payload.Length);
    }

    /// <summary>
    /// Scenario: emit all byte values 0-255.
    /// Expected: order and values preserved at serial.
    /// </summary>
    [Fact]
    public async Task RemoteSerialData_PreservesAllByteValuesAndOrder()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();

        var payload = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            payload[i] = (byte)i;
        }

        await harness.Transport.EmitSerialDataAsync(payload);
        harness.Serial.Writes.Should().ContainSingle().Which.Should().Equal(payload);
    }

    /// <summary>
    /// Scenario: multiple fragmented remote emits.
    /// Expected: concatenated write order matches emit order.
    /// </summary>
    [Fact]
    public async Task RemoteSerialData_FragmentedFrames_ConcatenateInOrder()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();

        var parts = new[]
        {
            "AAA"u8.ToArray(),
            new byte[] { 0x00, 0xFF, 0x00 },
            "BBB"u8.ToArray(),
            Encoding.UTF8.GetBytes("\r\n"),
            new byte[] { 0x80, 0xBF } // invalid UTF-8
        };

        foreach (var part in parts)
        {
            await harness.Transport.EmitSerialDataAsync(part);
        }

        harness.Serial.Writes.Should().HaveCount(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            harness.Serial.Writes[i].Should().Equal(parts[i]);
        }

        harness.Serial.GetStats().BytesTransmitted.Should().Be(parts.Sum(p => p.Length));
    }

    /// <summary>
    /// Scenario: emit the historically failing 10701-byte payload.
    /// Expected: full payload preserved at serial.
    /// </summary>
    [Fact]
    public async Task RemoteSerialData_ObservedFailureSize_10701_Preserved()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();

        var payload = BuildDeterministicPayload(10_701);
        await harness.Transport.EmitSerialDataAsync(payload);

        harness.Serial.Writes.Should().ContainSingle().Which.Should().Equal(payload);
        harness.Serial.GetStats().BytesTransmitted.Should().Be(10_701);
    }

    /// <summary>
    /// Scenario: large multi-chunk WriteAsync on SerialConnectionService.
    /// Expected: exact bytes at stream boundary.
    /// </summary>
    [Fact]
    public async Task SerialConnectionService_LargeMultiChunkWrite_ExactAtStreamBoundary()
    {
        var (service, factory, _) = CreateService();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var payload = BuildDeterministicPayload(20_000);
        const int chunk = 8192;
        for (var offset = 0; offset < payload.Length; offset += chunk)
        {
            var len = Math.Min(chunk, payload.Length - offset);
            await service.WriteAsync(payload.AsMemory(offset, len), CancellationToken.None);
        }

        var written = factory.Ports["COM1"].WrittenChunks.SelectMany(c => c).ToArray();
        written.Should().Equal(payload);
        service.GetStats().BytesTransmitted.Should().Be(payload.Length);
    }

    /// <summary>
    /// Scenario: fake port has output-queue drain delay.
    /// Expected: WriteAsync waits until BytesToWrite drains.
    /// </summary>
    [Fact]
    public async Task SerialConnectionService_WaitsForDriverOutputQueueDrain()
    {
        var options = new FakeSerialPortOptions
        {
            OutputQueueDrainDelay = TimeSpan.FromMilliseconds(80)
        };
        var (service, factory, _) = CreateService(options);
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        var sw = Stopwatch.StartNew();
        await service.WriteAsync("drain-me"u8.ToArray(), CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(50);
        factory.Ports["COM1"].BytesToWrite.Should().Be(0);
        service.GetStats().BytesTransmitted.Should().Be(8);
    }

    /// <summary>
    /// Scenario: output queue never drains (HoldOutputQueue).
    /// Expected: drain timeout propagates to caller.
    /// </summary>
    [Fact]
    public async Task SerialConnectionService_DrainTimeout_Propagates()
    {
        var options = new FakeSerialPortOptions { HoldOutputQueue = true };
        var (service, _, _) = CreateService(options);
        await using var _ = service;
        await service.OpenAsync(
            "COM1",
            new SerialPortConfiguration { BaudRate = 115200 },
            CancellationToken.None);

        var act = async () => await service.WriteAsync(new byte[64], CancellationToken.None);
        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*output queue did not drain*");
        service.GetStats().BytesTransmitted.Should().Be(0);
    }

    /// <summary>
    /// Scenario: base stream write faults.
    /// Expected: exception propagates; TX stats do not count failed bytes.
    /// </summary>
    [Fact]
    public async Task SerialConnectionService_WriteFailure_PropagatesAndDoesNotCountTx()
    {
        var (service, factory, _) = CreateService();
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);
        factory.Ports["COM1"].FaultWrites(new IOException("USB-serial write failed"));

        var act = async () => await service.WriteAsync("x"u8.ToArray(), CancellationToken.None);
        await act.Should().ThrowAsync<IOException>().WithMessage("*USB-serial write failed*");
        service.GetStats().BytesTransmitted.Should().Be(0);
    }

    /// <summary>
    /// Scenario: cancel during a slow write.
    /// Expected: write stops via cancellation.
    /// </summary>
    [Fact]
    public async Task SerialConnectionService_Cancellation_StopsWrite()
    {
        var options = new FakeSerialPortOptions { WriteDelay = TimeSpan.FromSeconds(2) };
        var (service, _, _) = CreateService(options);
        await using var _ = service;
        await service.OpenAsync("COM1", null, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var write = service.WriteAsync(new byte[16], cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        var act = async () => await write;
        await act.Should().ThrowAsync<OperationCanceledException>();
        service.GetStats().BytesTransmitted.Should().Be(0);
    }

    /// <summary>
    /// Scenario: serial write failure on remote TX path.
    /// Expected: failure propagates through the session handler.
    /// </summary>
    [Fact]
    public async Task RemotePath_WriteFailure_PropagatesThroughHandler()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();
        await harness.Serial.CloseAsync(CancellationToken.None);

        var act = async () => await harness.Transport.EmitSerialDataAsync("orphan"u8.ToArray());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not open*");
    }

    /// <summary>
    /// Scenario: serial WriteDelay slows TX.
    /// Expected: remote emit awaits/backpressures.
    /// </summary>
    [Fact]
    public async Task SlowSerialWrite_BackpressuresRemoteEmit()
    {
        await using var harness = new SessionHarness { WriteDelay = TimeSpan.FromMilliseconds(60) };
        await harness.StartAsync();

        var sw = Stopwatch.StartNew();
        await harness.Transport.EmitSerialDataAsync(new byte[32]);
        await harness.Transport.EmitSerialDataAsync(new byte[32]);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(100);
        harness.Serial.WriteCount.Should().Be(2);
        harness.Serial.GetStats().BytesTransmitted.Should().Be(64);
    }

    /// <summary>
    /// Scenario: local WriteAsync alongside remote TX.
    /// Expected: both paths can write without corruption.
    /// </summary>
    [Fact]
    public async Task InteractiveLocalWrite_StillWorksAlongsideRemoteTx()
    {
        await using var harness = new SessionHarness();
        await harness.StartAsync();

        await harness.Serial.WriteAsync("local"u8.ToArray(), CancellationToken.None);
        await harness.Transport.EmitSerialDataAsync("remote"u8.ToArray());

        harness.Serial.Writes.Should().HaveCount(2);
        harness.Serial.Writes[0].Should().Equal("local"u8.ToArray());
        harness.Serial.Writes[1].Should().Equal("remote"u8.ToArray());
    }

    /// <summary>
    /// Builds a deterministic payload of the requested length for integrity checks.
    /// </summary>
    private static byte[] BuildDeterministicPayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        // Embed awkward sequences at fixed offsets when length allows.
        if (length >= 16)
        {
            payload[0] = 0x00;
            payload[1] = 0xFF;
            payload[2] = 0x00;
            payload[3] = 0x0D;
            payload[4] = 0x0A;
            payload[5] = 0x80;
            payload[6] = 0xBF;
        }

        return payload;
    }

    /// <summary>
    /// Session harness using FakeSerialConnectionService/FakeRemoteTransport for remote TX path tests.
    /// </summary>
    private sealed class SessionHarness : IAsyncDisposable
    {
        /// <summary>
        /// Fake serial connection used by the session harness.
        /// </summary>
        public FakeSerialConnectionService Serial { get; } = new();
        /// <summary>
        /// Fake remote transport used by the session harness.
        /// </summary>
        public FakeRemoteTransport Transport { get; } = new();
        /// <summary>
        /// RemoteSessionService under test.
        /// </summary>
        public RemoteSessionService Session { get; private set; } = null!;
        /// <summary>
        /// Optional WriteAsync delay applied to the fake serial service.
        /// </summary>
        public TimeSpan? WriteDelay { get; init; }

        /// <summary>
        /// Opens serial (optional write delay) and starts the remote session.
        /// </summary>
        public async Task StartAsync()
        {
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-19T08:00:00Z"));
            var options = new OptionsMonitorStub<RemoteServiceOptions>(new RemoteServiceOptions
            {
                Host = "remote.contoso.test",
                Port = 9443,
                TlsHostName = "remote.contoso.test",
                Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 4096 },
                Reconnect = new ReconnectOptions
                {
                    InitialDelaySeconds = 1,
                    MaxDelaySeconds = 4,
                    BackoffMultiplier = 2,
                    JitterFactor = 0
                }
            });
            Session = new RemoteSessionService(
                Serial,
                Transport,
                new ReconnectCoordinator(
                    options,
                    time,
                    new FixedJitterProvider(0.5),
                    new CollectingLogger<ReconnectCoordinator>()),
                options,
                time,
                new CollectingLogger<RemoteSessionService>());

            Serial.WriteDelay = WriteDelay;
            await Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
            await Session.StartAsync("COM4", CancellationToken.None);
            await TestHelpers.WaitUntilAsync(() => Transport.IsConnected);
        }

        /// <summary>
        /// Disposes the session and harness resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Session is not null)
            {
                await Session.DisposeAsync();
            }

            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }
}
