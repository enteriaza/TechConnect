// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TechConnectSshServer.cs
//
// Purpose:
// Extends DevTunnels SshServer to capture each accepted TCP remote endpoint for
// SessionOpened handlers before the next accept proceeds.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace TechConnect.Server.Ssh;

/// <summary>
/// Captures the TCP remote endpoint of each accepted SSH connection.
/// </summary>
/// <remarks>
/// <see cref="SshServer"/> accepts sequentially and raises <c>SessionOpened</c>
/// before the next accept, so a single pending endpoint is sufficient and safe.
/// </remarks>
internal sealed class TechConnectSshServer : SshServer
{
    /// <summary>
    /// Remote endpoint string for the most recently accepted TCP connection, pending take.
    /// </summary>
    private string? _pendingRemoteEndpoint;

    /// <summary>
    /// Initializes the TechConnect SSH server wrapper.
    /// </summary>
    /// <param name="config">DevTunnels session configuration.</param>
    /// <param name="trace">Trace source for the underlying SSH stack.</param>
    public TechConnectSshServer(SshSessionConfiguration config, TraceSource trace)
        : base(config, trace)
    {
    }

    /// <summary>
    /// Takes the remote endpoint captured for the most recently accepted TCP connection.
    /// </summary>
    /// <remarks>
    /// Call from the <c>SessionOpened</c> handler before the next accept.
    /// </remarks>
    /// <returns>Formatted endpoint, or <see langword="null"/> when unavailable.</returns>
    public string? TakePendingRemoteEndpoint() =>
        Interlocked.Exchange(ref _pendingRemoteEndpoint, null);

    /// <summary>
    /// Accepts the next TCP connection and records its remote endpoint before returning the stream.
    /// </summary>
    /// <param name="listener">TCP listener owned by the base server.</param>
    /// <returns>Accepted stream, or <see langword="null"/> when the base accept returns null.</returns>
    protected override async Task<Stream?> AcceptConnectionAsync(TcpListener listener)
    {
        var stream = await base.AcceptConnectionAsync(listener).ConfigureAwait(false);
        _pendingRemoteEndpoint = TryFormatRemoteEndpoint(stream);
        return stream;
    }

    /// <summary>
    /// Formats the remote IP endpoint of a <see cref="NetworkStream"/> when possible.
    /// </summary>
    /// <param name="stream">Accepted stream; non-network streams yield <see langword="null"/>.</param>
    /// <returns>Formatted endpoint, or <see langword="null"/> on failure/disposal.</returns>
    internal static string? TryFormatRemoteEndpoint(Stream? stream)
    {
        if (stream is not NetworkStream networkStream)
        {
            return null;
        }

        try
        {
            if (networkStream.Socket.RemoteEndPoint is not IPEndPoint endpoint)
            {
                return null;
            }

            return FormatEndpoint(endpoint);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>
    /// Formats an <see cref="IPEndPoint"/> as <c>ip:port</c> or <c>[ipv6]:port</c>.
    /// </summary>
    /// <param name="endpoint">Remote endpoint.</param>
    /// <returns>Invariant-culture endpoint string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    internal static string FormatEndpoint(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"[{endpoint.Address}]:{endpoint.Port}");
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{endpoint.Address}:{endpoint.Port}");
    }
}
