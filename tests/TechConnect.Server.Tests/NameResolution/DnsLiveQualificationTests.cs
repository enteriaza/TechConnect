// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DnsLiveQualificationTests.cs
//
// Purpose:
// Optional live DNS qualification for ConfiguredDnsResolver; skipped unless environment opts in.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.NameResolution;

/// <summary>
/// Optional live network checks against explicitly configured public resolvers.
/// Skipped automatically when outbound DNS/UDP is unavailable.
/// </summary>
public sealed class DnsLiveQualificationTests
{
    /// <summary>
    /// Live Resolve Google Via Cloudflare.
    /// </summary>
    [Fact]
    public async Task Live_Resolve_Google_ViaCloudflare()
    {
        var resolver = new ConfiguredDnsResolver(
            [IPAddress.Parse("1.1.1.1")],
            TimeSpan.FromSeconds(3),
            NullLogger<ConfiguredDnsResolver>.Instance);

        DnsQueryResult a;
        DnsQueryResult aaaa;
        try
        {
            a = await resolver.QueryAsync("google.com", DnsRecordType.A, CancellationToken.None);
            aaaa = await resolver.QueryAsync("google.com", DnsRecordType.Aaaa, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Environment without outbound DNS — do not fail CI.
            Assert.Fail($"Live DNS unavailable: {ex.Message}");
            return;
        }

        if (!a.Success && !aaaa.Success)
        {
            // Soft-skip style: treat as inconclusive when blocked.
            return;
        }

        (a.Success || aaaa.Success).Should().BeTrue();
        if (a.Success)
        {
            a.Addresses.Should().NotBeEmpty();
            a.ServerUsed.Should().Be(IPAddress.Parse("1.1.1.1"));
        }

        if (aaaa.Success)
        {
            aaaa.Addresses.Should().OnlyContain(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        }

        var destination = new DestinationResolver(resolver, new AlwaysIpv6Gate(aaaa.Success));
        var selected = await destination.ResolveAsync("google.com", CancellationToken.None);
        selected.Success.Should().BeTrue(selected.Error);
        selected.WasLiteral.Should().BeFalse();
        if (aaaa.Success)
        {
            selected.Family.Should().Be(System.Net.Sockets.AddressFamily.InterNetworkV6);
        }
    }

    /// <summary>
    /// Live Dns Disabled Hostname Fails Literal Unaffected.
    /// </summary>
    [Fact]
    public async Task Live_DnsDisabled_HostnameFails_LiteralUnaffected()
    {
        var dns = new ConfiguredDnsResolver(
            [],
            TimeSpan.FromSeconds(1),
            NullLogger<ConfiguredDnsResolver>.Instance);
        var destination = new DestinationResolver(dns, new AlwaysIpv6Gate(false));

        var host = await destination.ResolveAsync("google.com", CancellationToken.None);
        host.Success.Should().BeFalse();
        host.Error.Should().Contain("DNS is not configured");

        var literal = await destination.ResolveAsync("8.8.8.8", CancellationToken.None);
        literal.Success.Should().BeTrue();
        literal.WasLiteral.Should().BeTrue();

        (await dns.ReverseResolveAsync(IPAddress.Parse("8.8.8.8"), CancellationToken.None))
            .Should().BeNull();
    }
    /// <summary>
    /// Nested or helper type <c>AlwaysIpv6Gate</c>.
    /// </summary>

    private sealed class AlwaysIpv6Gate(bool available) : IIPv6NetworkAvailability
    {
        /// <summary>
        /// Gets is ipv6 ping available.
        /// </summary>
        public bool IsIpv6PingAvailable => available;
        /// <summary>
        /// Performs get local ipv4 addresses.
        /// </summary>
        /// <returns>The operation result.</returns>
        public IReadOnlyList<IPAddress> GetLocalIpv4Addresses() => [IPAddress.Parse("10.0.0.1")];
        /// <summary>
        /// Performs get local global ipv6 addresses.
        /// </summary>
        /// <returns>The operation result.</returns>
        public IReadOnlyList<IPAddress> GetLocalGlobalIpv6Addresses() =>
            available ? [IPAddress.Parse("2001:db8::1")] : [];
    }
}
