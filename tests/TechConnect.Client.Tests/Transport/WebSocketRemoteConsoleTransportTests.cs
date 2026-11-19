// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    WebSocketRemoteConsoleTransportTests.cs
//
// Purpose:
// Verifies WebSocketRemoteConsoleTransport connect, send, receive, and TLS client-cert behaviour against WssTestServer.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Transport;

/// <summary>
/// Connect/send/receive/TLS client-cert tests for WebSocketRemoteConsoleTransport against WssTestServer.
/// </summary>
public sealed class WebSocketRemoteConsoleTransportTests
{
    /// <summary>
    /// Builds a transport pointed at <paramref name="server"/> with optional cert provider and options mutator.
    /// </summary>
    private static WebSocketRemoteConsoleTransport CreateTransport(
        WssTestServer server,
        TechConnect.Client.Abstractions.IClientCertificateProvider? certProvider = null,
        Action<RemoteServiceOptions>? configure = null)
    {
        var options = new RemoteServiceOptions
        {
            BaseUrl = server.HttpsUri.ToString().TrimEnd('/'),
            WebSocketPath = "/ws",
            ConnectTimeoutSeconds = 5,
            HeartbeatIntervalSeconds = 15,
            ClientCertificate = new ClientCertificateOptions { Thumbprint = string.Empty }
        };
        configure?.Invoke(options);

        certProvider ??= new FakeClientCertificateProvider { IsConfigured = false };
        return new WebSocketRemoteConsoleTransport(
            certProvider,
            Microsoft.Extensions.Options.Options.Create(options),
            new CollectingLogger<WebSocketRemoteConsoleTransport>());
    }

    /// <summary>
    /// Builds a transport from explicit <see cref="RemoteServiceOptions"/> (negative/config tests).
    /// </summary>
    private static WebSocketRemoteConsoleTransport CreateTransport(
        RemoteServiceOptions options,
        TechConnect.Client.Abstractions.IClientCertificateProvider? certProvider = null)
    {
        certProvider ??= new FakeClientCertificateProvider { IsConfigured = false };
        return new WebSocketRemoteConsoleTransport(
            certProvider,
            Microsoft.Extensions.Options.Options.Create(options),
            new CollectingLogger<WebSocketRemoteConsoleTransport>());
    }

    /// <summary>
    /// Creates a Windows-store-backed <see cref="ClientCertificateProvider"/> for mTLS happy-path tests.
    /// </summary>
    private static ClientCertificateProvider CreateStoreBackedClientProvider(RemoteServiceOptions options)
    {
        return new ClientCertificateProvider(
            Microsoft.Extensions.Options.Options.Create(options),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new CollectingLogger<ClientCertificateProvider>(),
            new WindowsCertificateStoreFactory());
    }

    /// <summary>
    /// Scenario: connect to WssTestServer and round-trip binary.
    /// Expected: payload preserved.
    /// </summary>
    [Fact]
    public async Task Connect_SendReceive_BinaryRoundTrip()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);

        await transport.ConnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeTrue();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SerialDataReceived += (_, data) => received.TrySetResult(data.ToArray());

        var payload = "hello-serial"u8.ToArray();
        await transport.SendSerialDataAsync(payload, CancellationToken.None);

        await TestHelpers.WaitUntilAsync(
            () => !server.ReceivedBinary.IsEmpty,
            because: "server should receive binary payload");
        server.ReceivedBinary.TryDequeue(out var fromClient).Should().BeTrue();
        fromClient.Should().Equal(payload);

        var reply = "from-server"u8.ToArray();
        await server.SendBinaryAsync(reply);
        var fromServer = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fromServer.Should().Equal(reply);
    }

    /// <summary>
    /// Scenario: large binary message send/receive.
    /// Expected: integrity preserved.
    /// </summary>
    [Fact]
    public async Task LargeBinaryMessage_PreservesIntegrity()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SerialDataReceived += (_, data) => received.TrySetResult(data.ToArray());

        var large = new byte[48 * 1024];
        Random.Shared.NextBytes(large);

        await transport.SendSerialDataAsync(large, CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => !server.ReceivedBinary.IsEmpty);
        server.ReceivedBinary.TryDequeue(out var echoed).Should().BeTrue();
        echoed.Should().Equal(large);

        await server.SendBinaryAsync(large);
        var roundTrip = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        roundTrip.Should().Equal(large);
    }

    /// <summary>
    /// Scenario: server sends text frames.
    /// Expected: SerialDataReceived is not raised.
    /// </summary>
    [Fact]
    public async Task TextFrames_DoNotRaiseSerialDataReceived()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var serialRaised = false;
        transport.SerialDataReceived += (_, _) => serialRaised = true;

        await server.SendTextAsync("control-frame");
        await Task.Delay(100);
        serialRaised.Should().BeFalse();

        var binary = "still-binary"u8.ToArray();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SerialDataReceived += (_, data) => received.TrySetResult(data.ToArray());
        await server.SendBinaryAsync(binary);
        (await received.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(binary);
    }

    /// <summary>
    /// Scenario: concurrent SendSerialDataAsync calls.
    /// Expected: all payloads arrive intact.
    /// </summary>
    [Fact]
    public async Task ConcurrentSends_PreservePayloads()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var payloads = Enumerable.Range(0, 20)
            .Select(i => System.Text.Encoding.UTF8.GetBytes($"payload-{i}"))
            .ToArray();

        await Task.WhenAll(payloads.Select(p => transport.SendSerialDataAsync(p, CancellationToken.None)));

        await TestHelpers.WaitUntilAsync(
            () => server.ReceivedBinary.Count >= payloads.Length,
            timeout: TimeSpan.FromSeconds(10));

        var received = new List<byte[]>();
        while (server.ReceivedBinary.TryDequeue(out var item))
        {
            received.Add(item);
        }

        received.Should().HaveCount(payloads.Length);
        received.Should().BeEquivalentTo(payloads, o => o.WithoutStrictOrdering());
    }

    /// <summary>
    /// Scenario: server aborts/closes unexpectedly.
    /// Expected: transport Faulted.
    /// </summary>
    [Fact]
    public async Task UnexpectedServerClosure_FaultsTransport()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Faulted += (_, message) => faulted.TrySetResult(message);

        server.AbortActive();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TestHelpers.WaitUntilAsync(() => !transport.IsConnected, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Scenario: cancel during ConnectAsync.
    /// Expected: throws/cancels.
    /// </summary>
    [Fact]
    public async Task CancellationDuringConnect_Throws()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await transport.ConnectAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Scenario: dispose while connected.
    /// Expected: resources released.
    /// </summary>
    [Fact]
    public async Task DisposeWhileConnected_ReleasesResources()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeTrue();

        await transport.DisposeAsync();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: disconnect then connect again.
    /// Expected: second connect succeeds.
    /// </summary>
    [Fact]
    public async Task ReconnectAfterDisconnect_Succeeds()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);

        await transport.ConnectAsync(CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeFalse();

        await transport.ConnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: endpoint not configured.
    /// Expected: configuration exception.
    /// </summary>
    [Fact]
    public async Task UnconfiguredEndpoint_ThrowsConfiguration()
    {
        var options = new RemoteServiceOptions { BaseUrl = string.Empty };
        await using var transport = CreateTransport(options);
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportConfigurationException>();
    }

    /// <summary>
    /// Scenario: http BaseUrl.
    /// Expected: rejected.
    /// </summary>
    [Fact]
    public async Task HttpEndpoint_Rejected()
    {
        var options = new RemoteServiceOptions
        {
            BaseUrl = "http://127.0.0.1/ws",
            WebSocketPath = "/ws"
        };

        RemoteEndpoint.IsConfigured(options).Should().BeFalse();
        var act = () => RemoteEndpoint.CreateWebSocketUri(options);
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*HTTPS*");

        await using var transport = CreateTransport(options);
        var connectAct = async () => await transport.ConnectAsync(CancellationToken.None);
        await connectAct.Should().ThrowAsync<RemoteTransportConfigurationException>();
    }

    /// <summary>
    /// Scenario: attempt to derive ws from non-https.
    /// Expected: only wss from https is allowed.
    /// </summary>
    [Fact]
    public void WsScheme_CannotBeDerived_OnlyWssFromHttps()
    {
        var options = new RemoteServiceOptions
        {
            BaseUrl = "ws://localhost/ws",
            WebSocketPath = "/ws"
        };

        RemoteEndpoint.IsConfigured(options).Should().BeFalse();
        var act = () => RemoteEndpoint.CreateWebSocketUri(options);
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*HTTPS*");
    }

    /// <summary>
    /// Scenario: client cert required but missing.
    /// Expected: throws.
    /// </summary>
    [Fact]
    public async Task MissingClientCertificateWhenConfigured_Throws()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        var certProvider = new FakeClientCertificateProvider
        {
            IsConfigured = true
        };

        await using var transport = CreateTransport(server, certProvider, o =>
        {
            o.ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = new string('A', 40)
            };
        });

        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportConfigurationException>()
            .WithMessage("*thumbprint*");
    }

    /// <summary>
    /// Scenario: mTLS with provisioned persistent client cert.
    /// Expected: connect succeeds.
    /// </summary>
    [Fact]
    public async Task MutualTls_WithPersistentClientCertificate_Connects()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync(requireClientCertificate: true);
        var options = PersistentDevCertificates.CreateClientOptions(server.HttpsUri.ToString());
        var certProvider = CreateStoreBackedClientProvider(options);

        await using var transport = CreateTransport(options, certProvider);
        await transport.ConnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: untrusted server certificate.
    /// Expected: RemoteTransportUnavailableException (or equivalent).
    /// </summary>
    [Fact]
    public async Task UntrustedServerCertificate_ThrowsUnavailable()
    {
        // In-memory untrusted CA — never installed into Windows stores.
        using var ca = TestCertificateFactory.CreateCa("TechConnect.Client Untrusted CA");
        using var serverCert = TestCertificateFactory.CreateServerCertificate(ca);

        await using var server = await WssTestServer.StartWithInMemoryCertificateAsync(serverCert);
        await using var transport = CreateTransport(server);
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: TLS hostname mismatch.
    /// Expected: unavailable/failure.
    /// </summary>
    [Fact]
    public async Task HostnameMismatch_ThrowsUnavailable()
    {
        // Negative leaf signed by the persistent trusted CA — in-memory only, never installed.
        using var caPrivate = LoadCaPrivateKeyForNegativeTests();
        using var wrongHost = TestCertificateFactory.CreateServerCertificate(caPrivate, hostName: "wrong.host.test");

        await using var server = await WssTestServer.StartWithInMemoryCertificateAsync(wrongHost);
        await using var transport = CreateTransport(server);
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: expired server certificate.
    /// Expected: unavailable/failure.
    /// </summary>
    [Fact]
    public async Task ExpiredServerCertificate_ThrowsUnavailable()
    {
        using var caPrivate = LoadCaPrivateKeyForNegativeTests();
        // Leaf must lie within the persistent CA validity window, but still be expired now.
        var notBefore = caPrivate.NotBefore.ToUniversalTime().AddHours(1);
        var notAfter = DateTimeOffset.UtcNow.AddHours(-1);
        notAfter.Should().BeAfter(notBefore, because: "CA must already be valid before the expired leaf ends");

        using var expired = TestCertificateFactory.CreateServerCertificate(
            caPrivate,
            hostName: "localhost",
            notBefore: notBefore,
            notAfter: notAfter);

        await using var server = await WssTestServer.StartWithInMemoryCertificateAsync(expired);
        await using var transport = CreateTransport(server);
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Loads the provisioned development CA private key from CurrentUser\\My for issuing negative-test leaves.
    /// Does not install or remove certificates.
    /// </summary>
    private static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadCaPrivateKeyForNegativeTests()
    {
        var registry = PersistentDevCertificates.RequireProvisioned();
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My,
            System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly |
                   System.Security.Cryptography.X509Certificates.OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(
            System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
            RemoteServiceOptionsValidator.NormalizeThumbprint(registry.CaThumbprint),
            validOnly: false);
        var cert = matches.OfType<System.Security.Cryptography.X509Certificates.X509Certificate2>()
            .FirstOrDefault(c => c.HasPrivateKey)
            ?? throw new InvalidOperationException(
                "Development CA private key not found in CurrentUser\\My for negative TLS leaf issuance.");
        return new System.Security.Cryptography.X509Certificates.X509Certificate2(cert);
    }
}
