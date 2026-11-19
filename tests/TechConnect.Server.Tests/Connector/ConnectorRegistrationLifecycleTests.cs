// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorRegistrationLifecycleTests.cs
//
// Purpose:
// Verifies Hello registration lifecycle: success, reject-new identity, malformed Hello, and shutdown clear.
//
// -----------------------------------------------------------------------------

using System.Net.Security;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Integration tests for connector Hello registration and session registry lifetime.
/// </summary>
public sealed class ConnectorRegistrationLifecycleTests
{
    /// <summary>
    /// Verifies valid Hello registers a session, returns HelloResult, and unregisters on disconnect.
    /// </summary>
    [Fact]
    public async Task ValidHello_RegistersAndReturnsSessionId()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "client-a",
            Hostname = "router-001",
            ApplicationVersion = "1.2.3",
            PortName = "COM1",
            BaudRate = 115200
        });

        var frame = await protocol.Reader.ReadAsync();
        var result = ControlMessageSerializer.DeserializeHelloResult(frame!.Value.Payload);
        result.Success.Should().BeTrue();
        result.SessionId.Should().MatchRegex("^[0-9a-f]{8}$");
        result.ServerName.Should().Be("TechConnect.Server");

        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));
        host.SessionRegistry.TryGetBySessionId(result.SessionId!, out var session).Should().BeTrue();
        session!.Hostname.Should().Be("router-001");
        session.ApplicationVersion.Should().Be("1.2.3");
        session.ConnectorIdentity.Should().Be(TestCertificateFactory.Thumbprint(clientCert));
        session.RemoteEndpoint.Should().NotBeNullOrWhiteSpace();
        session.TlsProtocol.Should().NotBeNullOrWhiteSpace();

        await ssl!.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies mTLS alone does not create a registry session.
    /// </summary>

    [Fact]
    public async Task AuthenticatedConnectionWithoutHello_IsNeverRegistered()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();
        ssl!.IsMutuallyAuthenticated.Should().BeTrue();

        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));
        await Task.Delay(200);
        host.SessionRegistry.Count.Should().Be(0);

        await ssl.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies malformed Hello fails HelloResult without creating a session.
    /// </summary>

    [Fact]
    public async Task MalformedHello_DoesNotCreateSession()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.Writer.WriteAsync(MessageType.Hello, "{not-json"u8.ToArray());

        var frame = await protocol.Reader.ReadAsync();
        var result = ControlMessageSerializer.DeserializeHelloResult(frame!.Value.Payload);
        result.Success.Should().BeFalse();
        host.SessionRegistry.Count.Should().Be(0);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies oversized hostname is rejected by the server and leaves the registry empty.
    /// </summary>

    [Fact]
    public async Task ExcessivelyLongHostname_IsRejected()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        // Bypass client-side ValidateHello so the server performs the length rejection.
        var oversized = new string('x', ControlMessageSerializer.MaxHostnameLength + 1);
        var raw = System.Text.Encoding.UTF8.GetBytes(
            $$"""{"clientSessionId":"client-a","hostname":"{{oversized}}","portName":"COM1","baudRate":115200,"dataBits":8}""");
        await protocol.Writer.WriteAsync(MessageType.Hello, raw);

        var frame = await protocol.Reader.ReadAsync();
        var result = ControlMessageSerializer.DeserializeHelloResult(frame!.Value.Payload);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("hostname");
        host.SessionRegistry.Count.Should().Be(0);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies duplicate identity Hello fails while the original registration remains.
    /// </summary>

    [Fact]
    public async Task DuplicateIdentity_ReceivesUnsuccessfulHelloResult_OriginalRemains()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();

        var (ssl1, tcp1, error1) = await host.ConnectClientAsync(clientCert);
        error1.Should().BeNull();
        await using var protocol1 = new ProtocolSession(ssl1!);
        await protocol1.WriteHelloAsync(CreateHello("first"));
        var first = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol1.Reader.ReadAsync())!.Value.Payload);
        first.Success.Should().BeTrue();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var (ssl2, tcp2, error2) = await host.ConnectClientAsync(clientCert);
        error2.Should().BeNull();
        await using var protocol2 = new ProtocolSession(ssl2!);
        await protocol2.WriteHelloAsync(CreateHello("second"));
        var second = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol2.Reader.ReadAsync())!.Value.Payload);
        second.Success.Should().BeFalse();
        second.Error.Should().Contain("already registered");
        host.SessionRegistry.Count.Should().Be(1);
        host.SessionRegistry.TryGetBySessionId(first.SessionId!, out _).Should().BeTrue();

        await ssl2!.DisposeAsync();
        tcp2!.Dispose();
        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));
        host.SessionRegistry.Count.Should().Be(1);
        host.SessionRegistry.TryGetBySessionId(first.SessionId!, out _).Should().BeTrue();

        await ssl1!.DisposeAsync();
        tcp1!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies distinct identities register independently and partial disconnect leaves the other.
    /// </summary>

    [Fact]
    public async Task DifferentIdentities_RegisterIndependently_OneFailureDoesNotAffectOther()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var certA = host.CreateClientCertificate();
        using var certB = host.CreateClientCertificate();

        var (sslA, tcpA, errA) = await host.ConnectClientAsync(certA);
        var (sslB, tcpB, errB) = await host.ConnectClientAsync(certB);
        errA.Should().BeNull();
        errB.Should().BeNull();

        await using var protocolA = new ProtocolSession(sslA!);
        await using var protocolB = new ProtocolSession(sslB!);
        await protocolA.WriteHelloAsync(CreateHello("a"));
        await protocolB.WriteHelloAsync(CreateHello("b"));

        var resultA = ControlMessageSerializer.DeserializeHelloResult(
            (await protocolA.Reader.ReadAsync())!.Value.Payload);
        var resultB = ControlMessageSerializer.DeserializeHelloResult(
            (await protocolB.Reader.ReadAsync())!.Value.Payload);
        resultA.Success.Should().BeTrue();
        resultB.Success.Should().BeTrue();
        resultA.SessionId.Should().NotBe(resultB.SessionId);
        await host.WaitForRegisteredCountAsync(2, TimeSpan.FromSeconds(5));

        await sslA!.DisposeAsync();
        tcpA!.Dispose();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));
        host.SessionRegistry.TryGetBySessionId(resultB.SessionId!, out _).Should().BeTrue();

        await sslB!.DisposeAsync();
        tcpB!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies host shutdown clears the session registry.
    /// </summary>

    [Fact]
    public async Task ServerShutdown_ClearsRegisteredSessions()
    {
        await using var host = await ConnectorTestHost.StartAsync(shutdownTimeoutSeconds: 3);
        var registry = host.SessionRegistry;
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(CreateHello("shutdown"));
        var result = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol.Reader.ReadAsync())!.Value.Payload);
        result.Success.Should().BeTrue();
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        await host.DisposeAsync();
        registry.Count.Should().Be(0);

        try
        {
            await ssl!.DisposeAsync();
        }
        catch
        {
            // Connection may already be closed by server shutdown.
        }

        tcp!.Dispose();
    }

    /// <summary>
    /// Builds a minimal valid Hello message for registration tests.
    /// </summary>
    /// <param name="clientSessionId">Client-side session identifier field.</param>
    /// <returns>Hello payload with test hostname/port/baud defaults.</returns>
    private static HelloMessage CreateHello(string clientSessionId) =>
        new()
        {
            ClientSessionId = clientSessionId,
            Hostname = "test-host",
            ApplicationVersion = "1.0.0",
            PortName = "COM1",
            BaudRate = 115200
        };
}
