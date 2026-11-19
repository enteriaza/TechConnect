// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SerialForwardingIntegrationTests.cs
//
// Purpose:
// Verifies byte-exact serial forwarding, connector-disconnect attachment release, and overflow detach without connector drop.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Attachment;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Integration tests for SerialData forwarding between connector and operator attachment queues.
/// </summary>
public sealed class SerialForwardingIntegrationTests
{
    /// <summary>
    /// Verifies device RX reaches the attached terminal queue byte-exact and is discarded after detach.
    /// </summary>
    [Fact]
    public async Task DeviceRx_ReachesAttachedOperatorQueue_ByteExact()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "rx-test",
            Hostname = "router-001",
            PortName = "COM1",
            BaudRate = 115200
        });
        var hello = await protocol.Reader.ReadHelloResultAsync();
        hello.Success.Should().BeTrue();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var session = host.SessionRegistry.List().Single();
        var attach = host.AttachmentManager.TryAttach("op-1", session.SessionId, session.ConnectionId);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.IsTerminalActive = true;

        var payload = new byte[] { 0x00, 0x0D, 0x0A, 0x1B, 0x5B, 0x41, 0xFF, 0xC3, 0x28 };
        await protocol.WriteSerialDataAsync(payload);

        var chunk = await attach.Attachment!.ToOperator.DequeueAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        chunk.Should().Equal(payload);

        // Unattached after detach: RX discarded, connector stays up.
        host.AttachmentManager.TryDetachByOperatorSessionId(
            "op-1",
            AttachmentDetachReason.OperatorRequest,
            out _)
            .Should().BeTrue();
        await protocol.WriteSerialDataAsync("after-detach"u8.ToArray());
        await Task.Delay(100);
        host.SessionRegistry.Count.Should().Be(1);
        host.Tracker.Count.Should().Be(1);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies operator-originated SerialData reaches the connector on the wire byte-exact.
    /// </summary>

    [Fact]
    public async Task OperatorTx_ReachesConnector_ByteExact()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "tx-test",
            Hostname = "router-001",
            PortName = "COM1",
            BaudRate = 115200
        });
        var hello = await protocol.Reader.ReadHelloResultAsync();
        hello.Success.Should().BeTrue();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var session = host.SessionRegistry.List().Single();
        host.Tracker.TryGet(session.ConnectionId, out var connection).Should().BeTrue();

        var payload = new byte[] { 0x01, 0x02, 0x00, 0xFF, (byte)'\r', (byte)'\n' };
        await connection!.Protocol.WriteSerialDataAsync(payload);

        var frame = await protocol.Reader.ReadSkippingOperatorStatusAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));
        frame.MessageType.Should().Be(MessageType.SerialData);
        frame.Payload.ToArray().Should().Equal(payload);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies connector disconnect clears attachment ownership and cancels detach CTS.
    /// </summary>

    [Fact]
    public async Task ConnectorDisconnect_DetachesOperator()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "disc",
            Hostname = "h",
            PortName = "COM1",
            BaudRate = 9600
        });
        _ = await protocol.Reader.ReadHelloResultAsync();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var session = host.SessionRegistry.List().Single();
        var attach = host.AttachmentManager.TryAttach("op-1", session.SessionId, session.ConnectionId);
        attach.Succeeded.Should().BeTrue();

        await ssl!.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && host.AttachmentManager.Count > 0)
        {
            await Task.Delay(25);
        }

        host.AttachmentManager.Count.Should().Be(0);
        attach.Attachment!.DetachCts.IsCancellationRequested.Should().BeTrue();
    }

    /// <summary>
    /// Verifies outbound overflow detaches the operator while the connector session remains tracked.
    /// </summary>

    [Fact]
    public async Task SlowOperator_Overflow_DetachesWithoutDroppingConnector()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "overflow",
            Hostname = "h",
            PortName = "COM1",
            BaudRate = 115200
        });
        _ = await protocol.Reader.ReadHelloResultAsync();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var session = host.SessionRegistry.List().Single();
        // Tiny outbound budget so one large frame overflows after fill.
        var attach = host.AttachmentManager.TryAttach(
            "op-1",
            session.SessionId,
            session.ConnectionId,
            operatorOutboundBytes: 4096);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.IsTerminalActive = true;

        await protocol.WriteSerialDataAsync(new byte[3000]);
        await protocol.WriteSerialDataAsync(new byte[3000]);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && host.AttachmentManager.Count > 0)
        {
            await Task.Delay(25);
        }

        host.AttachmentManager.Count.Should().Be(0);
        host.SessionRegistry.Count.Should().Be(1);
        host.Tracker.Count.Should().Be(1);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }
}
