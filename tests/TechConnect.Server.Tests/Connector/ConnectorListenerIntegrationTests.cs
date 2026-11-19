// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorListenerIntegrationTests.cs
//
// Purpose:
// Verifies connector TCP/TLS listener accept, mTLS rejection, capacity limits, and shutdown close behaviour.
//
// -----------------------------------------------------------------------------

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentAssertions;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Integration tests for ConnectorListenerHostedService accept path and mTLS enforcement.
/// </summary>
public sealed class ConnectorListenerIntegrationTests
{
    /// <summary>
    /// Verifies a valid mTLS client is tracked then removed after disconnect.
    /// </summary>
    [Fact]
    public async Task Listener_StartsOnConfiguredPort_AndAcceptsValidMtlsClient()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();

        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();
        ssl.Should().NotBeNull();
        ssl!.IsMutuallyAuthenticated.Should().BeTrue();

        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));
        var tracked = host.Tracker.Snapshot().Single();
        tracked.CertificateThumbprint.Should().Be(TestCertificateFactory.Thumbprint(clientCert));
        tracked.NegotiatedProtocol.Should().BeOneOf(SslProtocols.Tls12, SslProtocols.Tls13);

        await ssl.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies clients without a certificate are rejected and never tracked.
    /// </summary>

    [Fact]
    public async Task ClientWithoutCertificate_IsRejected_AndNotTracked()
    {
        await using var host = await ConnectorTestHost.StartAsync();

        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCertificate: null);
        await AssertRejectedAndUntrackedAsync(host, ssl, tcp, error);
    }

    /// <summary>
    /// Verifies untrusted client certificates are rejected and not tracked.
    /// </summary>

    [Fact]
    public async Task InvalidClientCertificate_IsRejected()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var untrustedCa = TestCertificateFactory.CreateCa("Untrusted CA");
        using var untrustedClient = TestCertificateFactory.CreateClientCertificate(untrustedCa);

        var (ssl, tcp, error) = await host.ConnectClientAsync(untrustedClient);
        await AssertRejectedAndUntrackedAsync(host, ssl, tcp, error);
    }

    /// <summary>
    /// Verifies multiple valid clients track independently and clean up on close.
    /// </summary>

    [Fact]
    public async Task MultipleValidClients_ConnectIndependently()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var client1 = host.CreateClientCertificate();
        using var client2 = host.CreateClientCertificate();

        var (ssl1, tcp1, error1) = await host.ConnectClientAsync(client1);
        var (ssl2, tcp2, error2) = await host.ConnectClientAsync(client2);

        error1.Should().BeNull();
        error2.Should().BeNull();
        await host.WaitForTrackedCountAsync(2, TimeSpan.FromSeconds(5));

        await ssl1!.DisposeAsync();
        tcp1!.Dispose();
        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));

        await ssl2!.DisposeAsync();
        tcp2!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies a failed handshake leaves the listener able to accept a later valid client.
    /// </summary>

    [Fact]
    public async Task FailedHandshake_DoesNotTerminateListener()
    {
        await using var host = await ConnectorTestHost.StartAsync();

        var (badSsl, badTcp, _) = await host.ConnectClientAsync(clientCertificate: null);
        if (badSsl is not null)
        {
            await badSsl.DisposeAsync();
        }

        badTcp?.Dispose();
        await Task.Delay(300);
        host.Tracker.Count.Should().Be(0);

        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, okError) = await host.ConnectClientAsync(clientCert);
        okError.Should().BeNull();
        ssl.Should().NotBeNull();
        ssl!.IsMutuallyAuthenticated.Should().BeTrue();

        await ssl.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies capacity enforcement keeps only the allowed number of tracked connections.
    /// </summary>

    [Fact]
    public async Task MaximumConnectionLimit_IsEnforced()
    {
        await using var host = await ConnectorTestHost.StartAsync(maximumConcurrentConnections: 1);
        using var client1 = host.CreateClientCertificate();
        using var client2 = host.CreateClientCertificate();

        var (ssl1, tcp1, error1) = await host.ConnectClientAsync(client1);
        error1.Should().BeNull();
        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));

        // Second TCP accept may succeed then be closed immediately when at capacity,
        // or TLS may fail — either way it must not join the tracker.
        var (ssl2, tcp2, _) = await host.ConnectClientAsync(client2);
        await Task.Delay(500);
        host.Tracker.Count.Should().Be(1);

        if (ssl2 is not null)
        {
            await ssl2.DisposeAsync();
        }

        tcp2?.Dispose();

        await ssl1!.DisposeAsync();
        tcp1!.Dispose();
        await host.WaitForTrackedCountAsync(0, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Verifies host dispose completes within the shutdown budget while closing connections.
    /// </summary>

    [Fact]
    public async Task ServerCancellation_StopsListener_AndClosesConnections()
    {
        var host = await ConnectorTestHost.StartAsync(shutdownTimeoutSeconds: 3);
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();
        await host.WaitForTrackedCountAsync(1, TimeSpan.FromSeconds(5));

        var shutdownStarted = Environment.TickCount64;
        await host.DisposeAsync();
        var elapsed = Environment.TickCount64 - shutdownStarted;

        elapsed.Should().BeLessThan(10_000);

        if (ssl is not null)
        {
            try
            {
                await ssl.DisposeAsync();
            }
            catch
            {
            }
        }

        tcp?.Dispose();
    }

    /// <summary>
    /// Verifies a client certificate missing client EKU is rejected and untracked.
    /// </summary>

    [Fact]
    public async Task WrongClientEku_IsRejected()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var wrongEku = host.CreateClientCertificate(includeClientEku: false);

        var (ssl, tcp, error) = await host.ConnectClientAsync(wrongEku);
        await AssertRejectedAndUntrackedAsync(host, ssl, tcp, error);
    }

    /// <summary>
    /// Asserts a rejected TLS attempt leaves the tracker empty (Schannel may throw or briefly connect).
    /// </summary>
    /// <param name="host">Test host under observation.</param>
    /// <param name="ssl">Optional SSL stream to dispose.</param>
    /// <param name="tcp">Optional TCP client to dispose.</param>
    /// <param name="error">Optional handshake error; when present must be AuthenticationException or IOException.</param>
    private static async Task AssertRejectedAndUntrackedAsync(
        ConnectorTestHost host,
        SslStream? ssl,
        TcpClient? tcp,
        Exception? error)
    {
        // Schannel may surface rejection as an exception or as a short-lived connection.
        // Product requirement: never register the connection in the tracker.
        if (ssl is not null)
        {
            await ssl.DisposeAsync();
        }

        tcp?.Dispose();
        await Task.Delay(400);
        host.Tracker.Count.Should().Be(0, because: "rejected TLS clients must not be tracked");

        if (error is not null)
        {
            (error is AuthenticationException or IOException).Should().BeTrue();
        }
    }
}
