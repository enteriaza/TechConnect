// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRxBridgeFailureTests.cs
//
// Purpose:
// Verifies serial RX → remote bridge lifecycle: normal operation, remote down,
// unexpected bridge failure, cancellation, and shutdown (REL-02).
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Lifecycle tests for the serial RX bridge inside <see cref="RemoteSessionService"/>.
/// </summary>
public sealed class SerialRxBridgeFailureTests
{
    /// <summary>
    /// Fake serial/transport harness for bridge tests.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public FakeSerialConnectionService Serial { get; } = new();
        public FakeRemoteTransport Transport { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        public RemoteSessionService Session { get; }
        public CollectingLogger<RemoteSessionService> Logger { get; } = new();

        public Harness()
        {
            var options = new RemoteServiceOptions
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

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Scenario: serial RX while remote is connected.
    /// Expected: bridge forwards bytes to the transport.
    /// </summary>
    [Fact]
    public async Task NormalBridge_ForwardsRxToTransport()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Serial.InjectRx("bridge-ok"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => h.Transport.SentPayloads.Count > 0);

        h.Transport.SentPayloads.Should().ContainSingle()
            .Which.Should().Equal("bridge-ok"u8.ToArray());
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: remote transport unavailable while serial RX continues.
    /// Expected: serial stays open; outage bytes discarded for remote (not queued).
    /// </summary>
    [Fact]
    public async Task RemoteUnavailable_SerialRxContinues_WithoutRemoteQueueGrowth()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("down"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        var payload = "still-local"u8.ToArray();
        h.Serial.InjectRx(payload);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesDropped >= payload.Length,
            because: "bridge should discard while remote is down");

        h.Serial.IsOpen.Should().BeTrue();
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);
        h.Transport.SentPayloads.Should().BeEmpty();
        h.Session.State.Should().NotBe(RemoteSessionState.Error);
    }

    /// <summary>
    /// Scenario: Incoming channel faults unexpectedly while the session is active.
    /// Expected: Error state, Faulted event, error log, serial remains open, no silent hang.
    /// </summary>
    [Fact]
    public async Task UnexpectedBridgeFailure_IsObservable_AndKeepsSerialOpen()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        string? fault = null;
        h.Session.Faulted += (_, message) => fault = message;

        h.Serial.CompleteIncoming(new InvalidOperationException("simulated Incoming fault"));

        await TestHelpers.WaitUntilAsync(
            () => h.Session.State == RemoteSessionState.Error,
            TimeSpan.FromSeconds(5),
            "bridge failure should move the session to Error");

        fault.Should().Be("Serial to remote bridge failed.");
        h.Serial.IsOpen.Should().BeTrue();
        h.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("Serial→remote RX bridge failed", StringComparison.Ordinal));

        // Producer cannot keep writing into a fault-completed channel (bounded outcome).
        var act = () => h.Serial.InjectRx("after-fault"u8.ToArray());
        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Scenario: cancel/stop while bridge is running.
    /// Expected: Disconnected, not reported as unexpected bridge failure Error.
    /// </summary>
    [Fact]
    public async Task CancellationDuringBridge_IsNotReportedAsUnexpectedFailure()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Serial.InjectRx("pump"u8.ToArray());
        await TestHelpers.WaitUntilAsync(() => h.Transport.SentPayloads.Count >= 1);

        await h.Session.StopAsync(CancellationToken.None);

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Serial.IsOpen.Should().BeTrue();
        h.Logger.Entries.Should().NotContain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("Serial→remote RX bridge failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: normal Stop after connected session.
    /// Expected: clean disconnect; serial remains open.
    /// </summary>
    [Fact]
    public async Task NormalShutdown_CompletesCleanly()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        await h.Session.StopAsync(CancellationToken.None);

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Session.SessionId.Should().BeNull();
        h.Serial.IsOpen.Should().BeTrue();
        h.Transport.IsConnected.Should().BeFalse();
    }
}
