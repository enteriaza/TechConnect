// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    LoopbackRadiusServer.cs
//
// Purpose:
// Regression tests and helpers covering LoopbackRadiusServer behaviour.
// Fixture: LoopbackRadiusServer.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Tests.Authentication.Radius.Transport;

/// <summary>
/// Minimal loopback UDP RADIUS responder for transport tests.
/// </summary>
internal sealed class LoopbackRadiusServer : IAsyncDisposable
{
    /// <summary>
    /// udp test helper member.
    /// </summary>
    private readonly UdpClient _udp;
    /// <summary>
    /// shared Secret test helper member.
    /// </summary>
    private readonly byte[] _sharedSecret;
    /// <summary>
    /// Field <c>_cts</c> used by this implementation.
    /// </summary>
    private readonly CancellationTokenSource _cts = new();
    /// <summary>
    /// loop test helper member.
    /// </summary>
    private readonly Task _loop;
    /// <summary>
    /// handler test helper member.
    /// </summary>
    private Func<byte[], byte[]?> _handler;
    /// <summary>
    /// Creates a LoopbackRadiusServer test helper.
    /// </summary>

    private LoopbackRadiusServer(UdpClient udp, byte[] sharedSecret, Func<byte[], byte[]?> handler)
    {
        _udp = udp;
        _sharedSecret = sharedSecret;
        _handler = handler;
        _loop = Task.Run(RunAsync);
    }
    /// <summary>
    /// set test helper member.
    /// </summary>

    public IPEndPoint EndPoint { get; private set; } = null!;
    /// <summary>
    /// set test helper member.
    /// </summary>

    public int DatagramsReceived { get; private set; }
    /// <summary>
    /// Verifies Start.
    /// </summary>

    public static LoopbackRadiusServer Start(
        byte[] sharedSecret,
        Func<byte[], byte[]?> handler,
        AddressFamily family = AddressFamily.InterNetwork)
    {
        ArgumentNullException.ThrowIfNull(sharedSecret);
        var local = family == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Loopback, 0)
            : new IPEndPoint(IPAddress.Loopback, 0);
        var udp = new UdpClient(local);
        // Defensive copy — Dispose zeros the secret and must not mutate caller/static vectors.
        var server = new LoopbackRadiusServer(udp, (byte[])sharedSecret.Clone(), handler)
        {
            EndPoint = (IPEndPoint)udp.Client.LocalEndPoint!
        };
        return server;
    }
    /// <summary>
    /// Verifies Set Handler.
    /// </summary>

    public void SetHandler(Func<byte[], byte[]?> handler) => _handler = handler;
    /// <summary>
    /// Verifies Build Access Response.
    /// </summary>

    public static byte[] BuildAccessResponse(
        RadiusPacketCode code,
        byte identifier,
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret,
        params RadiusAttribute[] attributes)
    {
        var packet = new RadiusPacket(code, identifier);
        foreach (var attribute in attributes)
        {
            packet.AddAttribute(attribute);
        }

        packet.SetAuthenticator(sharedSecret, requestAuthenticator);
        return packet.RawData.ToArray();
    }
    /// <summary>
    /// Verifies Run Async.
    /// </summary>

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                DatagramsReceived++;
                var response = _handler(result.Buffer);
                if (response is null)
                {
                    continue;
                }

                await _udp.SendAsync(response, result.RemoteEndPoint, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
    /// <summary>
    /// Verifies Dispose Async.
    /// </summary>

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        _cts.Dispose();
        CryptographicOperations.ZeroMemory(_sharedSecret);
    }
}
