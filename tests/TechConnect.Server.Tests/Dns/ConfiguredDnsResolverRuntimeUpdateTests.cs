// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfiguredDnsResolverRuntimeUpdateTests.cs
//
// Purpose:
// Verifies ConfiguredDnsResolver.ApplyOptions publishes complete immutable
// settings snapshots and that in-flight queries keep their snapshot.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Configuration;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Dns;

/// <summary>
/// Runtime DNS settings update tests.
/// </summary>
public sealed class ConfiguredDnsResolverRuntimeUpdateTests
{
    /// <summary>
    /// A query that starts with servers [silent, slow] and a 3 s timeout fails over to the
    /// second server and waits for its 1.5 s answer, even though the settings were replaced
    /// mid-query with a different server list and a 1 s timeout. Using the new timeout for the
    /// failover attempt (the defect fixed in QueryServerAsync) would time out instead.
    /// </summary>
    [Fact]
    public async Task InFlightQuery_KeepsServersAndTimeoutFromItsSnapshot()
    {
        var firstServerHit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var silent = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ =>
            {
                firstServerHit.TrySetResult();
                return new FakeDnsReply { Silent = true };
            });
        await using var slow = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            silent.Port,
            _ => new FakeDnsReply { Delay = TimeSpan.FromMilliseconds(1500), Addresses = [IPAddress.Parse("203.0.113.77")] });
        var resolver = new ConfiguredDnsResolver(
            [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")],
            TimeSpan.FromSeconds(3),
            NullLogger<ConfiguredDnsResolver>.Instance,
            silent.Port);

        var query = resolver.QueryAsync("snapshot.example", DnsRecordType.A, CancellationToken.None);
        await firstServerHit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        resolver.ApplyOptions(new DnsOptions { Servers = ["127.0.0.3"], QueryTimeoutSeconds = 1 });
        var result = await query;

        result.Success.Should().BeTrue(result.Error);
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
        result.Addresses.Should().Equal(IPAddress.Parse("203.0.113.77"));
        resolver.ConfiguredServers.Should().Equal(IPAddress.Parse("127.0.0.3"));
        resolver.QueryTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    /// <summary>Queries started after an update use the new snapshot.</summary>
    [Fact]
    public async Task QueryAfterUpdate_UsesNewServers()
    {
        await using var oldServer = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.1")] });
        await using var newServer = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            oldServer.Port,
            _ => new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.2")] });
        var resolver = new ConfiguredDnsResolver([IPAddress.Loopback], TimeSpan.FromSeconds(2), NullLogger<ConfiguredDnsResolver>.Instance, oldServer.Port);

        (await resolver.QueryAsync("a.example", DnsRecordType.A, CancellationToken.None)).ServerUsed.Should().Be(IPAddress.Loopback);
        resolver.ApplyOptions(new DnsOptions { Servers = ["127.0.0.2"], QueryTimeoutSeconds = 2 });
        var after = await resolver.QueryAsync("a.example", DnsRecordType.A, CancellationToken.None);

        after.Success.Should().BeTrue(after.Error);
        after.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
        after.Addresses.Should().Equal(IPAddress.Parse("198.51.100.2"));
    }

    /// <summary>A published server list is never mutated by a later update.</summary>
    [Fact]
    public void ApplyOptions_ReplacesSnapshot_WithoutMutatingPreviousList()
    {
        var resolver = new ConfiguredDnsResolver([IPAddress.Parse("192.0.2.1")], TimeSpan.FromSeconds(2), NullLogger<ConfiguredDnsResolver>.Instance);
        var before = resolver.ConfiguredServers;

        resolver.ApplyOptions(new DnsOptions { Servers = ["192.0.2.2", "2001:db8::2"], QueryTimeoutSeconds = 9 });

        before.Should().Equal(IPAddress.Parse("192.0.2.1"));
        resolver.ConfiguredServers.Should().Equal(IPAddress.Parse("192.0.2.2"), IPAddress.Parse("2001:db8::2"));
        resolver.QueryTimeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    /// <summary>Clearing the servers disables resolution without throwing.</summary>
    [Fact]
    public async Task ApplyOptions_EmptyServers_DisablesResolution()
    {
        var resolver = new ConfiguredDnsResolver([IPAddress.Loopback], TimeSpan.FromSeconds(2), NullLogger<ConfiguredDnsResolver>.Instance);

        resolver.ApplyOptions(new DnsOptions { Servers = [], QueryTimeoutSeconds = 3 });

        resolver.IsEnabled.Should().BeFalse();
        (await resolver.QueryAsync("x.example", DnsRecordType.A, CancellationToken.None)).Success.Should().BeFalse();
    }

    /// <summary>Concurrent queries during repeated updates always complete against one coherent snapshot.</summary>
    [Fact]
    public async Task ConcurrentQueriesAndUpdates_AllQueriesSucceed()
    {
        await using var a = FakeDnsServer.Start(IPAddress.Loopback, _ => new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.1")] });
        await using var b = FakeDnsServer.Start(IPAddress.Parse("127.0.0.2"), a.Port, _ => new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.2")] });
        var resolver = new ConfiguredDnsResolver([IPAddress.Loopback], TimeSpan.FromSeconds(2), NullLogger<ConfiguredDnsResolver>.Instance, a.Port);
        using var stop = new CancellationTokenSource();

        var updater = Task.Run(() =>
        {
            var toggle = false;
            while (!stop.IsCancellationRequested)
            {
                resolver.ApplyOptions(new DnsOptions { Servers = [toggle ? "127.0.0.1" : "127.0.0.2"], QueryTimeoutSeconds = 2 });
                toggle = !toggle;
            }
        });
        var queries = Enumerable.Range(0, 40)
            .Select(i => resolver.QueryAsync($"q{i}.example", DnsRecordType.A, CancellationToken.None))
            .ToList();
        var results = await Task.WhenAll(queries);
        await stop.CancelAsync();
        await updater;

        results.Should().OnlyContain(r => r.Success);
        results.Should().OnlyContain(r =>
            (r.ServerUsed!.Equals(IPAddress.Loopback) && r.Addresses.SequenceEqual(new[] { IPAddress.Parse("198.51.100.1") })) ||
            (r.ServerUsed.Equals(IPAddress.Parse("127.0.0.2")) && r.Addresses.SequenceEqual(new[] { IPAddress.Parse("198.51.100.2") })));
    }

    /// <summary>Caller cancellation still cancels an in-flight query after a runtime update.</summary>
    [Fact]
    public async Task Cancellation_AfterUpdate_StillCancels()
    {
        var hit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ =>
            {
                hit.TrySetResult();
                return new FakeDnsReply { Silent = true };
            });
        var resolver = new ConfiguredDnsResolver([IPAddress.Parse("192.0.2.1")], TimeSpan.FromSeconds(2), NullLogger<ConfiguredDnsResolver>.Instance, server.Port);
        resolver.ApplyOptions(new DnsOptions { Servers = ["127.0.0.1"], QueryTimeoutSeconds = 30 });
        using var cts = new CancellationTokenSource();

        var query = resolver.QueryAsync("cancel.example", DnsRecordType.A, cts.Token);
        await hit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cts.CancelAsync();
        var result = await query.WaitAsync(TimeSpan.FromSeconds(5));

        result.Success.Should().BeFalse();
    }
}
