// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TftpCopyQualificationTests.cs
//
// Purpose:
// Qualification coverage for TFTP exact sizes, duplicate-block handling, and
// ERROR-packet failure against a local UDP peer.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Qualification tests for binary-exact TFTP downloads via <see cref="TftpCopyFileSource"/>.
/// </summary>
/// <remarks>
/// Content length remains unknown (null) on the source; integrity is verified by
/// comparing streamed payload bytes and SHA-256 digests.
/// </remarks>
public sealed class TftpCopyQualificationTests
{
    /// <summary>
    /// Verifies exact payload sizes including empty, short, boundary, and multi-block cases.
    /// </summary>
    /// <param name="length">Payload length in bytes.</param>
    /// <remarks>
    /// Exact multiples of 512 require a zero-length final DATA block per RFC 1350.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(2048)]
    public async Task Tftp_ExactSizes_IncludingBoundaryAndShortFinal(int length)
    {
        var payload = length == 0
            ? []
            : CopyBinaryIntegrityQualificationTests.BuildQualificationPayload(length);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;

        var serverTask = Task.Run(async () =>
        {
            var rrq = await server.ReceiveAsync();
            var remote = rrq.RemoteEndPoint;
            ushort block = 1;
            var offset = 0;
            do
            {
                var take = Math.Min(TftpCopyFileSource.DefaultBlockSize, payload.Length - offset);
                var slice = payload.AsMemory(offset, take);
                await SendDataAsync(server, remote, block, slice);
                var ack = await server.ReceiveAsync();
                BinaryPrimitives.ReadUInt16BigEndian(ack.Buffer.AsSpan(2, 2)).Should().Be(block);
                offset += take;
                block++;
                if (take < TftpCopyFileSource.DefaultBlockSize)
                {
                    break;
                }
            } while (offset < payload.Length);

            // Exact multiple of 512 requires a zero-length final DATA block.
            if (payload.Length > 0 && payload.Length % TftpCopyFileSource.DefaultBlockSize == 0)
            {
                await SendDataAsync(server, remote, block, ReadOnlyMemory<byte>.Empty);
                var ack = await server.ReceiveAsync();
                BinaryPrimitives.ReadUInt16BigEndian(ack.Buffer.AsSpan(2, 2)).Should().Be(block);
            }
        });

        var sourceUri = new CopySourceUri
        {
            Kind = CopySourceKind.Tftp,
            DisplayUri = $"tftp://127.0.0.1:{endpoint.Port}/q.bin",
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Path = "/q.bin"
        };

        await using var source = await TftpCopyFileSource.OpenAsync(
            sourceUri,
            NullLogger.Instance,
            CancellationToken.None);

        var ms = new MemoryStream();
        await source.Content.CopyToAsync(ms);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Convert.ToHexString(SHA256.HashData(ms.ToArray())).Should().Be(expectedHash);
        ms.ToArray().Should().Equal(payload);
    }

    /// <summary>
    /// Verifies a retransmitted DATA block is ACKed again without duplicating payload bytes.
    /// </summary>
    [Fact]
    public async Task Tftp_DuplicateBlock_DoesNotDuplicatePayload()
    {
        // Block 1 must be full-size so the client does not finish before the retransmit.
        var payload = CopyBinaryIntegrityQualificationTests.BuildQualificationPayload(600);
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;

        var serverTask = Task.Run(async () =>
        {
            var rrq = await server.ReceiveAsync();
            var remote = rrq.RemoteEndPoint;
            var block1 = payload.AsMemory(0, 512);
            var block2 = payload.AsMemory(512);
            await SendDataAsync(server, remote, 1, block1);
            // Retransmit block 1 — client should ACK again without appending twice.
            await SendDataAsync(server, remote, 1, block1);
            _ = await server.ReceiveAsync();
            _ = await server.ReceiveAsync();
            await SendDataAsync(server, remote, 2, block2);
            _ = await server.ReceiveAsync();
        });

        var sourceUri = new CopySourceUri
        {
            Kind = CopySourceKind.Tftp,
            DisplayUri = $"tftp://127.0.0.1:{endpoint.Port}/q.bin",
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Path = "/q.bin"
        };

        await using var source = await TftpCopyFileSource.OpenAsync(
            sourceUri,
            NullLogger.Instance,
            CancellationToken.None);

        var ms = new MemoryStream();
        await source.Content.CopyToAsync(ms);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        ms.ToArray().Should().Equal(payload);
    }

    /// <summary>
    /// Verifies a TFTP ERROR packet surfaces as an operator-facing InvalidOperationException.
    /// </summary>
    [Fact]
    public async Task Tftp_ErrorPacket_FailsCleanly()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;

        var serverTask = Task.Run(async () =>
        {
            var rrq = await server.ReceiveAsync();
            var err = new byte[] { 0, 5, 0, 1, (byte)'n', (byte)'o', 0 };
            await server.SendAsync(err, rrq.RemoteEndPoint);
        });

        var sourceUri = new CopySourceUri
        {
            Kind = CopySourceKind.Tftp,
            DisplayUri = $"tftp://127.0.0.1:{endpoint.Port}/missing.bin",
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Path = "/missing.bin"
        };

        await using var source = await TftpCopyFileSource.OpenAsync(
            sourceUri,
            NullLogger.Instance,
            CancellationToken.None);

        var act = async () => await source.Content.CopyToAsync(new MemoryStream());
        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("TFTP");
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Sends a TFTP DATA packet for the given block number.
    /// </summary>
    /// <param name="server">UDP client acting as the TFTP server.</param>
    /// <param name="remote">Client endpoint.</param>
    /// <param name="block">Block number.</param>
    /// <param name="data">Payload bytes for this block.</param>
    private static async Task SendDataAsync(
        UdpClient server,
        IPEndPoint remote,
        ushort block,
        ReadOnlyMemory<byte> data)
    {
        var packet = new byte[4 + data.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), block);
        data.Span.CopyTo(packet.AsSpan(4));
        await server.SendAsync(packet, remote);
    }
}
