// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfiguredDnsResolverTests.cs
//
// Purpose:
// Verifies ConfiguredDnsResolver A/AAAA/PTR queries, failover, truncation TCP fallback, and disabled DNS.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Tests.Dns;

/// <summary>
/// Unit tests for TechConnect-configured DNS resolution (never OS DNS).
/// </summary>
public sealed class ConfiguredDnsResolverTests
{
    /// <summary>
    /// Query Async A And Aaaa Succeed.
    /// </summary>
    [Fact]
    public async Task QueryAsync_A_And_Aaaa_Succeed()
    {
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            q => q.Type switch
            {
                DnsRecordType.A => new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.10")] },
                DnsRecordType.Aaaa => new FakeDnsReply { Addresses = [IPAddress.Parse("2001:db8::10")] },
                _ => new FakeDnsReply { ResponseCode = DnsResponseCode.Refused }
            });

        var resolver = Create(server);
        var a = await resolver.QueryAsync("host.example", DnsRecordType.A, CancellationToken.None);
        a.Success.Should().BeTrue(a.Error);
        a.Addresses.Should().Equal(IPAddress.Parse("198.51.100.10"));
        a.ServerUsed.Should().Be(IPAddress.Loopback);

        var aaaa = await resolver.QueryAsync("host.example", DnsRecordType.Aaaa, CancellationToken.None);
        aaaa.Success.Should().BeTrue(aaaa.Error);
        aaaa.Addresses.Should().Equal(IPAddress.Parse("2001:db8::10"));
    }

    /// <summary>
    /// Resolve Async Orders Aaaa Then A Deterministically.
    /// </summary>
    [Fact]
    public async Task ResolveAsync_OrdersAaaaThenA_Deterministically()
    {
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            q => q.Type switch
            {
                DnsRecordType.A => new FakeDnsReply
                {
                    Addresses = [IPAddress.Parse("198.51.100.2"), IPAddress.Parse("198.51.100.1")]
                },
                DnsRecordType.Aaaa => new FakeDnsReply
                {
                    Addresses = [IPAddress.Parse("2001:db8::2"), IPAddress.Parse("2001:db8::1")]
                },
                _ => new FakeDnsReply { ResponseCode = DnsResponseCode.NxDomain }
            });

        var addresses = await Create(server).ResolveAsync("multi.example", CancellationToken.None);
        addresses.Should().Equal(
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("2001:db8::2"),
            IPAddress.Parse("198.51.100.1"),
            IPAddress.Parse("198.51.100.2"));
    }

    /// <summary>
    /// Reverse Resolve Async Ptr.
    /// </summary>
    [Fact]
    public async Task ReverseResolveAsync_Ptr()
    {
        var ptrName = DnsWireCodec.BuildPtrName(IPAddress.Parse("203.0.113.5"));
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            q =>
            {
                if (q.Type == DnsRecordType.Ptr && q.Name == ptrName)
                {
                    return new FakeDnsReply { PtrHostname = "ptr.example" };
                }

                return new FakeDnsReply { ResponseCode = DnsResponseCode.NxDomain };
            });

        var name = await Create(server).ReverseResolveAsync(IPAddress.Parse("203.0.113.5"), CancellationToken.None);
        name.Should().Be("ptr.example");
    }

    /// <summary>
    /// Query Async Nx Domain Does Not Failover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NxDomain_DoesNotFailover()
    {
        var primaryHits = 0;
        var secondaryHits = 0;
        await using var primary = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ =>
            {
                Interlocked.Increment(ref primaryHits);
                return new FakeDnsReply { ResponseCode = DnsResponseCode.NxDomain };
            });
        await using var secondary = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            primary.Port,
            _ =>
            {
                Interlocked.Increment(ref secondaryHits);
                return new FakeDnsReply { Addresses = [IPAddress.Parse("1.2.3.4")] };
            });

        var result = await CreateDual(primary, secondary)
            .QueryAsync("neg.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.NxDomain);
        primaryHits.Should().Be(1);
        secondaryHits.Should().Be(0);
    }

    /// <summary>
    /// Query Async No Data Does Not Failover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NoData_DoesNotFailover()
    {
        var primaryHits = 0;
        var secondaryHits = 0;
        await using var primary = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ =>
            {
                Interlocked.Increment(ref primaryHits);
                // NOERROR with empty answer section = NODATA
                return new FakeDnsReply { ResponseCode = DnsResponseCode.NoError, Addresses = [] };
            });
        await using var secondary = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            primary.Port,
            _ =>
            {
                Interlocked.Increment(ref secondaryHits);
                return new FakeDnsReply { Addresses = [IPAddress.Parse("1.2.3.4")] };
            });

        var result = await CreateDual(primary, secondary)
            .QueryAsync("nodata.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.NoError);
        result.Error.Should().Contain("No address");
        primaryHits.Should().Be(1);
        secondaryHits.Should().Be(0);
    }

    /// <summary>
    /// Query Async Server Errors Fail Over To Next Resolver.
    /// </summary>
    [Theory]
    [InlineData((int)DnsResponseCode.ServFail)]
    [InlineData((int)DnsResponseCode.Refused)]
    [InlineData((int)DnsResponseCode.FormErr)]
    public async Task QueryAsync_ServerErrors_FailOverToNextResolver(int codeValue)
    {
        var code = (DnsResponseCode)codeValue;
        var primaryHits = 0;
        var secondaryHits = 0;
        await using var primary = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ =>
            {
                Interlocked.Increment(ref primaryHits);
                return new FakeDnsReply { ResponseCode = code };
            });
        await using var secondary = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            primary.Port,
            _ =>
            {
                Interlocked.Increment(ref secondaryHits);
                return new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.77")] };
            });

        var result = await CreateDual(primary, secondary)
            .QueryAsync("failover.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.Addresses.Should().Equal(IPAddress.Parse("198.51.100.77"));
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
        primaryHits.Should().Be(1);
        secondaryHits.Should().Be(1);
    }

    /// <summary>
    /// Query Async Udp Truncate Tcp Fails Then Failsover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_UdpTruncate_TcpFails_ThenFailsover()
    {
        var secondaryHits = 0;
        await using var primary = FakeDnsServer.Start(
            IPAddress.Loopback,
            q =>
            {
                if (q.ViaTcp)
                {
                    return new FakeDnsReply { DropTcp = true };
                }

                return new FakeDnsReply
                {
                    TruncateUdp = true,
                    Addresses = [IPAddress.Parse("198.51.100.50")]
                };
            });
        await using var secondary = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            primary.Port,
            _ =>
            {
                Interlocked.Increment(ref secondaryHits);
                return new FakeDnsReply { Addresses = [IPAddress.Parse("203.0.113.50")] };
            });

        var result = await CreateDual(primary, secondary, TimeSpan.FromMilliseconds(400))
            .QueryAsync("tcpfail.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.Addresses.Should().Equal(IPAddress.Parse("203.0.113.50"));
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
        secondaryHits.Should().Be(1);
    }

    /// <summary>
    /// Query Async All Resolvers Server Error Reports Failure.
    /// </summary>
    [Fact]
    public async Task QueryAsync_AllResolversServerError_ReportsFailure()
    {
        await using var primary = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { ResponseCode = DnsResponseCode.ServFail });
        await using var secondary = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            primary.Port,
            _ => new FakeDnsReply { ResponseCode = DnsResponseCode.Refused });

        var result = await CreateDual(primary, secondary)
            .QueryAsync("allfail.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.Refused);
        result.Error.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Should Failover Matches Expected Semantics.
    /// </summary>
    [Fact]
    public async Task ShouldFailover_MatchesExpectedSemantics()
    {
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.ServFail).Should().BeTrue();
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.Refused).Should().BeTrue();
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.FormErr).Should().BeTrue();
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.Timeout).Should().BeTrue();
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.NxDomain).Should().BeFalse();
        ConfiguredDnsResolver.ShouldFailover(DnsResponseCode.NoError).Should().BeFalse();
    }

    /// <summary>
    /// Query Async Udp Truncate Falls Back To Tcp.
    /// </summary>
    [Fact]
    public async Task QueryAsync_UdpTruncate_FallsBackToTcp()
    {
        var sawTcp = false;
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            q =>
            {
                if (q.ViaTcp)
                {
                    sawTcp = true;
                    return new FakeDnsReply { Addresses = [IPAddress.Parse("198.51.100.50")] };
                }

                return new FakeDnsReply
                {
                    TruncateUdp = true,
                    Addresses = [IPAddress.Parse("198.51.100.50")]
                };
            });

        var result = await Create(server).QueryAsync("trunc.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.Addresses.Should().Equal(IPAddress.Parse("198.51.100.50"));
        sawTcp.Should().BeTrue();
    }

    /// <summary>
    /// Query Async Wrong Txid Is Malformed Then Failsover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WrongTxid_IsMalformed_ThenFailsover()
    {
        await using var bad = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { WrongTransactionId = true });
        await using var good = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            bad.Port,
            _ => new FakeDnsReply { Addresses = [IPAddress.Parse("203.0.113.9")] });

        var resolver = new ConfiguredDnsResolver(
            [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")],
            TimeSpan.FromSeconds(2),
            NullLogger<ConfiguredDnsResolver>.Instance,
            bad.Port);

        var result = await resolver.QueryAsync("failover.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.Addresses.Should().Equal(IPAddress.Parse("203.0.113.9"));
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
    }

    /// <summary>
    /// Query Async Malformed Garbage Then Failsover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_MalformedGarbage_ThenFailsover()
    {
        await using var bad = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { MalformedGarbage = true });
        await using var good = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            bad.Port,
            _ => new FakeDnsReply { Addresses = [IPAddress.Parse("203.0.113.10")] });

        var resolver = new ConfiguredDnsResolver(
            [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")],
            TimeSpan.FromSeconds(2),
            NullLogger<ConfiguredDnsResolver>.Instance,
            bad.Port);

        var result = await resolver.QueryAsync("garbage.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
    }

    /// <summary>
    /// Query Async Timeout Then Failsover.
    /// </summary>
    [Fact]
    public async Task QueryAsync_Timeout_ThenFailsover()
    {
        await using var silent = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { Silent = true });
        await using var good = FakeDnsServer.Start(
            IPAddress.Parse("127.0.0.2"),
            silent.Port,
            _ => new FakeDnsReply { Addresses = [IPAddress.Parse("203.0.113.11")] });

        var resolver = new ConfiguredDnsResolver(
            [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")],
            TimeSpan.FromMilliseconds(200),
            NullLogger<ConfiguredDnsResolver>.Instance,
            silent.Port);

        var result = await resolver.QueryAsync("slow.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeTrue(result.Error);
        result.ServerUsed.Should().Be(IPAddress.Parse("127.0.0.2"));
    }

    /// <summary>
    /// Query Async Cancel Returns Cancelled.
    /// </summary>
    [Fact]
    public async Task QueryAsync_Cancel_ReturnsCancelled()
    {
        await using var server = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { Delay = TimeSpan.FromSeconds(5), Addresses = [IPAddress.Parse("1.1.1.1")] });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        // Give the server a moment, then cancel aggressively.
        await Task.Delay(10);
        await cts.CancelAsync();

        var result = await Create(server, TimeSpan.FromSeconds(5))
            .QueryAsync("cancel.example", DnsRecordType.A, cts.Token);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.Cancelled);
    }

    /// <summary>
    /// Query Async No Servers Disabled.
    /// </summary>
    [Fact]
    public async Task QueryAsync_NoServers_Disabled()
    {
        var resolver = new ConfiguredDnsResolver(
            [],
            TimeSpan.FromSeconds(1),
            NullLogger<ConfiguredDnsResolver>.Instance);
        resolver.IsEnabled.Should().BeFalse();
        var result = await resolver.QueryAsync("x.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.Disabled);
        result.Error.Should().Contain("Dns:Servers");
    }

    /// <summary>
    /// Query Async All Servers Timeout Reports Timeout.
    /// </summary>
    [Fact]
    public async Task QueryAsync_AllServersTimeout_ReportsTimeout()
    {
        await using var silent = FakeDnsServer.Start(
            IPAddress.Loopback,
            _ => new FakeDnsReply { Silent = true });

        var resolver = Create(silent, TimeSpan.FromMilliseconds(150));
        var result = await resolver.QueryAsync("dead.example", DnsRecordType.A, CancellationToken.None);
        result.Success.Should().BeFalse();
        result.ResponseCode.Should().Be(DnsResponseCode.Timeout);
    }
    /// <summary>
    /// Performs create.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>The operation result.</returns>

    private static ConfiguredDnsResolver Create(FakeDnsServer server, TimeSpan? timeout = null) =>
        new(
            [server.Address],
            timeout ?? TimeSpan.FromSeconds(2),
            NullLogger<ConfiguredDnsResolver>.Instance,
            server.Port);
    /// <summary>
    /// Performs create dual.
    /// </summary>
    /// <param name="primary">The primary.</param>
    /// <param name="secondary">The secondary.</param>
    /// <param name="timeout">The timeout.</param>
    /// <returns>The operation result.</returns>

    private static ConfiguredDnsResolver CreateDual(
        FakeDnsServer primary,
        FakeDnsServer secondary,
        TimeSpan? timeout = null) =>
        new(
            [primary.Address, secondary.Address],
            timeout ?? TimeSpan.FromSeconds(2),
            NullLogger<ConfiguredDnsResolver>.Instance,
            primary.Port);
}
