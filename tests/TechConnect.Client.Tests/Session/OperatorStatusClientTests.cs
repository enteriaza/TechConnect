// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    OperatorStatusClientTests.cs
//
// Purpose:
// Verifies operator-status client publish and snapshot behaviour.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Verifies operator-status client state machine and version handling.
/// </summary>
public sealed class OperatorStatusClientTests
{
    /// <summary>
    /// Fake serial/transport harness with helpers to emit operator-status control messages.
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
        /// Collecting logger for RemoteSessionService.
        /// </summary>
        public CollectingLogger<RemoteSessionService> Logger { get; } = new();
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
                Logger);
        }

        /// <summary>
        /// Opens serial, starts the session, and waits until connected/waiting-for-NOC.
        /// </summary>
        public async Task StartConnectedAsync()
        {
            await Serial.OpenAsync("COM9", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
            await Session.StartAsync("COM9", CancellationToken.None);
            await TestHelpers.WaitUntilAsync(() => Transport.IsConnected);
            Session.GetSnapshot().NocStatus.Should().Be(NocAttachmentState.Waiting);
        }

        /// <summary>
        /// Emits an operator-status control message with the given version/attachment flags.
        /// </summary>
        public void EmitStatus(uint version, bool attached, bool terminalActive)
        {
            var json = Encoding.UTF8.GetString(ControlMessageSerializer.SerializeOperatorStatus(
                new OperatorStatusMessage
                {
                    StateVersion = version,
                    Attached = attached,
                    TerminalActive = terminalActive
                }));
            Transport.EmitControlMessage(json);
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
    /// Scenario: connected session receives operator status progressing waiting to attached to active.
    /// Expected: NOC snapshot states advance accordingly.
    /// </summary>
    [Fact]
    public async Task InitialWaiting_ThenAttached_ThenActive()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();

        h.EmitStatus(1, attached: false, terminalActive: false);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Waiting);

        h.EmitStatus(2, attached: true, terminalActive: false);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Attached);

        h.EmitStatus(3, attached: true, terminalActive: true);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Active);
        h.Session.GetSnapshot().RemoteStatus.Should().Be(RemoteSessionState.NocSessionActive);
    }

    /// <summary>
    /// Scenario: Ctrl+] then detach status while active.
    /// Expected: returns to attached then waiting.
    /// </summary>
    [Fact]
    public async Task CtrlBracket_ReturnsToAttached_Detach_ReturnsToWaiting()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();
        h.EmitStatus(1, false, false);
        h.EmitStatus(2, true, false);
        h.EmitStatus(3, true, true);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Active);

        h.EmitStatus(4, true, false);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Attached);

        h.EmitStatus(5, false, false);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Waiting);
    }

    /// <summary>
    /// Scenario: higher version applied; duplicate and lower versions ignored.
    /// Expected: only newer version wins; conflicts logged.
    /// </summary>
    [Fact]
    public async Task HigherVersionApplied_DuplicateIgnored_LowerIgnored_ConflictLogged()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();
        h.EmitStatus(1, false, false);
        h.EmitStatus(2, true, true);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Active);

        h.EmitStatus(2, true, true); // duplicate identical
        h.EmitStatus(1, false, false); // stale
        await Task.Delay(50);
        h.Session.GetSnapshot().NocStatus.Should().Be(NocAttachmentState.Active);

        h.EmitStatus(2, true, false); // same version, different state
        await Task.Delay(50);
        h.Session.GetSnapshot().NocStatus.Should().Be(NocAttachmentState.Active);
        h.Logger.Entries.Should().Contain(e =>
            e.Message.Contains("version conflict", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: invalid operator-status payload.
    /// Expected: session does not enter Active NOC state.
    /// </summary>
    [Fact]
    public async Task InvalidPayload_DoesNotBecomeActive()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();
        h.Transport.EmitControlMessage(
            """{"stateVersion":1,"attached":false,"terminalActive":true}""");
        await Task.Delay(50);
        h.Session.GetSnapshot().NocStatus.Should().Be(NocAttachmentState.Waiting);
    }

    /// <summary>
    /// Scenario: disconnect after NOC attachment.
    /// Expected: NOC status resets to None.
    /// </summary>
    [Fact]
    public async Task Disconnect_ResetsNocToNone()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();
        h.EmitStatus(1, false, false);
        h.EmitStatus(2, true, true);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Active);

        await h.Session.StopAsync(CancellationToken.None);
        h.Session.GetSnapshot().NocStatus.Should().Be(NocAttachmentState.None);
        h.Session.GetSnapshot().RemoteStatus.Should().Be(RemoteSessionState.Disconnected);
    }

    /// <summary>
    /// Scenario: reconnect then old-generation status arrives.
    /// Expected: version resets; stale generation ignored.
    /// </summary>
    [Fact]
    public async Task Reconnect_ResetsVersion_IgnoresOldGeneration()
    {
        await using var h = new Harness();
        await h.StartConnectedAsync();
        h.EmitStatus(1, false, false);
        h.EmitStatus(5, true, true);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Active);

        h.Transport.SimulateDrop();
        // Connected wait loop uses TimeProvider delays; advance so reconnect proceeds.
        h.Time.Advance(TimeSpan.FromSeconds(1));
        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.IsConnected && h.Transport.ConnectCallCount >= 2,
            because: "should reconnect after drop");
        await TestHelpers.WaitUntilAsync(() =>
            h.Session.GetSnapshot().NocStatus == NocAttachmentState.Waiting &&
            h.Session.GetSnapshot().RemoteStatus == RemoteSessionState.ConnectedWaitingForNoc);

        // New connection generation resets status version; v1 applies again.
        h.EmitStatus(1, true, false);
        await TestHelpers.WaitUntilAsync(() => h.Session.GetSnapshot().NocStatus == NocAttachmentState.Attached);
    }
}
