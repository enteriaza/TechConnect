// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    WebSocketTeardownHardeningTests.cs
//
// Purpose:
// Verifies WebSocket transport teardown hardening under abort/close races.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Transport;

/// <summary>
/// Legacy WSS transport teardown coverage.
/// Active TechConnect remote transport is TCP/mTLS (<see cref="TcpMtlsRemoteConsoleTransport"/>);
/// WSS is not registered in production DI. These tests remain as historical guards for
/// <see cref="WebSocketRemoteConsoleTransport"/> shutdown behaviour only.
/// </summary>
public sealed class WebSocketTeardownHardeningTests
{
    /// <summary>
    /// Scenario: DisconnectAsync while a receive is pending.
    /// Expected: completes quickly without hang.
    /// </summary>
    [Fact]
    public async Task Disconnect_while_receive_pending_completes_quickly()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeTrue();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await transport.DisconnectAsync(CancellationToken.None);
        sw.Stop();

        transport.IsConnected.Should().BeFalse();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Scenario: DisconnectAsync called repeatedly.
    /// Expected: safe/idempotent.
    /// </summary>
    [Fact]
    public async Task Repeated_disconnect_is_safe()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        await transport.DisconnectAsync(CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: remote closes first, then local DisconnectAsync.
    /// Expected: completes cleanly.
    /// </summary>
    [Fact]
    public async Task Disconnect_after_remote_close_completes()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Faulted += (_, message) => faulted.TrySetResult(message);

        await server.CloseActiveAsync();
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await transport.DisconnectAsync(CancellationToken.None);
        transport.IsConnected.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: DisconnectAsync while send is in flight.
    /// Expected: does not hang.
    /// </summary>
    [Fact]
    public async Task Disconnect_while_send_in_flight_does_not_hang()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var payload = new byte[32 * 1024];
        Random.Shared.NextBytes(payload);

        var send = transport.SendSerialDataAsync(payload, CancellationToken.None);
        var disconnect = transport.DisconnectAsync(CancellationToken.None);

        await Task.WhenAny(Task.WhenAll(send.ContinueWith(_ => { }), disconnect), Task.Delay(TimeSpan.FromSeconds(8)));
        await disconnect.WaitAsync(TimeSpan.FromSeconds(2));
        transport.IsConnected.Should().BeFalse();

        try
        {
            await send;
        }
        catch (Exception)
        {
            // Send may fail after abort; that is expected.
        }
    }

    /// <summary>
    /// Scenario: Send after teardown has begun.
    /// Expected: fails fast.
    /// </summary>
    [Fact]
    public async Task Send_after_teardown_begins_fails_fast()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);

        var act = async () => await transport.SendSerialDataAsync("x"u8.ToArray(), CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();
    }

    /// <summary>
    /// Scenario: cancel a second send while waiting on the send gate during a large in-flight send.
    /// Expected: cancelled send throws <see cref="OperationCanceledException"/> and the gate remains usable.
    /// Skipped: timing-dependent on legacy WSS; prefer TCP mTLS teardown coverage for live behaviour.
    /// </summary>
    // Disposition: keep skipped in this legacy WSS fixture (do not delete yet; do not wire WSS into DI).
    // Root cause: race — if the first SendSerialDataAsync finishes before the second hits _sendGate.WaitAsync,
    // Cancel() never produces OperationCanceledException. Not worth hardening an obsolete transport.
    [Fact(Skip =
        "Legacy WSS only: cancel-while-waiting-on-_sendGate is timing-dependent and WSS is not the active transport. " +
        "Isolate as skipped legacy coverage; prefer TcpMtlsRemoteConsoleTransport tests for live teardown.")]
    public async Task Cancellation_waiting_for_send_gate_leaves_gate_usable()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var large = new byte[48 * 1024];
        Random.Shared.NextBytes(large);
        var hold = transport.SendSerialDataAsync(large, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var cancelled = transport.SendSerialDataAsync("n"u8.ToArray(), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await cancelled);
        try
        {
            await hold;
        }
        catch (Exception)
        {
        }

        await transport.SendSerialDataAsync("ok"u8.ToArray(), CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);
    }

    /// <summary>
    /// Builds a <see cref="WebSocketRemoteConsoleTransport"/> pointed at the given <see cref="WssTestServer"/>.
    /// </summary>
    private static WebSocketRemoteConsoleTransport CreateTransport(WssTestServer server)
    {
        var options = new TechConnect.Client.Configuration.RemoteServiceOptions
        {
            BaseUrl = server.HttpsUri.ToString().TrimEnd('/'),
            WebSocketPath = "/ws",
            ConnectTimeoutSeconds = 5,
            HeartbeatIntervalSeconds = 15,
            MaximumMessageBytes = 65_536,
            ClientCertificate = new TechConnect.Client.Configuration.ClientCertificateOptions { Thumbprint = string.Empty }
        };

        return new WebSocketRemoteConsoleTransport(
            new FakeClientCertificateProvider { IsConfigured = false },
            Microsoft.Extensions.Options.Options.Create(options),
            new CollectingLogger<WebSocketRemoteConsoleTransport>());
    }
}
