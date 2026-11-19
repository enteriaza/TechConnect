// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    TcpMtlsRemoteConsoleTransportTests.cs
//
// Purpose:
// Verifies TCP mTLS remote console transport against an ephemeral local server.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Client.Configuration;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Transport;

/// <summary>
/// TCP mTLS transport connect, rejection, cancellation, and teardown tests.
/// </summary>
public sealed class TcpMtlsRemoteConsoleTransportTests
{
    /// <summary>
    /// Scenario: connect to ephemeral mTLS server with valid cert/hostname.
    /// Expected: connect succeeds.
    /// </summary>
    [Fact]
    public async Task Connect_AcceptsValidServerCertificateAndHostname()
    {
        await using var server = await EphemeralMtlsServer.StartWithPersistentCertificatesAsync();
        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider, tlsHostName: "localhost");
        await transport.ConnectAsync(CancellationToken.None);

        transport.IsConnected.Should().BeTrue();
        await transport.DisconnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: server presents untrusted certificate.
    /// Expected: connect fails.
    /// </summary>
    [Fact]
    public async Task Connect_RejectsUntrustedServerCertificate()
    {
        using var ca = TestCertificateFactory.CreateCa("TechConnect.Client Untrusted TCP CA");
        using var serverCert = TestCertificateFactory.CreateServerCertificate(ca);
        await using var server = await EphemeralMtlsServer.StartWithInMemoryCertificateAsync(serverCert);

        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider);
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportException>();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: TLS hostname does not match certificate.
    /// Expected: connect fails.
    /// </summary>
    [Fact]
    public async Task Connect_RejectsHostnameMismatch()
    {
        using var caPrivate = LoadCaPrivateKeyForNegativeTests();
        using var wrongHost = TestCertificateFactory.CreateServerCertificate(caPrivate, hostName: "wrong.host.test");
        await using var server = await EphemeralMtlsServer.StartWithInMemoryCertificateAsync(wrongHost);

        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider, tlsHostName: "localhost");
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportException>();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: cancel during connect.
    /// Expected: operation cancels.
    /// </summary>
    [Fact]
    public async Task Connect_Cancellation_Works()
    {
        await using var server = await EphemeralMtlsServer.StartWithPersistentCertificatesAsync();
        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () => await transport.ConnectAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Scenario: disconnect/dispose after connect.
    /// Expected: teardown completes within a bound.
    /// </summary>
    [Fact]
    public async Task Teardown_RemainsBounded()
    {
        await using var server = await EphemeralMtlsServer.StartWithPersistentCertificatesAsync();
        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider);
        await transport.ConnectAsync(CancellationToken.None);

        var started = Environment.TickCount64;
        await transport.DisconnectAsync(CancellationToken.None);
        (Environment.TickCount64 - started).Should().BeLessThan(5_000);
    }

    /// <summary>
    /// Scenario: remote aborts the TCP connection.
    /// Expected: teardown bounded; no serial coupling required.
    /// </summary>
    [Fact]
    public async Task RemoteAbort_TeardownRemainsBounded_WithoutSerialCoupling()
    {
        await using var server = await EphemeralMtlsServer.StartWithPersistentCertificatesAsync();
        using var clientCert = PersistentDevCertificates.LoadClientCertificate();
        var provider = new FakeClientCertificateProvider { IsConfigured = true };
        provider.SetCertificate(clientCert);

        await using var transport = CreateTransport(server.Port, provider);
        await transport.ConnectAsync(CancellationToken.None);

        server.AbortAll();
        await Task.Delay(200);

        var started = Environment.TickCount64;
        await transport.DisconnectAsync(CancellationToken.None);
        (Environment.TickCount64 - started).Should().BeLessThan(5_000);
    }

    /// <summary>
    /// Creates a TcpMtlsRemoteConsoleTransport for the given host/port/options.
    /// </summary>
    private static TcpMtlsRemoteConsoleTransport CreateTransport(
        int port,
        FakeClientCertificateProvider provider,
        string tlsHostName = "localhost")
    {
        var options = Options.Create(new RemoteServiceOptions
        {
            Host = "127.0.0.1",
            Port = port,
            TlsHostName = tlsHostName,
            ConnectTimeoutSeconds = 5
        });

        return new TcpMtlsRemoteConsoleTransport(
            provider,
            options,
            new CollectingLogger<TcpMtlsRemoteConsoleTransport>());
    }

    /// <summary>
    /// Loads CA material for negative TLS tests without mutating the Windows store.
    /// </summary>
    private static X509Certificate2 LoadCaPrivateKeyForNegativeTests()
    {
        var registry = PersistentDevCertificates.RequireProvisioned();
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            RemoteServiceOptionsValidator.NormalizeThumbprint(registry.CaThumbprint),
            validOnly: false);
        var cert = matches.OfType<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new InvalidOperationException(
                "Development CA private key not found in CurrentUser\\My for negative TLS leaf issuance.");
        return new X509Certificate2(cert);
    }
}

/// <summary>
/// Minimal ephemeral mTLS TCP server for TechConnect.Client transport tests.
/// Never installs certificates into Windows stores.
/// </summary>
internal sealed class EphemeralMtlsServer : IAsyncDisposable
{
    /// <summary>
    /// Loopback TCP listener accepting TLS clients.
    /// </summary>
    private readonly TcpListener _listener;
    /// <summary>
    /// Server certificate presented during TLS handshake.
    /// </summary>
    private readonly X509Certificate2 _server;
    /// <summary>
    /// Optional trust anchor retained for server lifetime.
    /// </summary>
    private readonly X509Certificate2? _trustAnchor;
    /// <summary>
    /// When true, DisposeAsync disposes _server.
    /// </summary>
    private readonly bool _ownsServerCertificate;
    /// <summary>
    /// Cancels the accept loop on dispose.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();
    /// <summary>
    /// Accepted clients retained until abort/dispose.
    /// </summary>
    private readonly List<TcpClient> _clients = [];
    /// <summary>
    /// Synchronizes client list mutations.
    /// </summary>
    private readonly object _gate = new();
    /// <summary>
    /// Background accept-loop task.
    /// </summary>
    private readonly Task _acceptTask;

    /// <summary>
    /// Creates the server over an already-bound listener and certificate material.
    /// </summary>
    private EphemeralMtlsServer(
        TcpListener listener,
        int port,
        X509Certificate2 server,
        X509Certificate2? trustAnchor,
        bool ownsServerCertificate)
    {
        _listener = listener;
        Port = port;
        _server = server;
        _trustAnchor = trustAnchor;
        _ownsServerCertificate = ownsServerCertificate;
        _acceptTask = Task.Run(AcceptLoopAsync);
    }

    /// <summary>
    /// Ephemeral listen port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Starts using provisioned development server certificate material.
    /// </summary>
    public static async Task<EphemeralMtlsServer> StartWithPersistentCertificatesAsync()
    {
        var serverCert = PersistentDevCertificates.LoadServerCertificate();
        var ca = PersistentDevCertificates.LoadCaPublicCertificate();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = new EphemeralMtlsServer(listener, port, serverCert, ca, ownsServerCertificate: true);
        await Task.Yield();
        return server;
    }

    /// <summary>
    /// Starts with an in-memory server certificate for negative tests.
    /// </summary>
    public static async Task<EphemeralMtlsServer> StartWithInMemoryCertificateAsync(X509Certificate2 serverCertificate)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Keep a duplicate so the caller can dispose their instance independently.
        var owned = X509CertificateLoader.LoadPkcs12(
            serverCertificate.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        var server = new EphemeralMtlsServer(listener, port, owned, trustAnchor: null, ownsServerCertificate: true);
        await Task.Yield();
        return server;
    }

    /// <summary>
    /// Aborts all accepted TCP clients.
    /// </summary>
    public void AbortAll()
    {
        lock (_gate)
        {
            foreach (var client in _clients)
            {
                try
                {
                    client.Client?.Close();
                    client.Dispose();
                }
                catch
                {
                }
            }

            _clients.Clear();
        }
    }

    /// <summary>
    /// Accepts inbound TCP clients until cancelled.
    /// </summary>
    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                continue;
            }

            lock (_gate)
            {
                _clients.Add(client);
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    /// <summary>
    /// Performs TLS auth and holds the connection until abort/dispose.
    /// </summary>
    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _server,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        {
                            if (certificate is null)
                            {
                                return false;
                            }

                            if (_trustAnchor is null)
                            {
                                // Negative-test servers accept any presented client cert.
                                return true;
                            }

                            using var chain = new X509Chain();
                            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                            chain.ChainPolicy.CustomTrustStore.Add(_trustAnchor);
                            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                            var leaf = certificate as X509Certificate2 ?? new X509Certificate2(certificate);
                            return chain.Build(leaf);
                        }
                    },
                    _cts.Token)
                .ConfigureAwait(false);

            var buffer = new byte[1024];
            while (!_cts.IsCancellationRequested)
            {
                var read = await ssl.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Stops accept loop and disposes clients/certificates.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener.Stop();
        }
        catch
        {
        }

        AbortAll();
        try
        {
            await _acceptTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch
        {
        }

        if (_ownsServerCertificate)
        {
            _server.Dispose();
        }

        _trustAnchor?.Dispose();
        _cts.Dispose();
    }
}
