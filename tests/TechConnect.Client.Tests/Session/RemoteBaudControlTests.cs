// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteBaudControlTests.cs
//
// Purpose:
// Verifies remote baud/configuration control messages applied to the serial service.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Verifies remote setBaud/control configuration applied through the serial service.
/// </summary>
public sealed class RemoteBaudControlTests
{
    /// <summary>
    /// Scenario: session connects with an open serial port.
    /// Expected: connector.hello includes current serial config.
    /// </summary>
    [Fact]
    public async Task Connect_sends_connector_hello_with_serial_config()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        await TestHelpers.WaitUntilAsync(() => !h.Transport.SentControlMessages.IsEmpty);

        h.Transport.SentControlMessages.TryPeek(out var json).Should().BeTrue();
        ControlProtocol.TryParse(json!, out var message, out var error).Should().BeTrue(error);
        message!.Type.Should().Be(ControlProtocol.TypeConnectorHello);
        ControlProtocol.TryGetPayload(message, out ConnectorHelloPayload? hello, out _).Should().BeTrue();
        hello!.PortName.Should().Be("COM4");
        hello.Config.BaudRate.Should().Be(115200);
        hello.ClientSessionId.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Scenario: valid setBaud control message.
    /// Expected: serial service baud updated and success result reported.
    /// </summary>
    [Fact]
    public async Task Valid_setBaud_applies_through_serial_service()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var request = ControlProtocol.Create(
            ControlProtocol.TypeSetBaud,
            new SetBaudPayload { BaudRate = 9600 },
            correlationId: "baud-1");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(request));

        await TestHelpers.WaitUntilAsync(
            () => h.Serial.CurrentConfiguration.BaudRate == 9600,
            because: "setBaud should apply via SerialConnectionService");

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m => m.Contains("setBaudResult", StringComparison.Ordinal)),
            because: "connector should reply with setBaudResult");

        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: unsupported baud in setBaud.
    /// Expected: rejected; prior config unchanged.
    /// </summary>
    [Fact]
    public async Task Invalid_baud_is_rejected_without_changing_config()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var request = ControlProtocol.Create(
            ControlProtocol.TypeSetBaud,
            new SetBaudPayload { BaudRate = 111 },
            correlationId: "baud-bad");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(request));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("setBaudResult", StringComparison.Ordinal) &&
                m.Contains("false", StringComparison.Ordinal)));

        h.Serial.CurrentConfiguration.BaudRate.Should().Be(115200);
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: setBaud delivered as binary JSON frame.
    /// Expected: applies and reports result like text control.
    /// </summary>
    [Fact]
    public async Task Binary_SetBaud_json_applies_and_reports_result()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var payload = Encoding.UTF8.GetString(ControlMessageSerializer.SerializeSetBaud(
            new SetBaudMessage { BaudRate = 57600, CorrelationId = "bin-1" }));
        h.Transport.EmitControlMessage(payload);

        await TestHelpers.WaitUntilAsync(
            () => h.Serial.CurrentConfiguration.BaudRate == 57600,
            because: "binary SetBaud should apply");

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("setBaudResult", StringComparison.Ordinal) &&
                m.Contains("57600", StringComparison.Ordinal)));

        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: setBaud while serial closed.
    /// Expected: rejection without applying.
    /// </summary>
    [Fact]
    public async Task Closed_serial_rejects_setBaud()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);
        await h.Serial.CloseAsync(CancellationToken.None);

        var request = ControlProtocol.Create(
            ControlProtocol.TypeSetBaud,
            new SetBaudPayload { BaudRate = 9600 },
            correlationId: "baud-closed");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(request));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("setBaudResult", StringComparison.Ordinal) &&
                m.Contains("not open", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Scenario: setBaud on live fake serial before terminal traffic.
    /// Expected: success result and baud applied.
    /// </summary>
    [Fact]
    public async Task LiveSerial_SetBaud_AppliesAndReportsSuccess_BeforeTerminalUse()
    {
        await using var h = new LiveSerialHarness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        var request = ControlProtocol.Create(
            ControlProtocol.TypeSetBaud,
            new SetBaudPayload
            {
                BaudRate = 9600,
                DataBits = 8,
                Parity = "None",
                StopBits = "One",
                Handshake = "None"
            },
            correlationId: "live-baud-1");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(request));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("setBaudResult", StringComparison.Ordinal) &&
                m.Contains("\"success\":true", StringComparison.OrdinalIgnoreCase)),
            because: "SetBaudResult success must follow completed ApplyConfiguration");

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.CurrentConfiguration.BaudRate.Should().Be(9600);
        h.Factory.Ports["COM4"].OpenCount.Should().Be(2);
        h.Serial.GetReceiveDiagnostics().IsReaderActive.Should().BeTrue();

        await h.Serial.WriteAsync("ping"u8.ToArray(), CancellationToken.None);
        h.Factory.Ports["COM4"].WrittenChunks.Should().ContainSingle()
            .Which.Should().Equal("ping"u8.ToArray());

        h.Factory.Ports["COM4"].InjectRx("pong");
        await TestHelpers.WaitUntilAsync(() => h.Serial.GetStats().BytesReceived >= 4);
    }

    /// <summary>
    /// Scenario: driver rejects baud change.
    /// Expected: failure reported; port remains open.
    /// </summary>
    [Fact]
    public async Task LiveSerial_SetBaud_DriverReject_ReportsFailure_KeepsPortOpen()
    {
        await using var h = new LiveSerialHarness();
        await h.Serial.OpenAsync("COM4", SerialPortConfiguration.CreateDefault(), CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.IsConnected);

        h.Factory.Ports["COM4"].Options.ConfigurationException =
            new ArgumentException("driver rejected framing");

        var request = ControlProtocol.Create(
            ControlProtocol.TypeSetBaud,
            new SetBaudPayload { BaudRate = 57600 },
            correlationId: "live-baud-fail");
        h.Transport.EmitControlMessage(ControlProtocol.Serialize(request));

        await TestHelpers.WaitUntilAsync(
            () => h.Transport.SentControlMessages.Any(m =>
                m.Contains("setBaudResult", StringComparison.Ordinal) &&
                m.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase)));

        h.Serial.IsOpen.Should().BeTrue();
        h.Serial.CurrentConfiguration.BaudRate.Should().Be(115200);
        h.Serial.GetReceiveDiagnostics().IsReaderActive.Should().BeTrue();
    }

    /// <summary>
    /// Fake serial/transport harness for baud-control unit tests.
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
        /// Fake time provider driving session timers.
        /// </summary>
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        /// <summary>
        /// RemoteSessionService under test.
        /// </summary>
        public RemoteSessionService Session { get; }

        /// <summary>
        /// Creates the fake-serial baud-control harness.
        /// </summary>
        public Harness()
        {
            Session = CreateSession(Serial, Transport, Time);
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
    /// Harness using FakeSerialPort-backed SerialConnectionService for live setBaud paths.
    /// </summary>
    private sealed class LiveSerialHarness : IAsyncDisposable
    {
        /// <summary>
        /// Fake serial port factory backing the live serial service.
        /// </summary>
        public FakeSerialPortFactory Factory { get; } = new();
        /// <summary>
        /// Real SerialConnectionService over FakeSerialPort for live setBaud paths.
        /// </summary>
        public SerialConnectionService Serial { get; }

        /// <summary>
        /// Fake remote transport used by the live-serial harness.
        /// </summary>
        public FakeRemoteTransport Transport { get; } = new();

        /// <summary>
        /// Fake time provider driving session timers.
        /// </summary>
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));

        /// <summary>
        /// RemoteSessionService under test.
        /// </summary>
        public RemoteSessionService Session { get; }

        /// <summary>
        /// Creates the live-serial baud-control harness.
        /// </summary>
        public LiveSerialHarness()
        {
            Serial = new SerialConnectionService(
                new CollectingLogger<SerialConnectionService>(),
                Time,
                Factory);
            Session = CreateSession(Serial, Transport, Time);
        }

        /// <summary>
        /// Disposes the session, serial service, and transport.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates a RemoteSessionService wired to the provided serial/transport/time dependencies.
    /// </summary>
    private static RemoteSessionService CreateSession(
        ISerialConnectionService serial,
        FakeRemoteTransport transport,
        FakeTimeProvider time)
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
            time,
            new FixedJitterProvider(0.5),
            new CollectingLogger<ReconnectCoordinator>());

        return new RemoteSessionService(
            serial,
            transport,
            reconnect,
            opts,
            time,
            new CollectingLogger<RemoteSessionService>());
    }
}
