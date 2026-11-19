// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DnsConfigurationModels.cs
//
// Purpose:
// Defines the TechConnect DNS configuration snapshot and provider contract
// (configured servers only — never OS resolver settings).
//
// -----------------------------------------------------------------------------

using System.Net;

namespace TechConnect.Server.Inspection;

/// <summary>
/// TechConnect DNS configuration actually used by the process (never OS resolver settings).
/// </summary>
internal sealed class DnsConfigurationSnapshot
{
    /// <summary>Whether at least one TechConnect DNS server is configured.</summary>
    public bool Enabled { get; init; }

    /// <summary>Configured DNS server addresses in configuration order.</summary>
    public IReadOnlyList<IPAddress> Servers { get; init; } = [];

    /// <summary>Per-query timeout in whole seconds.</summary>
    public int QueryTimeoutSeconds { get; init; }
}

/// <summary>
/// Provides the TechConnect DNS configuration snapshot for NOC display.
/// </summary>
internal interface IDnsConfigurationProvider
{
    /// <summary>
    /// Returns the current TechConnect DNS configuration (not OS settings).
    /// </summary>
    /// <returns>Configuration snapshot.</returns>
    DnsConfigurationSnapshot GetConfiguration();
}
