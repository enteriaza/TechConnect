// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusClientFactory.cs
//
// Purpose:
// Creates and owns per-server InternalRadiusClient instances for the application
// lifetime from RADIUS authentication configuration.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Authentication.Radius.Transport;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// Creates and owns per-server <see cref="InternalRadiusClient"/> instances for the application lifetime.
/// </summary>
/// <remarks>
/// When RADIUS is disabled, no clients are created and <see cref="Primary"/> /
/// <see cref="Secondary"/> remain <see langword="null"/>. Endpoint resolution runs
/// synchronously at construction; prefer IP literals in configuration so startup
/// does not depend on DNS. Dispose is idempotent.
/// </remarks>
internal sealed class RadiusClientFactory : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Logger factory used to create per-role RADIUS client loggers.
    /// </summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Time provider forwarded to each <see cref="InternalRadiusClient"/>.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Owned handles disposed with this factory.
    /// </summary>
    private readonly List<RadiusServerClientHandle> _handles = [];

    /// <summary>
    /// 0 until disposed; set to 1 by the first dispose path.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Builds primary (and optional secondary) clients when RADIUS is enabled.
    /// </summary>
    /// <param name="options">Server options containing RADIUS settings.</param>
    /// <param name="loggerFactory">Factory for per-role client loggers.</param>
    /// <param name="timeProvider">Clock used by RADIUS clients for timeouts/retries.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="options"/>, <paramref name="loggerFactory"/>, or
    /// <paramref name="timeProvider"/> is <see langword="null"/>.
    /// </exception>
    public RadiusClientFactory(
        IOptions<TechConnectServerOptions> options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var radius = options.Value.Authentication?.Radius ?? new RadiusAuthenticationOptions();
        Enabled = radius.Enabled;
        if (!radius.Enabled)
        {
            return;
        }

        Primary = CreateHandle("primary", radius.PrimaryServer);
        if (IsConfigured(radius.SecondaryServer))
        {
            Secondary = CreateHandle("secondary", radius.SecondaryServer!);
        }
    }

    /// <summary>
    /// Whether RADIUS authentication is enabled in configuration.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Handle for the primary RADIUS server when enabled; otherwise <see langword="null"/>.
    /// </summary>
    public RadiusServerClientHandle? Primary { get; }

    /// <summary>
    /// Handle for the secondary RADIUS server when configured and enabled; otherwise <see langword="null"/>.
    /// </summary>
    public RadiusServerClientHandle? Secondary { get; }

    /// <summary>
    /// Resolves the server endpoint and creates an owned client handle.
    /// </summary>
    /// <param name="role">Role label for logging.</param>
    /// <param name="server">Server options including address, port, and shared secret.</param>
    /// <returns>Owned handle registered for disposal.</returns>
    private RadiusServerClientHandle CreateHandle(string role, RadiusServerOptions server)
    {
        // Resolve synchronously at startup — addresses are validated as IP literals or hostnames
        // that must resolve for the listener to be useful. Prefer IP literals in configuration.
        var endpoint = RadiusEndpointResolver
            .ResolveAsync(server.Address, server.Port, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var client = new InternalRadiusClient(
            endpoint,
            _loggerFactory.CreateLogger($"TechConnect.Server.Authentication.Radius.{role}"),
            _timeProvider);

        var handle = new RadiusServerClientHandle(role, server, client);
        _handles.Add(handle);
        return handle;
    }

    /// <summary>
    /// Returns whether a secondary server entry has a usable address.
    /// </summary>
    /// <param name="server">Optional secondary server options.</param>
    /// <returns><see langword="true"/> when address is non-whitespace.</returns>
    private static bool IsConfigured(RadiusServerOptions? server) =>
        server is not null && !string.IsNullOrWhiteSpace(server.Address);

    /// <summary>
    /// Synchronously disposes by waiting on <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Disposes all owned server handles; subsequent calls are no-ops.
    /// </summary>
    /// <returns>A completed value task when disposal finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var handle in _handles)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
        }

        _handles.Clear();
    }
}
