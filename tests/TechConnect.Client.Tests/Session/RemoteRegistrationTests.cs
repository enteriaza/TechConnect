// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteRegistrationTests.cs
//
// Purpose:
// Verifies connector hello/registration handshake and rejection paths.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Verifies connector hello/registration handshake and failure isolation from serial.
/// </summary>
public sealed class RemoteRegistrationTests
{
    /// <summary>
    /// Fake serial/transport harness for registration handshake tests.
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
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-18T10:00:00Z"));
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
                Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 4096 },
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

    /// <summary>
    /// Scenario: session connects successfully.
    /// Expected: connector.hello is sent including application version.
    /// </summary>
    [Fact]
    public async Task Hello_SentAfterConnect_IncludesApplicationVersion()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        await TestHelpers.WaitUntilAsync(() => !h.Transport.SentControlMessages.IsEmpty);

        h.Transport.SentControlMessages.TryPeek(out var json).Should().BeTrue();
        ControlProtocol.TryParse(json!, out var message, out var error).Should().BeTrue(error);
        message!.Type.Should().Be(ControlProtocol.TypeConnectorHello);
        ControlProtocol.TryGetPayload(message, out ConnectorHelloPayload? hello, out _).Should().BeTrue();
        hello!.ApplicationVersion.Should().NotBeNullOrWhiteSpace();
        hello.PortName.Should().Be("COM7");
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: successful hello result from remote.
    /// Expected: server session id is stored on the snapshot.
    /// </summary>
    [Fact]
    public async Task SuccessfulHelloResult_StoresServerSessionId()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var payload = Encoding.UTF8.GetString(ControlMessageSerializer.SerializeHelloResult(
            new HelloResultMessage
            {
                Success = true,
                ServerName = "TechConnect.Server",
                SessionId = "a82f31c2"
            }));
        h.Transport.EmitControlMessage(payload);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().ServerSessionId == "a82f31c2",
            because: "successful HelloResult should retain the server session id");
        h.Session.GetSnapshot().LastRemoteError.Should().BeNull();
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: failed hello result.
    /// Expected: error reported while serial remains open.
    /// </summary>
    [Fact]
    public async Task FailedHelloResult_ReportsError_DoesNotCloseSerial()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var payload = Encoding.UTF8.GetString(ControlMessageSerializer.SerializeHelloResult(
            new HelloResultMessage
            {
                Success = false,
                Error = "A connector with this certificate identity is already registered."
            }));
        h.Transport.EmitControlMessage(payload);

        await TestHelpers.WaitUntilAsync(
            () => h.Session.GetSnapshot().LastRemoteError is not null,
            because: "failed HelloResult should surface registration failure");
        h.Session.GetSnapshot().ServerSessionId.Should().BeNull();
        h.Session.GetSnapshot().LastRemoteError.Should().Contain("already registered");
        h.Serial.IsOpen.Should().BeTrue();
        h.Session.State.Should().Be(RemoteSessionState.ConnectedWaitingForNoc);
    }

    /// <summary>
    /// Scenario: disconnect after a successful hello.
    /// Expected: server session id cleared.
    /// </summary>
    [Fact]
    public async Task Disconnect_ClearsServerSessionId()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.EmitControlMessage(Encoding.UTF8.GetString(ControlMessageSerializer.SerializeHelloResult(
            new HelloResultMessage { Success = true, SessionId = "deadbeef", ServerName = "TechConnect.Server" })));
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().ServerSessionId == "deadbeef");

        await h.Session.StopAsync(CancellationToken.None);
        h.Session.GetSnapshot().ServerSessionId.Should().BeNull();
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: reconnect after drop.
    /// Expected: a new server session id is accepted.
    /// </summary>
    [Fact]
    public async Task Reconnect_ReceivesNewSessionId()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Transport.EmitControlMessage(Encoding.UTF8.GetString(ControlMessageSerializer.SerializeHelloResult(
            new HelloResultMessage { Success = true, SessionId = "aaaaaaaa", ServerName = "TechConnect.Server" })));
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().ServerSessionId == "aaaaaaaa");

        h.Transport.SimulateDrop();
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.ConnectCallCount >= 2 && h.Transport.IsConnected,
            timeout: TimeSpan.FromSeconds(5),
            because: "transport should reconnect after drop");

        h.Session.GetSnapshot().ServerSessionId.Should().BeNull(
            because: "prior server session id clears on reconnect before new HelloResult");

        h.Transport.EmitControlMessage(Encoding.UTF8.GetString(ControlMessageSerializer.SerializeHelloResult(
            new HelloResultMessage { Success = true, SessionId = "bbbbbbbb", ServerName = "TechConnect.Server" })));
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().ServerSessionId == "bbbbbbbb");
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: remote connect fails.
    /// Expected: local serial console path remains usable.
    /// </summary>
    [Fact]
    public async Task RemoteConnectionFailure_DoesNotBlockLocalConsole()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM7", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        h.Transport.SetConnectException(new RemoteTransportUnavailableException("down"));

        await h.Session.StartAsync("COM7", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(
            () => h.Session.State is RemoteSessionState.WaitingForRemoteService or RemoteSessionState.Reconnecting);

        h.Serial.IsOpen.Should().BeTrue();
        h.Session.GetSnapshot().ServerSessionId.Should().BeNull();
        await h.Serial.WriteAsync("local-still-works"u8.ToArray(), CancellationToken.None);
        h.Serial.Writes.Should().NotBeEmpty();
    }
}
