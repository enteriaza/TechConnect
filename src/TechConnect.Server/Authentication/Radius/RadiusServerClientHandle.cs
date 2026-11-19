// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusServerClientHandle.cs
//
// Purpose:
// Owns one configured RADIUS server's UDP client and shared-secret bytes for the
// application lifetime, zeroing secret material on dispose.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// One configured RADIUS server with its dedicated UDP client and secret material.
/// </summary>
/// <remarks>
/// Owns <see cref="Client"/> disposal and zeroes <see cref="SharedSecret"/> on dispose.
/// Concurrent use of <see cref="Client"/> follows that client's thread-safety rules.
/// Dispose is idempotent via an interlocked flag.
/// </remarks>
internal sealed class RadiusServerClientHandle : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// UTF-8 encoding of the configured shared secret; zeroed on dispose.
    /// </summary>
    private readonly byte[] _sharedSecret;

    /// <summary>
    /// 0 until disposed; set to 1 by the first dispose path.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Creates a handle for a configured RADIUS server role.
    /// </summary>
    /// <param name="role">Logical role name (for example <c>primary</c> / <c>secondary</c>).</param>
    /// <param name="options">Server address, port, and shared secret from configuration.</param>
    /// <param name="client">UDP client already bound to the resolved endpoint; owned by this handle.</param>
    /// <exception cref="ArgumentException"><paramref name="role"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/> or <paramref name="client"/> is <see langword="null"/>.
    /// </exception>
    public RadiusServerClientHandle(
        string role,
        RadiusServerOptions options,
        InternalRadiusClient client)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(options);
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Role = role;
        Address = options.Address.Trim();
        Port = options.Port;
        _sharedSecret = Encoding.UTF8.GetBytes(options.SharedSecret);
        EndPoint = client.RemoteEndPoint;
    }

    /// <summary>
    /// Logical role label for logging and diagnostics.
    /// </summary>
    public string Role { get; }

    /// <summary>
    /// Configured server address string (hostname or IP literal), trimmed.
    /// </summary>
    public string Address { get; }

    /// <summary>
    /// Configured UDP port.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Resolved remote endpoint used by <see cref="Client"/>.
    /// </summary>
    public IPEndPoint EndPoint { get; }

    /// <summary>
    /// Owned RADIUS UDP client for this server.
    /// </summary>
    public InternalRadiusClient Client { get; }

    /// <summary>
    /// Shared-secret bytes for request encoding and response authentication.
    /// </summary>
    /// <remarks>
    /// Do not log or persist this memory. Cleared when the handle is disposed.
    /// </remarks>
    public ReadOnlyMemory<byte> SharedSecret => _sharedSecret;

    /// <summary>
    /// Synchronously disposes by waiting on <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Disposes the client and zeroes shared-secret memory; subsequent calls are no-ops.
    /// </summary>
    /// <returns>A completed value task when disposal finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Client.DisposeAsync().ConfigureAwait(false);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_sharedSecret);
    }
}
