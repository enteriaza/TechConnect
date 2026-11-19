// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TestConnectorConnectionFactory.cs
//
// Purpose:
// Builds a disposable loopback TLS ConnectorConnection for
// disconnect-lifecycle tests.
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TechConnect.Server.Connector;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Builds a disposable loopback TLS <see cref="ConnectorConnection"/> for disconnect-lifecycle tests.
/// </summary>
internal static class TestConnectorConnectionFactory
{
    /// <summary>
    /// Asynchronously performs create authenticated.
    /// </summary>
    /// <param name="connectionId">The connection id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The operation result.</returns>
    public static async Task<(ConnectorConnection Connection, TcpClient Peer, X509Certificate2 ServerCert, X509Certificate2 Ca)>
        CreateAuthenticatedAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var ca = TestCertificateFactory.CreateCa();
        var serverCert = TestCertificateFactory.CreateServerCertificate(ca);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken)
                .ConfigureAwait(false);
            var serverTcp = await acceptTask.ConfigureAwait(false);

            var serverSsl = new SslStream(serverTcp.GetStream(), leaveInnerStreamOpen: false);
            var clientSsl = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);

            var serverAuth = serverSsl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCert,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ClientCertificateRequired = false
                },
                cancellationToken);

            var clientAuth = clientSsl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                },
                cancellationToken);

            await Task.WhenAll(serverAuth, clientAuth).ConfigureAwait(false);

            // Peer keeps the client side alive until the test disposes it; server side is owned by ConnectorConnection.
            var lifetime = new CancellationTokenSource();
            var connection = new ConnectorConnection(
                connectionId,
                certificateThumbprint: "AABBCCDDEEFF00112233445566778899AABBCCDD",
                certificateSubject: "CN=test-connector",
                remoteEndPoint: serverTcp.Client.RemoteEndPoint,
                tcpClient: serverTcp,
                sslStream: serverSsl,
                negotiatedProtocol: serverSsl.SslProtocol,
                establishedAtUtc: DateTimeOffset.UtcNow,
                lifetimeCts: lifetime);

            return (connection, client, serverCert, ca);
        }
        finally
        {
            listener.Stop();
        }
    }
}
