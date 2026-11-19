// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TechConnectDnsConfigurationProvider.cs
//
// Purpose:
// Adapts IDnsResolver configured-server state into a DnsConfigurationSnapshot
// for NOC display (never OS DNS settings).
//
// -----------------------------------------------------------------------------

using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Inspection;

/// <summary>
/// Surfaces TechConnect-configured DNS settings from <see cref="IDnsResolver"/>.
/// </summary>
/// <remarks>
/// Does not read operating-system resolver configuration. Timeout is rounded to
/// whole seconds from <see cref="IDnsResolver.QueryTimeout"/>.
/// </remarks>
internal sealed class TechConnectDnsConfigurationProvider : IDnsConfigurationProvider
{
    /// <summary>Underlying configured DNS resolver.</summary>
    private readonly IDnsResolver _resolver;

    /// <summary>
    /// Initializes the provider over the process DNS resolver.
    /// </summary>
    /// <param name="resolver">TechConnect DNS resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is null.</exception>
    public TechConnectDnsConfigurationProvider(IDnsResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public DnsConfigurationSnapshot GetConfiguration() =>
        new()
        {
            Enabled = _resolver.IsEnabled,
            Servers = _resolver.ConfiguredServers,
            QueryTimeoutSeconds = (int)Math.Round(_resolver.QueryTimeout.TotalSeconds)
        };
}
