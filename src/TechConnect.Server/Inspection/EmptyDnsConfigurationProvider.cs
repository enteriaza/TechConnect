// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    EmptyDnsConfigurationProvider.cs
//
// Purpose:
// Fallback IDnsConfigurationProvider that reports DNS disabled with an empty
// server list (tests / defaults when DI has not supplied real configuration).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Inspection;

/// <summary>Fallback when DI has not supplied TechConnect DNS configuration (tests / defaults).</summary>
internal sealed class EmptyDnsConfigurationProvider : IDnsConfigurationProvider
{
    /// <inheritdoc />
    /// <remarks>Always returns <see cref="DnsConfigurationSnapshot.Enabled"/> false, empty servers, 3s timeout.</remarks>
    public DnsConfigurationSnapshot GetConfiguration() =>
        new()
        {
            Enabled = false,
            Servers = [],
            QueryTimeoutSeconds = 3
        };
}
