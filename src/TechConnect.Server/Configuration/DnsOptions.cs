// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DnsOptions.cs
//
// Purpose:
// Defines explicit TechConnect DNS server list and per-query timeout options;
// never populated from the host OS resolver.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Configuration;

/// <summary>
/// Explicit TechConnect DNS configuration. Never populated from the host OS resolver.
/// </summary>
/// <remarks>
/// An empty <see cref="Servers"/> list disables DNS name resolution; IP literals
/// continue to work. Bound under <c>TechConnectServer:Dns</c>.
/// </remarks>
public sealed class DnsOptions
{
    /// <summary>
    /// Ordered list of DNS server IP addresses (IPv4 and/or IPv6).
    /// </summary>
    /// <value>Empty by default (DNS disabled).</value>
    public List<string> Servers { get; set; } = [];

    /// <summary>
    /// Per-query timeout against a single DNS server, in seconds.
    /// </summary>
    /// <value>Defaults to 3. Validated to 1–60 when options validation runs.</value>
    public int QueryTimeoutSeconds { get; set; } = 3;
}
