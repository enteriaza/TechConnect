// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DestinationResolverTests.cs
//
// Purpose:
// Verifies DestinationResolver literal/hostname selection, IPv6 gating, and DNS-disabled failures.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Regression tests for destination resolution used by ping and traceroute.
/// </summary>
public sealed class DestinationResolverTests
{
    /// <summary>
    /// Resolve Ipv4Literal.
    /// </summary>
    [Fact]
    public async Task Resolve_Ipv4Literal()
    {
        var resolver = new DestinationResolver(new FakeDns(), new FakeIpv6(available: false));
        var result = await resolver.ResolveAsync("8.8.8.8", CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.WasLiteral.Should().BeTrue();
        result.Address.Should().Be(IPAddress.Parse("8.8.8.8"));
        result.Family.Should().Be(AddressFamily.InterNetwork);
    }

    /// <summary>
    /// Resolve Ipv6Literal When Available.
    /// </summary>
    [Fact]
    public async Task Resolve_Ipv6Literal_WhenAvailable()
    {
        var resolver = new DestinationResolver(new FakeDns(), new FakeIpv6(available: true));
        var result = await resolver.ResolveAsync("2001:db8::1", CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.WasLiteral.Should().BeTrue();
        result.Address.Should().Be(IPAddress.Parse("2001:db8::1"));
        result.Family.Should().Be(AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// Resolve Ipv6Literal When Unavailable Fails.
    /// </summary>
    [Fact]
    public async Task Resolve_Ipv6Literal_WhenUnavailable_Fails()
    {
        var resolver = new DestinationResolver(new FakeDns(), new FakeIpv6(available: false));
        var result = await resolver.ResolveAsync("2001:db8::1", CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("IPv6");
    }

    /// <summary>
    /// Resolve Hostname Prefers Ipv6 When Available.
    /// </summary>
    [Fact]
    public async Task Resolve_Hostname_PrefersIpv6_WhenAvailable()
    {
        var dns = new FakeDns
        {
            Enabled = true,
            Addresses =
            [
                IPAddress.Parse("198.51.100.1"),
                IPAddress.Parse("2001:db8::2"),
                IPAddress.Parse("2001:db8::1")
            ]
        };
        var resolver = new DestinationResolver(dns, new FakeIpv6(available: true));
        var result = await resolver.ResolveAsync("host.example", CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.WasLiteral.Should().BeFalse();
        result.Address.Should().Be(IPAddress.Parse("2001:db8::1"));
        result.AllAddresses.Should().HaveCount(3);
    }

    /// <summary>
    /// Resolve Hostname Falls Back To Ipv4 When Ipv6Unavailable.
    /// </summary>
    [Fact]
    public async Task Resolve_Hostname_FallsBackToIpv4_WhenIpv6Unavailable()
    {
        var dns = new FakeDns
        {
            Enabled = true,
            Addresses =
            [
                IPAddress.Parse("2001:db8::1"),
                IPAddress.Parse("198.51.100.7")
            ]
        };
        var resolver = new DestinationResolver(dns, new FakeIpv6(available: false));
        var result = await resolver.ResolveAsync("host.example", CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.Address.Should().Be(IPAddress.Parse("198.51.100.7"));
        result.Family.Should().Be(AddressFamily.InterNetwork);
    }

    /// <summary>
    /// Resolve Hostname Only Aaaa When Ipv6Unavailable Fails.
    /// </summary>
    [Fact]
    public async Task Resolve_Hostname_OnlyAaaa_WhenIpv6Unavailable_Fails()
    {
        var dns = new FakeDns
        {
            Enabled = true,
            Addresses = [IPAddress.Parse("2001:db8::1")]
        };
        var resolver = new DestinationResolver(dns, new FakeIpv6(available: false));
        var result = await resolver.ResolveAsync("v6only.example", CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("usable address family");
    }

    /// <summary>
    /// Resolve Hostname Dns Disabled Fails.
    /// </summary>
    [Fact]
    public async Task Resolve_Hostname_DnsDisabled_Fails()
    {
        var resolver = new DestinationResolver(new FakeDns { Enabled = false }, new FakeIpv6(available: true));
        var result = await resolver.ResolveAsync("host.example", CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("DNS is not configured");
    }

    /// <summary>
    /// Resolve Hostname Empty Answers Fails.
    /// </summary>
    [Fact]
    public async Task Resolve_Hostname_EmptyAnswers_Fails()
    {
        var dns = new FakeDns { Enabled = true, Addresses = [] };
        var resolver = new DestinationResolver(dns, new FakeIpv6(available: true));
        var result = await resolver.ResolveAsync("empty.example", CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("did not resolve");
    }

    /// <summary>
    /// Resolve Blank Fails.
    /// </summary>
    [Fact]
    public async Task Resolve_Blank_Fails()
    {
        var resolver = new DestinationResolver(new FakeDns(), new FakeIpv6(available: true));
        var result = await resolver.ResolveAsync("   ", CancellationToken.None);
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Invalid destination");
    }

    /// <summary>
    /// Select Address Prefers Lowest Ipv6String Then Ipv4.
    /// </summary>
    [Fact]
    public void SelectAddress_PrefersLowestIpv6String_ThenIpv4()
    {
        var addresses = new[]
        {
            IPAddress.Parse("10.0.0.2"),
            IPAddress.Parse("2001:db8::b"),
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("2001:db8::a")
        };

        DestinationResolver.SelectAddress(addresses, ipv6Available: true)
            .Should().Be(IPAddress.Parse("2001:db8::a"));
        DestinationResolver.SelectAddress(addresses, ipv6Available: false)
            .Should().Be(IPAddress.Parse("10.0.0.1"));
        DestinationResolver.SelectAddress([IPAddress.Parse("2001:db8::1")], ipv6Available: false)
            .Should().BeNull();
    }

    /// <summary>
    /// Minimal <see cref="IDnsResolver"/> that returns a fixed address list from <see cref="ResolveAsync"/>.
    /// </summary>
    private sealed class FakeDns : IDnsResolver
    {
        /// <summary>When false, <see cref="IsEnabled"/> is false and configured servers are empty.</summary>
        public bool Enabled { get; set; }

        /// <summary>Addresses returned by <see cref="ResolveAsync"/>.</summary>
        public IReadOnlyList<IPAddress> Addresses { get; set; } = [];

        /// <inheritdoc />
        public bool IsEnabled => Enabled;

        /// <inheritdoc />
        public IReadOnlyList<IPAddress> ConfiguredServers => Enabled ? [IPAddress.Loopback] : [];

        /// <inheritdoc />
        public TimeSpan QueryTimeout => TimeSpan.FromSeconds(1);

        /// <inheritdoc />
        /// <exception cref="NotSupportedException">Always thrown; these tests use <see cref="ResolveAsync"/> only.</exception>
        public Task<DnsQueryResult> QueryAsync(string name, DnsRecordType type, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        /// <inheritdoc />
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string hostname, CancellationToken cancellationToken) =>
            Task.FromResult(Addresses);

        /// <inheritdoc />
        public Task<string?> ReverseResolveAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Stub <see cref="IIPv6NetworkAvailability"/> with a fixed availability flag.
    /// </summary>
    /// <param name="available">Value exposed as <see cref="IsIpv6PingAvailable"/>.</param>
    private sealed class FakeIpv6(bool available) : IIPv6NetworkAvailability
    {
        /// <inheritdoc />
        public bool IsIpv6PingAvailable => available;

        /// <inheritdoc />
        public IReadOnlyList<IPAddress> GetLocalIpv4Addresses() => [IPAddress.Parse("10.0.0.5")];

        /// <inheritdoc />
        public IReadOnlyList<IPAddress> GetLocalGlobalIpv6Addresses() =>
            available ? [IPAddress.Parse("2001:db8::5")] : [];
    }
}
