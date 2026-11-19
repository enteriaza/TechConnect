// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    WssTestServer.cs
//
// Purpose:
// Local Kestrel WSS test server for transport hardening and mTLS tests. Never installs or removes Windows certificates.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Local Kestrel WSS endpoint. Never installs or removes Windows certificates.
/// Happy-path tests supply the persistent development server certificate from the store.
/// Negative tests supply in-memory certificates only.
/// </summary>
internal sealed class WssTestServer : IAsyncDisposable
{
    /// <summary>
    /// Caller-facing server certificate instance.
    /// </summary>
    private readonly X509Certificate2 _serverCertificate;
    /// <summary>
    /// Certificate instance bound into Kestrel HTTPS.
    /// </summary>
    private readonly X509Certificate2 _kestrelCertificate;
    /// <summary>
    /// When true, Kestrel requires a client certificate.
    /// </summary>
    private readonly bool _requireClientCertificate;
    /// <summary>
    /// Optional normalized client thumbprint required for mTLS.
    /// </summary>
    private readonly string? _expectedClientThumbprint;
    /// <summary>
    /// Running WebApplication host, if started.
    /// </summary>
    private WebApplication? _app;
    /// <summary>
    /// Ephemeral listen port assigned by Kestrel.
    /// </summary>
    private int _port;
    /// <summary>
    /// Most recently accepted client WebSocket.
    /// </summary>
    private WebSocket? _activeSocket;

    /// <summary>
    /// Private constructor; use StartWithPersistentCertificatesAsync or StartWithInMemoryCertificateAsync.
    /// </summary>
    private WssTestServer(
        X509Certificate2 serverCertificate,
        bool requireClientCertificate,
        string? expectedClientThumbprint,
        bool rematerializeExportableKey)
    {
        _serverCertificate = serverCertificate;
        _requireClientCertificate = requireClientCertificate;
        _expectedClientThumbprint = expectedClientThumbprint;
        _kestrelCertificate = rematerializeExportableKey
            ? MaterializeExportable(serverCertificate)
            : new X509Certificate2(serverCertificate);
    }

    /// <summary>
    /// wss://127.0.0.1:{port}/ws listen URI.
    /// </summary>
    public Uri WssUri => new($"wss://127.0.0.1:{_port}/ws");

    /// <summary>
    /// https://127.0.0.1:{port}/ listen URI.
    /// </summary>
    public Uri HttpsUri => new($"https://127.0.0.1:{_port}/");

    /// <summary>
    /// http://127.0.0.1:{port}/ companion URI (not used for connector traffic).
    /// </summary>
    public Uri HttpUri => new($"http://127.0.0.1:{_port}/");

    /// <summary>
    /// Binary WebSocket messages received from the connected client.
    /// </summary>
    public ConcurrentQueue<byte[]> ReceivedBinary { get; } = new();

    /// <summary>
    /// Text WebSocket messages received from the connected client.
    /// </summary>
    public ConcurrentQueue<string> ReceivedText { get; } = new();

    /// <summary>
    /// Count of accepted WebSocket clients for this server lifetime.
    /// </summary>
    public int ConnectedClients { get; private set; }

    /// <summary>
    /// Server certificate supplied at construction.
    /// </summary>
    public X509Certificate2 ServerCertificate => _serverCertificate;

    /// <summary>
    /// Starts using the persistent development server certificate from the Windows store.
    /// Fails clearly when provisioning is incomplete. Never modifies certificate stores.
    /// </summary>
    public static async Task<WssTestServer> StartWithPersistentCertificatesAsync(
        bool requireClientCertificate = false)
    {
        var serverCert = PersistentDevCertificates.LoadServerCertificate();
        string? clientThumbprint = null;
        if (requireClientCertificate)
        {
            var registry = PersistentDevCertificates.RequireProvisioned();
            clientThumbprint = registry.ClientThumbprint;
        }

        var server = new WssTestServer(
            serverCert,
            requireClientCertificate,
            clientThumbprint,
            rematerializeExportableKey: false);
        await server.StartCoreAsync().ConfigureAwait(false);
        return server;
    }

    /// <summary>
    /// Starts with caller-supplied in-memory certificates for negative TLS tests.
    /// Never installs those certificates into Windows stores.
    /// </summary>
    public static async Task<WssTestServer> StartWithInMemoryCertificateAsync(
        X509Certificate2 serverCertificate,
        bool requireClientCertificate = false,
        string? expectedClientThumbprint = null)
    {
        var server = new WssTestServer(
            serverCertificate,
            requireClientCertificate,
            expectedClientThumbprint,
            rematerializeExportableKey: true);
        await server.StartCoreAsync().ConfigureAwait(false);
        return server;
    }

    /// <summary>
    /// Sends a complete binary message to the active client socket.
    /// </summary>
    public async Task SendBinaryAsync(byte[] payload)
    {
        var socket = _activeSocket ?? throw new InvalidOperationException("No active WebSocket client.");
        await socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a complete text message to the active client socket.
    /// </summary>
    public async Task SendTextAsync(string text)
    {
        var socket = _activeSocket ?? throw new InvalidOperationException("No active WebSocket client.");
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a binary fragment with the given endOfMessage flag.
    /// </summary>
    public Task SendBinaryFragmentAsync(ReadOnlyMemory<byte> payload, bool endOfMessage)
    {
        var socket = _activeSocket ?? throw new InvalidOperationException("No active WebSocket client.");
        return socket.SendAsync(payload, WebSocketMessageType.Binary, endOfMessage, CancellationToken.None).AsTask();
    }

    /// <summary>
    /// Sends a text fragment with the given endOfMessage flag.
    /// </summary>
    public Task SendTextFragmentAsync(ReadOnlyMemory<byte> payload, bool endOfMessage)
    {
        var socket = _activeSocket ?? throw new InvalidOperationException("No active WebSocket client.");
        return socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage, CancellationToken.None).AsTask();
    }

    /// <summary>
    /// Gracefully closes the active client socket.
    /// </summary>
    public async Task CloseActiveAsync()
    {
        var socket = _activeSocket;
        if (socket is null)
        {
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test-close", cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            socket.Abort();
        }
    }

    /// <summary>
    /// Aborts the active client socket.
    /// </summary>
    public void AbortActive() => _activeSocket?.Abort();

    /// <summary>
    /// Stops Kestrel and disposes certificates.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _app.StopAsync(stopCts.Token).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }

        _kestrelCertificate.Dispose();
        _serverCertificate.Dispose();
    }

    /// <summary>
    /// Binds Kestrel HTTPS+WebSockets and records the ephemeral port.
    /// </summary>
    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.UseHttps(new HttpsConnectionAdapterOptions
                {
                    ServerCertificate = _kestrelCertificate,
                    ClientCertificateMode = _requireClientCertificate
                        ? ClientCertificateMode.RequireCertificate
                        : ClientCertificateMode.NoCertificate,
                    ClientCertificateValidation = ValidateClientCertificate
                });
            });
        });

        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket required").ConfigureAwait(false);
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            ConnectedClients++;
            _activeSocket = socket;

            var buffer = new byte[64 * 1024];
            var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        CancellationToken.None).ConfigureAwait(false);
                    break;
                }

                if (result.Count > 0)
                {
                    message.Write(buffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                var payload = message.ToArray();
                message.SetLength(0);
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    ReceivedBinary.Enqueue(payload);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    ReceivedText.Enqueue(System.Text.Encoding.UTF8.GetString(payload));
                }
            }

            if (ReferenceEquals(_activeSocket, socket))
            {
                _activeSocket = null;
            }
        });

        await app.StartAsync().ConfigureAwait(false);
        _app = app;

        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var feature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Unable to resolve Kestrel listen address.");
        var address = feature.Addresses.First();
        _port = new Uri(address).Port;
    }

    /// <summary>
    /// Optional client-certificate thumbprint check when mTLS is required.
    /// </summary>
    private bool ValidateClientCertificate(
        X509Certificate2? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? _,
        System.Net.Security.SslPolicyErrors __)
    {
        if (!_requireClientCertificate)
        {
            return true;
        }

        if (certificate is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_expectedClientThumbprint))
        {
            return true;
        }

        var actual = TechConnect.Client.Configuration.RemoteServiceOptionsValidator.NormalizeThumbprint(
            certificate.Thumbprint ?? string.Empty);
        var expected = TechConnect.Client.Configuration.RemoteServiceOptionsValidator.NormalizeThumbprint(
            _expectedClientThumbprint);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-imports a certificate as exportable for Kestrel when using in-memory certs.
    /// </summary>
    private static X509Certificate2 MaterializeExportable(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }
}
