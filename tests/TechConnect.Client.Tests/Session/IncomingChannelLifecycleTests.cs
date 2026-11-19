// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    IncomingChannelLifecycleTests.cs
//
// Purpose:
// Verifies PERF-CLI-03 remediation: Incoming publication is gated by remote
// ingress and cannot grow unboundedly without a bridge consumer.
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
/// Lifecycle tests for Incoming channel growth across remote session states (PERF-CLI-03).
/// </summary>
public sealed class IncomingChannelLifecycleTests
{
    /// <summary>
    /// Configurable harness for Incoming lifecycle scenarios.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public FakeSerialConnectionService Serial { get; } = new();
        public FakeRemoteTransport Transport { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-19T12:00:00Z"));
        public RemoteSessionService Session { get; }
        public CollectingLogger<RemoteSessionService> Logger { get; } = new();

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

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Scenario A: remote endpoint unconfigured while serial RX continues.
    /// Expected: ingress stays disabled; Incoming does not accumulate; serial stays open.
    /// </summary>
    [Fact]
    public async Task UnconfiguredRemote_SerialRx_DoesNotGrowIncoming()
    {
        await using var h = new Harness(new RemoteServiceOptions
        {
            Host = string.Empty,
            Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 4096 }
        });

        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);

        h.Session.State.Should().Be(RemoteSessionState.Error);
        h.Serial.IsRemoteIngressEnabled.Should().BeFalse();

        var uiBytes = 0;
        h.Serial.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                Interlocked.Add(ref uiBytes, chunk.Data.Length);
            }
        };

        const int bursts = 40;
        const int burstSize = 1024;
        for (var i = 0; i < bursts; i++)
        {
            h.Serial.InjectRx(new byte[burstSize]);
        }

        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref uiBytes) >= bursts * burstSize);

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.GetReceiveDiagnostics().BytesPublishedToIncomingChannel.Should().Be(0);
        h.Serial.GetReceiveDiagnostics().BytesSkippedRemoteIngressDisabled.Should().Be(bursts * burstSize);
        h.Serial.Incoming.TryRead(out _).Should().BeFalse();
        h.Transport.ConnectCallCount.Should().Be(0);
    }

    /// <summary>
    /// Scenario B: remote unavailable during reconnect while serial RX continues.
    /// Expected: bridge discards for remote; outbound queue stays bounded; serial open.
    /// </summary>
    [Fact]
    public async Task ReconnectOutage_SerialRx_DoesNotGrowRemoteQueueOrAbandonIncoming()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        h.Serial.IsRemoteIngressEnabled.Should().BeTrue();

        h.Transport.SetConnectException(new RemoteTransportUnavailableException("down"));
        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));

        const int total = 8 * 1024;
        h.Serial.InjectRx(new byte[total]);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesDropped >= total,
            because: "bridge must discard while remote is down");

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.IsRemoteIngressEnabled.Should().BeTrue();
        h.Session.GetSnapshot().Delivery.BytesQueued.Should().Be(0);
        h.Session.State.Should().NotBe(RemoteSessionState.Error);
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
    }

    /// <summary>
    /// Scenario C: remote transport slower than serial RX.
    /// Expected: RemoteOutboundBuffer bounds queue; serial remains open; Incoming drained by bridge.
    /// </summary>
    [Fact]
    public async Task SlowRemoteTransport_OutboundBufferBounds_IncomingDrained()
    {
        await using var h = new Harness(new RemoteServiceOptions
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

        h.Transport.SendDelay = TimeSpan.FromMilliseconds(50);
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        for (var i = 0; i < 20; i++)
        {
            h.Serial.InjectRx(new byte[512]);
        }

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().Delivery.BytesAccepted >= 20 * 512,
            TimeSpan.FromSeconds(5));

        var snap = h.Session.GetSnapshot().Delivery;
        // Queued is hard-capped; one dequeued in-flight chunk may sit outside the queue budget.
        snap.BytesQueued.Should().BeLessThanOrEqualTo(4096);
        h.Serial.IsOpen.Should().BeTrue();

        await TestHelpers.WaitUntilAsync(
            () => h.Serial.GetReceiveDiagnostics().PendingIncomingItems == 0,
            TimeSpan.FromSeconds(5),
            "bridge should drain Incoming into the bounded outbound buffer");
    }

    /// <summary>
    /// Scenario D/E: unexpected bridge failure triggers REL-02 discard-drain then disables ingress.
    /// Expected: Error state, serial open, no post-failure Incoming growth.
    /// </summary>
    [Fact]
    public async Task UnexpectedBridgeFailure_Finalizes_AndDisablesIngress()
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
            TimeSpan.FromSeconds(5));

        await TestHelpers.WaitUntilAsync(
            () => !h.Serial.IsRemoteIngressEnabled,
            TimeSpan.FromSeconds(5),
            "finalize must disable remote ingress after bridge stop");

        fault.Should().Be("Serial to remote bridge failed.");
        h.Serial.IsOpen.Should().BeTrue();

        var uiBytes = 0;
        h.Serial.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                Interlocked.Add(ref uiBytes, chunk.Data.Length);
            }
        };

        h.Serial.InjectRx(new byte[2048]);
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref uiBytes) >= 2048);

        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.GetReceiveDiagnostics().BytesPublishedToIncomingChannel.Should().Be(0);
        h.Serial.Incoming.TryRead(out _).Should().BeFalse();
        h.Logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("Serial→remote RX bridge failed", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario F: client shutdown while serial RX is active.
    /// Expected: Stop completes; ingress disabled; Incoming empty; serial remains open.
    /// </summary>
    [Fact]
    public async Task ShutdownWhileSerialRxActive_DisablesIngress_AndDrainsIncoming()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Serial.InjectRx(new byte[256]);
        await TestHelpers.WaitUntilAsync(() => h.Transport.SentPayloads.Count >= 1);

        var stop = h.Session.StopAsync(CancellationToken.None);
        h.Serial.InjectRx(new byte[1024]);
        await stop;

        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.IsRemoteIngressEnabled.Should().BeFalse();
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);

        var uiBytes = 0;
        h.Serial.DataTransferred += (_, chunk) =>
        {
            if (chunk.Direction == SerialDataDirection.Rx)
            {
                Interlocked.Add(ref uiBytes, chunk.Data.Length);
            }
        };

        h.Serial.InjectRx(new byte[4096]);
        await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref uiBytes) >= 4096);
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.Incoming.TryRead(out _).Should().BeFalse();
    }

    /// <summary>
    /// Scenario: local serial remains independent of remote failure / stop.
    /// Expected: open stays true across unconfigured, outage discard, and stop.
    /// </summary>
    [Fact]
    public async Task LocalSerialRemainsIndependent_OfRemoteLifecycle()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Serial.InjectRx("local-1"u8.ToArray());
        h.Serial.IsOpen.Should().BeTrue();

        await h.Session.StopAsync(CancellationToken.None);
        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.InjectRx("local-2"u8.ToArray());
        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.IsRemoteIngressEnabled.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: TryWrite fails (completed Incoming) while ingress is enabled.
    /// Expected: exception path still clears in-flight; disable does not hang.
    /// </summary>
    [Fact]
    public async Task CompletedIncoming_FailedPublish_DoesNotHangDisable()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        h.Serial.SetRemoteIngressEnabled(true);

        h.Serial.CompleteIncoming(new InvalidOperationException("channel fault"));

        var act = () => h.Serial.InjectRx(new byte[32]);
        act.Should().Throw<InvalidOperationException>();

        var disable = Task.Run(() =>
        {
            h.Serial.SetRemoteIngressEnabled(false);
            _ = h.Serial.DrainPendingRemoteIngress();
        });

        var finished = await Task.WhenAny(disable, Task.Delay(TimeSpan.FromSeconds(5)));
        finished.Should().Be(disable, because: "in-flight must clear on TryWrite failure via finally");
        await disable;

        h.Serial.IsRemoteIngressEnabled.Should().BeFalse();
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: concurrent InjectRx while disable + drain runs (PERF-CLI-03 race).
    /// Expected: after disable returns and drain completes, Incoming stays empty forever.
    /// </summary>
    [Fact]
    public async Task ConcurrentRxDuringDisableDrain_LeavesNoAbandonedIncoming()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);
        await h.Session.StartAsync("COM3", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        h.Serial.IsRemoteIngressEnabled.Should().BeTrue();

        using var injectCts = new CancellationTokenSource();
        var injectTask = Task.Run(() =>
        {
            var payload = new byte[128];
            while (!injectCts.IsCancellationRequested)
            {
                try
                {
                    h.Serial.InjectRx(payload);
                }
                catch (InvalidOperationException)
                {
                    // Channel completed during teardown races — ignore.
                }
            }
        });

        // Let producers run briefly while the bridge is active.
        await Task.Delay(20);
        await h.Session.StopAsync(CancellationToken.None);
        injectCts.Cancel();
        await injectTask;

        h.Serial.IsRemoteIngressEnabled.Should().BeFalse();
        h.Serial.DrainPendingRemoteIngress().Should().Be(0);
        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.Incoming.TryRead(out _).Should().BeFalse();
        h.Serial.IsOpen.Should().BeTrue();

        // Post-disable publishes must not accumulate.
        for (var i = 0; i < 100; i++)
        {
            h.Serial.InjectRx(new byte[64]);
        }

        h.Serial.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
        h.Serial.Incoming.TryRead(out _).Should().BeFalse();
    }
}
