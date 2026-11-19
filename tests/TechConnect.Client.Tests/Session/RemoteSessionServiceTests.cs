// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteSessionServiceTests.cs
//
// Purpose:
// Verifies RemoteSessionService connect/disconnect lifecycle, delivery, and fault handling with fakes.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Lifecycle, reconnect, RX/TX bridge, and shutdown tests for RemoteSessionService with fakes.
/// </summary>
public sealed class RemoteSessionServiceTests
{
    /// <summary>
    /// Fake serial/transport/time harness hosting RemoteSessionService.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>
        /// Fake serial connection used by the harness.
        /// </summary>
        public FakeSerialConnectionService Serial { get; } = new();
        /// <summary>
        /// Fake remote transport used by the harness.
        /// </summary>
        public FakeRemoteTransport Transport { get; } = new();
        /// <summary>
        /// Fake time provider driving reconnect/session timers.
        /// </summary>
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        /// <summary>
        /// RemoteSessionService under test.
        /// </summary>
        public RemoteSessionService Session { get; }
        /// <summary>
        /// Collecting logger for RemoteSessionService.
        /// </summary>
        public CollectingLogger<RemoteSessionService> Logger { get; } = new();

        /// <summary>
        /// Creates the harness with optional RemoteServiceOptions overrides.
        /// </summary>
        public Harness(RemoteServiceOptions? options = null)
        {
            options ??= new RemoteServiceOptions
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
            };

            var opts = new OptionsMonitorStub<RemoteServiceOptions>(options);
            var reconnect = new ReconnectCoordinator(
                opts,
                Time,
                new FixedJitterProvider(0.5),
                new CollectingLogger<ReconnectCoordinator>());

            Session = new RemoteSessionService(
                Serial,
                Transport,
                reconnect,
                opts,
                Time,
                Logger);
        }

        /// <summary>
        /// Disposes the session and harness resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Scenario: StartAsync without an open serial port.
    /// Expected: fails/requires open serial.
    /// </summary>
    [Fact]
    public async Task Start_RequiresOpenSerial()
    {
        await using var h = new Harness();
        var act = async () => await h.Session.StartAsync("COM1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*serial port must be open*");
    }

    /// <summary>
    /// Scenario: remote unavailable then becomes available.
    /// Expected: reconnect succeeds once available.
    /// </summary>
    [Fact]
    public async Task RemoteUnavailableThenAvailable_Reconnects()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        h.Transport.SetConnectException(new RemoteTransportUnavailableException("down"));

        await h.Session.StartAsync("COM3", CancellationToken.None);

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.ConnectCallCount >= 1,
            because: "first connect should be attempted");

        h.Session.GetSnapshot().RemoteStatus.Should().BeOneOf(
            RemoteSessionState.WaitingForRemoteService,
            RemoteSessionState.Connecting,
            RemoteSessionState.Reconnecting);

        h.Transport.SetConnectException(null);
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.IsConnected,
            TimeSpan.FromSeconds(5),
            "transport should connect after becoming available");

        // MaintainTransportAsync polls every 1s via FakeTimeProvider — advance to keep session alive.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Session.GetSnapshot().ClientSessionId.Should().NotBeNullOrEmpty();
        h.Session.GetSnapshot().ServerSessionId.Should().BeNull();
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: transport drops mid-session.
    /// Expected: serial stays open and reconnect proceeds.
    /// </summary>
    [Fact]
    public async Task TransportDrop_KeepsSerialOpen_AndReconnects()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);

        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        var sessionId = h.Session.SessionId;
        sessionId.Should().NotBeNull();

        h.Transport.SimulateDrop();
        h.Serial.IsOpen.Should().BeTrue();

        // Leave the connected wait loop, then backoff, then reconnect.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.IsConnected && h.Transport.ConnectCallCount >= 2,
            TimeSpan.FromSeconds(5),
            "should reconnect after drop");

        h.Session.SessionId.Should().Be(sessionId, because: "ClientSessionId is stable for the session");
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: Stop after connected.
    /// Expected: no further reconnect; session id cleared.
    /// </summary>
    [Fact]
    public async Task Stop_PreventsFurtherReconnect_AndClearsSessionId()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var before = h.Transport.ConnectCallCount;
        await h.Session.StopAsync(CancellationToken.None);

        h.Session.SessionId.Should().BeNull();
        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Session.GetSnapshot().ServerSessionId.Should().BeNull();

        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        h.Transport.ConnectCallCount.Should().Be(before);
    }

    /// <summary>
    /// Scenario: Stop while RX bridge and TX drain are active.
    /// Expected: stop completes; serial remains open.
    /// </summary>
    [Fact]
    public async Task Stop_while_rx_bridge_and_tx_drain_active_completes_and_keeps_serial_open()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Serial.InjectRx("keep-pumping"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => h.Transport.SentPayloads.Count >= 1);

        var stop = h.Session.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(8));

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Serial.IsOpen.Should().BeTrue();
        h.Transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: WSS/transport failure after serial open.
    /// Expected: local RX/TX remain usable.
    /// </summary>
    [Fact]
    public async Task Wss_failure_leaves_serial_rx_and_tx_usable()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SimulateDrop();
        h.Serial.IsOpen.Should().BeTrue();

        await h.Serial.WriteAsync("local-tx"u8.ToArray(), CancellationToken.None);
        h.Serial.InjectRx("local-rx"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => h.Serial.GetStats().BytesReceived > 0);

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.GetStats().BytesTransmitted.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Scenario: transport callbacks fire after Stop.
    /// Expected: no serial writes from obsolete callbacks.
    /// </summary>
    [Fact]
    public async Task ObsoleteTransportCallbacks_AfterStop_DoNotWrite()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        await h.Session.StopAsync(CancellationToken.None);

        h.Transport.EmitSerialData("late"u8.ToArray());
        await Task.Delay(50);
        h.Serial.WriteCount.Should().Be(0);
    }

    /// <summary>
    /// Scenario: dispose/stop during Connecting.
    /// Expected: completes without hang.
    /// </summary>
    [Fact]
    public async Task Shutdown_DuringConnecting_Completes()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        h.Transport.HoldNextConnect();

        var start = h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.ConnectCallCount >= 1);
        await start;

        var stop = h.Session.StopAsync(CancellationToken.None);
        h.Transport.ReleaseHeldConnect();
        await stop;

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Session.SessionId.Should().BeNull();
    }

    /// <summary>
    /// Scenario: dispose/stop during Reconnecting.
    /// Expected: completes without hang.
    /// </summary>
    [Fact]
    public async Task Shutdown_DuringReconnecting_Completes()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("outage"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.Session.State is RemoteSessionState.Reconnecting or RemoteSessionState.WaitingForRemoteService,
            because: "should enter reconnect after drop");

        await h.Session.StopAsync(CancellationToken.None);
        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
    }

    /// <summary>
    /// Scenario: shutdown during reconnect delay.
    /// Expected: completes without waiting full backoff.
    /// </summary>
    [Fact]
    public async Task Shutdown_DuringReconnectDelay_CompletesWithoutWaitingFullBackoff()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        h.Transport.SetConnectException(new RemoteTransportUnavailableException("down"));
        await h.Session.StartAsync("COM3", CancellationToken.None);

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.ConnectCallCount >= 1,
            because: "first connect attempt should fail into backoff");

        // Stop while coordinator would be waiting on backoff (do not advance fake time).
        var stop = h.Session.StopAsync(CancellationToken.None);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: shutdown with queued outbound bytes.
    /// Expected: completes cleanly.
    /// </summary>
    [Fact]
    public async Task Shutdown_WithQueuedOutbound_Completes()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("outage"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        h.Serial.InjectRx(new byte[512]);
        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesQueued > 0 ||
                  h.Session.GetSnapshot().Delivery.BytesAccepted > 0);

        await h.Session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
    }

    /// <summary>
    /// Scenario: serial RX while connected.
    /// Expected: bytes enqueued/sent on transport.
    /// </summary>
    [Fact]
    public async Task RxPath_EnqueuesToTransport()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Serial.InjectRx("abc"u8.ToArray());

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentPayloads.Count > 0,
            because: "RX should drain to remote transport");

        h.Transport.SentPayloads.Should().ContainSingle()
            .Which.Should().Equal("abc"u8.ToArray());
        h.Session.GetSnapshot().Delivery.BytesSent.Should().Be(3);
    }

    /// <summary>
    /// Scenario: transport emits serial data.
    /// Expected: serial service receives ordered writes.
    /// </summary>
    [Fact]
    public async Task TxPath_TransportToSerialWrites()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.EmitSerialData("xyz"u8.ToArray());

        await TestHelpers.WaitUntilAsync(
            () => h.Serial.WriteCount > 0,
            because: "remote TX should write to serial");

        h.Serial.Writes.Should().ContainSingle().Which.Should().Equal("xyz"u8.ToArray());
    }

    /// <summary>
    /// Scenario: RX during remote outage then reconnect.
    /// Expected: local serial continues; outage bytes are not replayed.
    /// </summary>
    [Fact]
    public async Task RemoteOutage_LocalSerialContinues_OutageBytesNotReplayed()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("outage"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var duringOutage = "during-outage"u8.ToArray();
        h.Serial.InjectRx(duringOutage);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesDropped >= duringOutage.Length,
            because: "outage RX is discarded for remote delivery, not queued");

        h.Serial.IsOpen.Should().BeTrue();
        h.Transport.SentPayloads.Should().BeEmpty();
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);

        h.Transport.SetConnectException(null);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        h.Time.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(100);

        h.Transport.SentPayloads.Any(p => p.SequenceEqual(duringOutage)).Should().BeFalse(
            "outage bytes must not be replayed after reconnect");
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);
        h.Serial.IsOpen.Should().BeTrue();

        // Fresh RX after reconnect is eligible for remote send.
        var afterReconnect = "after-reconnect"u8.ToArray();
        h.Serial.InjectRx(afterReconnect);
        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentPayloads.Any(p => p.SequenceEqual(afterReconnect)),
            TimeSpan.FromSeconds(5),
            "post-reconnect RX should send on the new connection");
    }
}
