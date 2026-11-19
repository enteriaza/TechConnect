// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SessionPingTests.cs
//
// Purpose:
// Verifies remote session ping/heartbeat scheduling and failure handling.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Verifies session ping handling does not couple to the serial path.
/// </summary>
public sealed class SessionPingTests
{
    /// <summary>
    /// Scenario: connected session receives a ping control message.
    /// Expected: ping reply is sent on the transport and serial write count stays zero.
    /// </summary>
    [Fact]
    public async Task SessionPing_DoesNotTouchSerial_AndReplies()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var writesBefore = h.Serial.WriteCount;

        // Mirror TcpMtlsRemoteConsoleTransport.DispatchSessionPing: legacy envelope with a
        // source-gen-registered SessionPingMessage payload (anonymous types are not registered).
        var legacy = ControlProtocol.Create(
            ControlProtocol.TypeSessionPing,
            new SessionPingMessage { CorrelationId = "sp-1" },
            "sp-1");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(legacy));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("session.pingResult", StringComparison.Ordinal) &&
                m.Contains("sp-1", StringComparison.Ordinal)),
            because: "connector should reply to session.ping without using serial");

        h.Serial.WriteCount.Should().Be(writesBefore);
    }

    /// <summary>
    /// Fake serial/transport/time harness hosting a real RemoteSessionService for ping tests.
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
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-18T12:00:00Z"));
        /// <summary>
        /// RemoteSessionService under test.
        /// </summary>
        public RemoteSessionService Session { get; }

        /// <summary>
        /// Creates the harness with default remote options.
        /// </summary>
        public Harness()
        {
            var options = new RemoteServiceOptions
            {
                Host = "remote.contoso.test",
                Port = 9443,
                TlsHostName = "remote.contoso.test",
                Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 65536 },
                Reconnect = new ReconnectOptions
                {
                    InitialDelaySeconds = 1,
                    MaxDelaySeconds = 2,
                    BackoffMultiplier = 1,
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
}
