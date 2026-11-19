// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRemotePipelineTests.cs
//
// Purpose:
// Verifies serial-to-remote pipeline integration with fakes (RX forward and TX write-back).
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Integration;

/// <summary>
/// Integration of serial RX/TX bridges through RemoteSessionService with fakes.
/// </summary>
public sealed class SerialRemotePipelineTests
{
    /// <summary>
    /// Fake serial/transport harness for pipeline integration tests.
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
                new CollectingLogger<RemoteSessionService>());
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
    /// Scenario: serial RX while session connected.
    /// Expected: transport receives ordered serial payloads.
    /// </summary>
    [Fact]
    public async Task RxPath_SerialBytesAppearOnTransportInOrder()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", null, CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var payloads = new[]
        {
            "one"u8.ToArray(),
            "two"u8.ToArray(),
            "three"u8.ToArray()
        };

        foreach (var payload in payloads)
        {
            h.Serial.InjectRx(payload);
        }

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentPayloads.Count >= payloads.Length,
            because: "all RX chunks should drain to the transport in order");

        h.Transport.SentPayloads.Should().HaveCount(payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
        {
            h.Transport.SentPayloads.ElementAt(i).Should().Equal(payloads[i]);
        }
    }

    /// <summary>
    /// Scenario: RX during disconnect then reconnect.
    /// Expected: local continues; disconnected bytes not replayed.
    /// </summary>
    [Fact]
    public async Task RxWhileDisconnected_LocalContinues_NotReplayedAfterReconnect()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", null, CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        // Pre-disconnect byte that may still be in the outbound path when the drop hits.
        h.Serial.InjectRx("pre-drop"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => !h.Transport.SentPayloads.IsEmpty);

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("outage"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var duringOutage = "rx-outage"u8.ToArray();
        var inject = () => h.Serial.InjectRx(duringOutage);
        inject.Should().NotThrow();
        await h.Serial.WriteAsync("local-tx-during-outage"u8.ToArray(), CancellationToken.None);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesDropped >= duringOutage.Length,
            because: "outage RX is counted dropped, not held for reconnect");

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.Writes.Any(w => w.SequenceEqual("local-tx-during-outage"u8.ToArray())).Should().BeTrue();
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);

        var sentBeforeReconnect = h.Transport.SentPayloads.ToArray();

        h.Transport.SetConnectException(null);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        h.Time.Advance(TimeSpan.FromMilliseconds(250));
        await Task.Delay(100);

        var sentAfter = h.Transport.SentPayloads.ToArray();
        sentAfter.Should().Equal(sentBeforeReconnect, "reconnect must not transmit outage or stale queued bytes");
        sentAfter.Any(p => p.SequenceEqual(duringOutage)).Should().BeFalse();
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: transport emits serial data.
    /// Expected: serial writes preserve order.
    /// </summary>
    [Fact]
    public async Task TxPath_TransportEmitWritesSerialInOrder()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", null, CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var payloads = new[]
        {
            "alpha"u8.ToArray(),
            "beta"u8.ToArray(),
            "gamma"u8.ToArray()
        };

        foreach (var payload in payloads)
        {
            h.Transport.EmitSerialData(payload);
        }

        await TestHelpers.WaitUntilAsync(
            () => h.Serial.WriteCount >= payloads.Length,
            because: "remote TX should write to serial in order");

        h.Serial.Writes.Should().HaveCount(payloads.Length);
        for (var i = 0; i < payloads.Length; i++)
        {
            h.Serial.Writes[i].Should().Equal(payloads[i]);
        }
    }

    /// <summary>
    /// Scenario: EmitSerialData after Stop.
    /// Expected: no serial writes.
    /// </summary>
    [Fact]
    public async Task AfterStop_EmitSerialData_DoesNotWrite()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", null, CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        await h.Session.StopAsync(CancellationToken.None);
        h.Session.State.Should().Be(RemoteSessionState.Disconnected);

        h.Transport.EmitSerialData("obsolete"u8.ToArray());
        await Task.Delay(100);
        h.Serial.WriteCount.Should().Be(0);
    }
}
