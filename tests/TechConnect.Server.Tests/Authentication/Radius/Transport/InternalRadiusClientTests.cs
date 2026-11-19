// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    InternalRadiusClientTests.cs
//
// Purpose:
// Regression tests and helpers covering InternalRadiusClient behaviour.
// Fixture: InternalRadiusClientTests.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Authentication.Radius.Transport;

namespace TechConnect.Server.Tests.Authentication.Radius.Transport;
/// <summary>
/// xUnit test fixture for InternalRadiusClient.
/// </summary>

public sealed class InternalRadiusClientTests
{
    /// <summary>
    /// Secret test helper member.
    /// </summary>
    private static readonly byte[] Secret = RadiusProtocolReferenceVectors.SharedSecret;
    /// <summary>
    /// Verifies Exchange Access Accept IPv4.
    /// </summary>

    [Fact]
    public async Task Exchange_AccessAccept_IPv4()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            req.Valid.Should().BeTrue();
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret,
                RadiusAttribute.Create(RadiusAttributeType.FilterId, "techconnect:Operator"));
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client);

        result.Outcome.Should().Be(RadiusExchangeOutcome.Accept);
        result.Packet.Should().NotBeNull();
        result.Packet!.TryGetAttribute(RadiusAttributeType.FilterId, out var filter).Should().BeTrue();
        filter!.GetStringValue().Should().Be("techconnect:Operator");
    }
    /// <summary>
    /// Verifies Exchange Access Reject.
    /// </summary>

    [Fact]
    public async Task Exchange_AccessReject()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessReject,
                req.Identifier,
                req.Authenticator,
                Secret);
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client);
        result.Outcome.Should().Be(RadiusExchangeOutcome.Reject);
        result.Packet.Should().NotBeNull();
    }
    /// <summary>
    /// Verifies Exchange Timeout Is Unavailable Not Invalid.
    /// </summary>

    [Fact]
    public async Task Exchange_Timeout_IsUnavailable_NotInvalid()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, _ => null);
        await using var client = new InternalRadiusClient(server.EndPoint);

        var result = await SendAccessRequestAsync(client, timeout: TimeSpan.FromMilliseconds(200));
        result.Outcome.Should().Be(RadiusExchangeOutcome.Unavailable);
        result.Detail.Should().Contain("timed out");
    }
    /// <summary>
    /// Verifies Exchange Caller Cancellation Is Cancelled.
    /// </summary>

    [Fact]
    public async Task Exchange_CallerCancellation_IsCancelled()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, asyncRequest =>
        {
            Thread.Sleep(500);
            var req = new RadiusPacket(asyncRequest);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret);
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(5), cts.Token);
        result.Outcome.Should().Be(RadiusExchangeOutcome.Cancelled);
    }
    /// <summary>
    /// Verifies Exchange Cancel Before Send.
    /// </summary>

    [Fact]
    public async Task Exchange_CancelBeforeSend()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, _ => null);
        await using var client = new InternalRadiusClient(server.EndPoint);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await SendAccessRequestAsync(client, cancellationToken: cts.Token);
        result.Outcome.Should().Be(RadiusExchangeOutcome.Cancelled);
    }
    /// <summary>
    /// Verifies Exchange Invalid Response Authenticator Is Invalid Response.
    /// </summary>

    [Fact]
    public async Task Exchange_InvalidResponseAuthenticator_IsInvalidResponse()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            var response = LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret);
            response[4] ^= 0xFF;
            return response;
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client);
        result.Outcome.Should().Be(RadiusExchangeOutcome.InvalidResponse);
        result.Detail.Should().Contain("Authenticator");
    }
    /// <summary>
    /// Verifies Exchange Malformed Packet Is Invalid Response Not Unavailable.
    /// </summary>

    [Fact]
    public async Task Exchange_MalformedPacket_IsInvalidResponse_NotUnavailable()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            // Preserve identifier so the client can associate the datagram.
            return [request[0], request[1], 0, 20, ..new byte[10]];
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(2));
        result.Outcome.Should().Be(RadiusExchangeOutcome.InvalidResponse);
    }
    /// <summary>
    /// Verifies Exchange Unexpected Code Is Invalid Response.
    /// </summary>

    [Fact]
    public async Task Exchange_UnexpectedCode_IsInvalidResponse()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            // Accounting-Response (5) with valid response authenticator.
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccountingResponse,
                req.Identifier,
                req.Authenticator,
                Secret);
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client);
        result.Outcome.Should().Be(RadiusExchangeOutcome.InvalidResponse);
        result.Detail.Should().Contain("Unexpected");
    }
    /// <summary>
    /// Verifies Concurrent Exchanges Correlate Correctly Out Of Order.
    /// </summary>

    [Fact]
    public async Task ConcurrentExchanges_CorrelateCorrectly_OutOfOrder()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new ConcurrentDictionary<byte, byte[]>();

        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            seen[req.Identifier] = req.Authenticator;
            // Delay lower identifiers so higher ones complete first.
            if (req.Identifier < 2)
            {
                gate.Task.GetAwaiter().GetResult();
            }

            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret,
                RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, req.Identifier.ToString()));
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(5)))
            .ToArray();

        // Allow some requests to arrive, then release the delayed ones.
        await Task.Delay(100);
        gate.TrySetResult();

        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r.Outcome == RadiusExchangeOutcome.Accept);
        results.Select(r => r.Packet!.TryGetAttribute(RadiusAttributeType.ReplyMessage, out var m)
                ? m!.GetStringValue()
                : null)
            .Should().OnlyHaveUniqueItems();
    }
    /// <summary>
    /// Verifies Late Response After Timeout Does Not Complete Later Request.
    /// </summary>

    [Fact]
    public async Task LateResponse_AfterTimeout_DoesNotCompleteLaterRequest()
    {
        var releaseLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte? firstId = null;

        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            if (firstId is null)
            {
                firstId = req.Identifier;
                releaseLate.Task.GetAwaiter().GetResult();
                return LoopbackRadiusServer.BuildAccessResponse(
                    RadiusPacketCode.AccessAccept,
                    req.Identifier,
                    req.Authenticator,
                    Secret,
                    RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, "late"));
            }

            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret,
                RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, "second"));
        });

        await using var client = new InternalRadiusClient(server.EndPoint);

        var first = SendAccessRequestAsync(client, timeout: TimeSpan.FromMilliseconds(150));
        var firstResult = await first;
        firstResult.Outcome.Should().Be(RadiusExchangeOutcome.Unavailable);

        // Quarantine holds the first identifier; second request gets a different id.
        var secondTask = SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(2));
        releaseLate.TrySetResult();
        var second = await secondTask;

        second.Outcome.Should().Be(RadiusExchangeOutcome.Accept);
        second.Packet!.TryGetAttribute(RadiusAttributeType.ReplyMessage, out var msg).Should().BeTrue();
        msg!.GetStringValue().Should().Be("second");
    }
    /// <summary>
    /// Verifies Separate Clients Do Not Cross Deliver.
    /// </summary>

    [Fact]
    public async Task SeparateClients_DoNotCrossDeliver()
    {
        await using var serverA = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret,
                RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, "A"));
        });
        await using var serverB = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessReject,
                req.Identifier,
                req.Authenticator,
                Secret,
                RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, "B"));
        });

        await using var clientA = new InternalRadiusClient(serverA.EndPoint);
        await using var clientB = new InternalRadiusClient(serverB.EndPoint);

        var a = await SendAccessRequestAsync(clientA);
        var b = await SendAccessRequestAsync(clientB);

        a.Outcome.Should().Be(RadiusExchangeOutcome.Accept);
        b.Outcome.Should().Be(RadiusExchangeOutcome.Reject);
    }
    /// <summary>
    /// Verifies Identifier Exhaustion Throws.
    /// </summary>

    [Fact]
    public async Task IdentifierExhaustion_Throws()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, _ => null);
        await using var client = new InternalRadiusClient(server.EndPoint);

        var pending = new List<Task<RadiusExchangeResult>>();
        for (var i = 0; i < RadiusIdentifierAllocator.Capacity; i++)
        {
            pending.Add(SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(5)));
        }

        var act = async () => await SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*exhausted*");

        // Cancel by disposing — completes outstanding as Cancelled.
        await client.DisposeAsync();
        var results = await Task.WhenAll(pending);
        results.Should().OnlyContain(r =>
            r.Outcome == RadiusExchangeOutcome.Cancelled ||
            r.Outcome == RadiusExchangeOutcome.Unavailable);
    }
    /// <summary>
    /// Verifies Dispose Completes Pending As Cancelled.
    /// </summary>

    [Fact]
    public async Task Dispose_CompletesPending_AsCancelled()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, _ => null);
        var client = new InternalRadiusClient(server.EndPoint);
        var pending = SendAccessRequestAsync(client, timeout: TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        await client.DisposeAsync();
        var result = await pending;
        result.Outcome.Should().Be(RadiusExchangeOutcome.Cancelled);
    }
    /// <summary>
    /// Verifies Dns Failure Throws Transport Exception.
    /// </summary>

    [Fact]
    public async Task DnsFailure_ThrowsTransportException()
    {
        var act = async () => await InternalRadiusClient.CreateAsync(
            "this.hostname.should.not.resolve.techconnect.test",
            1812);
        await act.Should().ThrowAsync<RadiusTransportException>();
    }
    /// <summary>
    /// Verifies Create Async Loopback Uses Configurable Port.
    /// </summary>

    [Fact]
    public async Task CreateAsync_Loopback_UsesConfigurablePort()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                req.Identifier,
                req.Authenticator,
                Secret);
        });

        await using var client = await InternalRadiusClient.CreateAsync(
            "127.0.0.1",
            server.EndPoint.Port);
        var result = await SendAccessRequestAsync(client);
        result.Outcome.Should().Be(RadiusExchangeOutcome.Accept);
    }
    /// <summary>
    /// Verifies Invalid Message Authenticator Is Invalid Response.
    /// </summary>

    [Fact]
    public async Task InvalidMessageAuthenticator_IsInvalidResponse()
    {
        await using var server = LoopbackRadiusServer.Start(Secret, request =>
        {
            var req = new RadiusPacket(request);
            var packet = new RadiusPacket(RadiusPacketCode.AccessAccept, req.Identifier);
            packet.AddMessageAuthenticatorPlaceholder();
            packet.SetAuthenticator(Secret, req.Authenticator);
            var bytes = packet.RawData.ToArray();
            // Corrupt MA value after signing.
            bytes[^1] ^= 0x5A;
            // Recompute response authenticator would be needed for a "valid RA + bad MA" case;
            // instead rebuild with valid RA then corrupt MA and fix RA for the corrupted packet.
            var requestAuth = req.Authenticator;
            var ra = RadiusAuthenticators.ComputeResponseAuthenticator(bytes, requestAuth, Secret);
            ra.CopyTo(bytes.AsSpan(4, 16));
            return bytes;
        });

        await using var client = new InternalRadiusClient(server.EndPoint);
        var result = await SendAccessRequestAsync(client);
        result.Outcome.Should().Be(RadiusExchangeOutcome.InvalidResponse);
        result.Detail.Should().Contain("Message-Authenticator");
    }
    /// <summary>
    /// Verifies IPv6 Loopback When Available.
    /// </summary>

    [Fact]
    public async Task IPv6Loopback_WhenAvailable()
    {
        try
        {
            await using var server = LoopbackRadiusServer.Start(
                Secret,
                request =>
                {
                    var req = new RadiusPacket(request);
                    return LoopbackRadiusServer.BuildAccessResponse(
                        RadiusPacketCode.AccessAccept,
                        req.Identifier,
                        req.Authenticator,
                        Secret);
                },
                AddressFamily.InterNetworkV6);

            await using var client = new InternalRadiusClient(server.EndPoint);
            var result = await SendAccessRequestAsync(client);
            result.Outcome.Should().Be(RadiusExchangeOutcome.Accept);
        }
        catch (SocketException)
        {
            // IPv6 loopback may be disabled on some Windows CI images.
            return;
        }
    }
    /// <summary>
    /// Verifies Send Access Request Async.
    /// </summary>

    private static async Task<RadiusExchangeResult> SendAccessRequestAsync(
        InternalRadiusClient client,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessRequest);
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, "nemo"));
        var requestAuth = RadiusAuthenticators.CreateAccessRequestAuthenticator();
        packet.SetAccessRequestAuthenticatorForTests(requestAuth);
        var encodedPassword = RadiusPapPassword.Encode("arctangent"u8, requestAuth, Secret);
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserPassword, encodedPassword));

        return await client.ExchangeAsync(
            packet.RawData,
            requestAuth,
            Secret,
            timeout ?? TimeSpan.FromSeconds(3),
            cancellationToken);
    }
}
