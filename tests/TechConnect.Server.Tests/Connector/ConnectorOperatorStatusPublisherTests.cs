// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorOperatorStatusPublisherTests.cs
//
// Purpose:
// Verifies OperatorStatus version allocation, duplicate skip, concurrent ordering, and post-dispose no-op behaviour.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Connector;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Tests for ConnectorOperatorStatusPublisher wire ordering and lifecycle.
/// </summary>
public sealed class ConnectorOperatorStatusPublisherTests
{
    /// <summary>
    /// Verifies the first Waiting publish allocates version 1 with attached/terminal false.
    /// </summary>
    [Fact]
    public async Task InitialWaiting_PublishesVersion1()
    {
        await using var harness = await LoopbackHarness.CreateAsync();
        await harness.Connection.OperatorStatus.PublishAsync(attached: false, terminalActive: false);

        var status = await harness.ReadOperatorStatusAsync();
        status.StateVersion.Should().Be(1u);
        status.Attached.Should().BeFalse();
        status.TerminalActive.Should().BeFalse();
    }

    /// <summary>
    /// Verifies state transitions allocate strictly increasing versions and skip duplicates.
    /// </summary>

    [Fact]
    public async Task Transitions_AreStrictlyIncreasing_AndSkipDuplicates()
    {
        await using var harness = await LoopbackHarness.CreateAsync();
        var pub = harness.Connection.OperatorStatus;
        await pub.PublishAsync(false, false);
        await pub.PublishAsync(false, false);
        await pub.PublishAsync(true, false);
        await pub.PublishAsync(true, true);
        await pub.PublishAsync(true, false);
        await pub.PublishAsync(false, false);

        var versions = new List<uint>();
        for (var i = 0; i < 5; i++)
        {
            versions.Add((await harness.ReadOperatorStatusAsync()).StateVersion);
        }

        versions.Should().Equal(1u, 2u, 3u, 4u, 5u);
    }

    /// <summary>
    /// Verifies concurrent publishes remain uniquely ordered ascending on the wire.
    /// </summary>

    [Fact]
    public async Task ConcurrentPublishes_DoNotReorderOnWire()
    {
        await using var harness = await LoopbackHarness.CreateAsync();
        var tasks = Enumerable.Range(0, 16)
            .Select(i => harness.Connection.OperatorStatus.PublishAsync(
                attached: i % 2 == 0,
                terminalActive: false))
            .ToArray();
        await Task.WhenAll(tasks);

        var versions = new List<uint>();
        while (versions.Count < 16)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try
            {
                versions.Add((await harness.ReadOperatorStatusAsync(cts.Token)).StateVersion);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        versions.Should().NotBeEmpty();
        versions.Should().BeInAscendingOrder();
        versions.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Verifies publish after dispose does not emit frames.
    /// </summary>

    [Fact]
    public async Task PublishAfterDispose_IsNoOp()
    {
        await using var harness = await LoopbackHarness.CreateAsync();
        await harness.Connection.OperatorStatus.DisposeAsync();
        await harness.Connection.OperatorStatus.PublishAsync(true, true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var act = async () => await harness.ReadOperatorStatusAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies serializer rejects TerminalActive when Attached is false.
    /// </summary>

    [Fact]
    public void TerminalActiveWithoutAttached_RejectedBySerializer()
    {
        var act = () => ControlMessageSerializer.SerializeOperatorStatus(new OperatorStatusMessage
        {
            StateVersion = 1,
            Attached = false,
            TerminalActive = true
        });
        act.Should().Throw<ProtocolInvalidPayloadException>();
    }

    /// <summary>
    /// Loopback TLS harness exposing a server-side ConnectorConnection and peer FrameReader.
    /// </summary>
    private sealed class LoopbackHarness : IAsyncDisposable
    {
        /// <summary>Peer TCP client retained for dispose.</summary>
        private readonly TcpClient _peerTcp;
        /// <summary>Peer SSL stream used to read OperatorStatus frames.</summary>
        private readonly SslStream _peerSsl;
        /// <summary>Frame reader over the peer SSL stream.</summary>
        private readonly FrameReader _reader;
        /// <summary>Ephemeral server certificate owned by the harness.</summary>
        private readonly X509Certificate2 _serverCert;
        /// <summary>Ephemeral CA certificate owned by the harness.</summary>
        private readonly X509Certificate2 _ca;

        /// <summary>
        /// Initializes ownership of connection and peer resources.
        /// </summary>
        /// <param name="connection">Server-side connector connection under test.</param>
        /// <param name="peerTcp">Peer TCP client retained for dispose.</param>
        /// <param name="peerSsl">Peer SSL stream used to read frames.</param>
        /// <param name="serverCert">Ephemeral server certificate.</param>
        /// <param name="ca">Ephemeral CA certificate.</param>
        private LoopbackHarness(
            ConnectorConnection connection,
            TcpClient peerTcp,
            SslStream peerSsl,
            X509Certificate2 serverCert,
            X509Certificate2 ca)
        {
            Connection = connection;
            _peerTcp = peerTcp;
            _peerSsl = peerSsl;
            _reader = new FrameReader(peerSsl);
            _serverCert = serverCert;
            _ca = ca;
        }

        /// <summary>Server-side authenticated connector connection under test.</summary>
        public ConnectorConnection Connection { get; }

        /// <summary>
        /// Creates a loopback mTLS pair and a ConnectorConnection on the server side.
        /// </summary>
        /// <returns>Disposable harness ready for OperatorStatus publishes.</returns>
        public static async Task<LoopbackHarness> CreateAsync()
        {
            var ca = TestCertificateFactory.CreateCa();
            var serverCert = TestCertificateFactory.CreateServerCertificate(ca);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var acceptTask = listener.AcceptTcpClientAsync();
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var serverTcp = await acceptTask;

                var serverSsl = new SslStream(serverTcp.GetStream(), leaveInnerStreamOpen: false);
                var clientSsl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

                await Task.WhenAll(
                    serverSsl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCert,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ClientCertificateRequired = false
                    }),
                    clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = "localhost",
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }));

                var connection = new ConnectorConnection(
                    "op-status-test",
                    certificateThumbprint: "AABBCCDDEEFF00112233445566778899AABBCCDD",
                    certificateSubject: "CN=test-connector",
                    remoteEndPoint: serverTcp.Client.RemoteEndPoint,
                    tcpClient: serverTcp,
                    sslStream: serverSsl,
                    negotiatedProtocol: serverSsl.SslProtocol,
                    establishedAtUtc: DateTimeOffset.UtcNow,
                    lifetimeCts: new CancellationTokenSource());

                return new LoopbackHarness(connection, client, clientSsl, serverCert, ca);
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Reads the next OperatorStatus frame from the peer.
        /// </summary>
        /// <param name="cancellationToken">Cancels the underlying frame read.</param>
        /// <returns>Deserialized OperatorStatus message.</returns>
        public async Task<OperatorStatusMessage> ReadOperatorStatusAsync(
            CancellationToken cancellationToken = default)
        {
            var frame = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            frame.Should().NotBeNull();
            frame!.Value.MessageType.Should().Be(MessageType.OperatorStatus);
            return ControlMessageSerializer.DeserializeOperatorStatus(frame.Value.Payload);
        }

        /// <summary>Disposes connection, peer SSL/TCP, and ephemeral certificates.</summary>
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _peerSsl.DisposeAsync();
            _peerTcp.Dispose();
            _serverCert.Dispose();
            _ca.Dispose();
        }
    }
}
