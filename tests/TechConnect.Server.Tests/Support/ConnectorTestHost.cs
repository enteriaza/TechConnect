// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorTestHost.cs
//
// Purpose:
// Starts a temp-content-root TechConnect host with connector TLS
// enabled and fake certificate stores for integration tests.
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Attachment;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;
using TechConnect.Server.Connector;
using TechConnect.Server.Hosting;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Disposable connector TLS test host with fake certificate stores and temp content root.
/// </summary>
/// <remarks>
/// Limitations: uses <see cref="FakeCertificateStore"/> instead of the Windows store;
/// Development environment; ephemeral loopback port; temp content root best-effort deleted
/// on dispose. Not a substitute for full mutual-TLS production packaging tests.
/// </remarks>
internal sealed class ConnectorTestHost : IAsyncDisposable
{
    /// <summary>Running generic host.</summary>
    private readonly IHost _host;

    /// <summary>Ephemeral test CA certificate.</summary>
    private readonly X509Certificate2 _ca;

    /// <summary>Ephemeral server certificate issued by <see cref="_ca"/>.</summary>
    private readonly X509Certificate2 _server;

    /// <summary>Temporary content root directory.</summary>
    private readonly string _contentRoot;
    /// <summary>
    /// Captures a started host, its loopback port, and ephemeral certificates/content root.
    /// </summary>
    private ConnectorTestHost(
        IHost host,
        int port,
        X509Certificate2 ca,
        X509Certificate2 server,
        string contentRoot)
    {
        _host = host;
        Port = port;
        _ca = ca;
        _server = server;
        _contentRoot = contentRoot;
    }
    /// <summary>Ephemeral loopback TCP port the connector listener bound.</summary>
    public int Port { get; }
    /// <summary>DI <see cref="ConnectorConnectionTracker"/> from the test host.</summary>
    public ConnectorConnectionTracker Tracker =>
        _host.Services.GetRequiredService<ConnectorConnectionTracker>();
    /// <summary>DI <see cref="ConnectorSessionRegistry"/> from the test host.</summary>
    public ConnectorSessionRegistry SessionRegistry =>
        _host.Services.GetRequiredService<ConnectorSessionRegistry>();
    /// <summary>DI <see cref="OperatorAttachmentManager"/> from the test host.</summary>
    public OperatorAttachmentManager AttachmentManager =>
        _host.Services.GetRequiredService<OperatorAttachmentManager>();
    /// <summary>DI <see cref="ConnectorSerialConfigurationCache"/> from the test host.</summary>
    public ConnectorSerialConfigurationCache SerialConfigCache =>
        _host.Services.GetRequiredService<ConnectorSerialConfigurationCache>();
    /// <summary>DI <see cref="PendingBaudRequestTracker"/> from the test host.</summary>
    public PendingBaudRequestTracker PendingBaud =>
        _host.Services.GetRequiredService<PendingBaudRequestTracker>();
    /// <summary>Ephemeral test CA used to issue server/client certs.</summary>
    public X509Certificate2 Ca => _ca;
    /// <summary>Ephemeral server certificate presented by the connector listener.</summary>
    public X509Certificate2 ServerCertificate => _server;
    /// <summary>
    /// Builds and starts a Development TechConnect host with connector TLS on a free loopback port.
    /// </summary>
    /// <param name="maximumConcurrentConnections">Connector concurrency limit for the test.</param>
    /// <param name="shutdownTimeoutSeconds">Host shutdown timeout configuration.</param>
    /// <param name="cancellationToken">Cancels host start / port wait.</param>
    /// <returns>Ready host after the listener accepts connections.</returns>
    public static async Task<ConnectorTestHost> StartAsync(
        int maximumConcurrentConnections = 10,
        int shutdownTimeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var ca = TestCertificateFactory.CreateCa();
        var server = TestCertificateFactory.CreateServerCertificate(ca);
        var store = new FakeCertificateStore();
        store.Add(server);

        var port = GetFreeTcpPort();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = "Development",
            Args = []
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TechConnectServer:Ssh:Enabled"] = "false",
            ["TechConnectServer:Connector:Enabled"] = "true",
            ["TechConnectServer:Connector:ListenAddress"] = "127.0.0.1",
            ["TechConnectServer:Connector:Port"] = port.ToString(),
            ["TechConnectServer:Connector:ServerCertificateThumbprint"] = TestCertificateFactory.Thumbprint(server),
            ["TechConnectServer:Connector:ServerCertificateStoreName"] = "My",
            ["TechConnectServer:Connector:ServerCertificateStoreLocation"] = "CurrentUser",
            ["TechConnectServer:Connector:ClientCertificateValidation:RequireClientCertificate"] = "true",
            ["TechConnectServer:Connector:ClientCertificateValidation:AllowedCertificateAuthorities:0"] =
                TestCertificateFactory.Thumbprint(ca),
            ["TechConnectServer:Connector:MaximumConcurrentConnections"] = maximumConcurrentConnections.ToString(),
            ["TechConnectServer:Connector:HandshakeTimeoutSeconds"] = "5",
            ["TechConnectServer:Connector:ShutdownTimeoutSeconds"] = shutdownTimeoutSeconds.ToString(),
            ["Logging:File:Directory"] = Path.Combine(contentRoot, "logs"),
            ["Logging:File:FileNamePrefix"] = "connector-test",
            ["Serilog:MinimumLevel:Default"] = "Warning"
        });

        builder.Logging.ClearProviders();

        // Register ephemeral store/trust before host defaults (TryAdd) so tests never touch Windows stores.
        builder.Services.AddSingleton<ICertificateStoreFactory>(_ => new FakeCertificateStoreFactory(store));
        builder.Services.AddSingleton<IClientCertificateTrustAnchorProvider>(_ => new FakeTrustAnchorProvider(ca));
        builder.AddTechConnectServerHost();

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await WaitForPortAsync(port, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        return new ConnectorTestHost(host, port, ca, server, contentRoot);
    }
    /// <summary>
    /// Issues a client certificate from this host's test CA.
    /// </summary>
    /// <param name="includeClientEku">When false, uses serverAuth EKU for negative tests.</param>
    /// <returns>Client certificate for <see cref="ConnectClientAsync"/>.</returns>
    public X509Certificate2 CreateClientCertificate(bool includeClientEku = true) =>
        TestCertificateFactory.CreateClientCertificate(_ca, includeClientEku: includeClientEku);
    /// <summary>
    /// Opens a TCP+TLS client to this host's connector port with optional client certificate.
    /// </summary>
    /// <param name="clientCertificate">Client cert for mutual TLS; null omits client cert.</param>
    /// <param name="tlsHostName">SNI / certificate name used in AuthenticateAsClient.</param>
    /// <param name="cancellationToken">Cancels connect/authenticate.</param>
    /// <returns>SslStream and TcpClient on success; Error set when handshake fails (resources may still need dispose).</returns>
    public async Task<(SslStream? Ssl, TcpClient? Tcp, Exception? Error)> ConnectClientAsync(
        X509Certificate2? clientCertificate,
        string tlsHostName = "localhost",
        CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);
            var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                var clientCerts = new X509Certificate2Collection();
                if (clientCertificate is not null)
                {
                    clientCerts.Add(clientCertificate);
                }

                await ssl.AuthenticateAsClientAsync(
                        new SslClientAuthenticationOptions
                        {
                            TargetHost = tlsHostName,
                            ClientCertificates = clientCerts,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                            {
                                if (cert is null)
                                {
                                    return false;
                                }

                                // Trust only the ephemeral test server certificate for this host.
                                var leaf = cert as X509Certificate2 ?? new X509Certificate2(cert);
                                var thumbprintMatch = string.Equals(
                                    TestCertificateFactory.Thumbprint(leaf),
                                    TestCertificateFactory.Thumbprint(_server),
                                    StringComparison.OrdinalIgnoreCase);

                                // Allow name mismatch only when we intentionally test wrong hostnames elsewhere.
                                var nameOk = (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                                    || string.Equals(tlsHostName, "localhost", StringComparison.OrdinalIgnoreCase);

                                return thumbprintMatch &&
                                       (errors == SslPolicyErrors.None ||
                                        errors == SslPolicyErrors.RemoteCertificateChainErrors ||
                                        (nameOk && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == 0));
                            }
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                return (ssl, tcp, null);
            }
            catch (Exception ex)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                tcp.Dispose();
                return (null, null, ex);
            }
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            return (null, null, ex);
        }
    }
    /// <summary>
    /// Polls <see cref="SessionRegistry"/> until <see cref="ConnectorSessionRegistry.Count"/> equals <paramref name="expected"/>.
    /// </summary>
    /// <param name="expected">Desired registered session count.</param>
    /// <param name="timeout">Maximum wait before <see cref="TimeoutException"/>.</param>
    public async Task WaitForRegisteredCountAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SessionRegistry.Count == expected)
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected {expected} registered sessions but found {SessionRegistry.Count}.");
    }
    /// <summary>
    /// Polls <see cref="Tracker"/> until its count equals <paramref name="expected"/>.
    /// </summary>
    /// <param name="expected">Desired tracked connection count.</param>
    /// <param name="timeout">Maximum wait before <see cref="TimeoutException"/>.</param>
    public async Task WaitForTrackedCountAsync(int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Tracker.Count == expected)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Expected tracker count {expected} but was {Tracker.Count} after {timeout}.");
    }
    /// <summary>
    /// Stops the host, disposes certificates, and best-effort deletes the temp content root.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _host.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        _host.Dispose();
        _server.Dispose();
        _ca.Dispose();

        try
        {
            if (Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
        catch
        {
        }
    }
    /// <summary>
    /// Binds an ephemeral loopback listener briefly to obtain an unused TCP port number.
    /// </summary>
    /// <returns>Port that was free at bind time (may race until the host binds).</returns>
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    /// <summary>
    /// Waits until a TCP connect to <paramref name="port"/> succeeds, then aborts without a TLS handshake.
    /// </summary>
    /// <remarks>
    /// Uses a hard close (linger 0) so the probe does not consume a durable connector handshake slot.
    /// </remarks>
    /// <param name="port">Loopback port to probe.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Avoid AcceptTcpClientAsync probes that consume a TLS handshake slot.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                var connectTask = socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellationToken);
                await connectTask.ConfigureAwait(false);
                // Abort immediately so the server treats this as a failed handshake and frees capacity.
                socket.LingerState = new LingerOption(true, 0);
                socket.Close();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"Connector listener did not accept connections on port {port}.");
    }
}
