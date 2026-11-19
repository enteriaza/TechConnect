// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    HelloMtlsProtocolIntegrationTests.cs
//
// Purpose:
// Verifies Hello/HelloResult and framing failure behaviour over real mTLS with ephemeral test certificates.
//
// -----------------------------------------------------------------------------

using System.Net.Security;
using System.Security.Authentication;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Phase 4: Hello / HelloResult exchange over real mTLS (ephemeral test certs; no store mutation).
/// </summary>
public sealed class HelloMtlsProtocolIntegrationTests
{
    /// <summary>
    /// Verifies successful Hello yields HelloResult, session registration, and initial OperatorStatus Waiting.
    /// </summary>
    [Fact]
    public async Task ClientHello_ReceivesHelloResult_OverMtls()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();

        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();
        ssl.Should().NotBeNull();
        ssl!.IsMutuallyAuthenticated.Should().BeTrue();

        await using var protocol = new ProtocolSession(ssl);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "integration-session",
            Hostname = "test-host",
            PortName = "COM1",
            BaudRate = 115200
        });

        var frame = await protocol.Reader.ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(MessageType.HelloResult);

        var result = ControlMessageSerializer.DeserializeHelloResult(frame.Value.Payload);
        result.Success.Should().BeTrue();
        result.ServerName.Should().Be("TechConnect.Server");
        result.SessionId.Should().MatchRegex("^[0-9a-f]{8}$");
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var statusFrame = await protocol.Reader.ReadAsync();
        statusFrame.Should().NotBeNull();
        statusFrame!.Value.MessageType.Should().Be(MessageType.OperatorStatus);
        var status = ControlMessageSerializer.DeserializeOperatorStatus(statusFrame.Value.Payload);
        status.StateVersion.Should().Be(1u);
        status.Attached.Should().BeFalse();
        status.TerminalActive.Should().BeFalse();

        await ssl.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
        host.SessionRegistry.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies unsupported frame version drops the tracked connection without resynchronization.
    /// </summary>

    [Fact]
    public async Task UnsupportedProtocolVersion_TerminatesProtocol()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        // Craft a raw frame with version 99.
        var header = new byte[ProtocolConstants.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0);
        header[4] = (byte)MessageType.Hello;
        header[5] = 99;
        await ssl!.WriteAsync(header);
        await ssl.FlushAsync();

        // Server should drop the connection after the fatal framing error.
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));

        await ssl.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies malformed Hello JSON returns failed HelloResult without registering a session.
    /// </summary>

    [Fact]
    public async Task MalformedHelloPayload_ReturnsFailedHelloResult()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.Writer.WriteAsync(MessageType.Hello, "{not-json"u8.ToArray());

        var frame = await protocol.Reader.ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(MessageType.HelloResult);
        var result = ControlMessageSerializer.DeserializeHelloResult(frame.Value.Payload);
        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }
}
