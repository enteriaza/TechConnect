// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TftpCopyProtocolTests.cs
//
// Purpose:
// Loopback TFTP protocol smoke test covering multi-block RRQ download and ACK
// sequencing with a short final DATA block.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Protocol-level tests for <see cref="TftpCopyFileSource"/> against a local UDP peer.
/// </summary>
public sealed class TftpCopyProtocolTests
{
    /// <summary>
    /// Verifies a 600-byte download across a full 512-byte block and an 88-byte final block.
    /// </summary>
    [Fact]
    public async Task Tftp_DownloadsMultipleBlocks_IncludingFinalShortBlock()
    {
        var payload = Enumerable.Range(0, 600).Select(i => (byte)(i & 0xFF)).ToArray();
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)server.Client.LocalEndPoint!;

        var serverTask = Task.Run(async () =>
        {
            var result = await server.ReceiveAsync();
            // RRQ
            result.Buffer[1].Should().Be(1);
            var remote = result.RemoteEndPoint;

            // Block 1: 512 bytes
            await SendDataAsync(server, remote, 1, payload.AsMemory(0, 512));
            var ack1 = await server.ReceiveAsync();
            BinaryPrimitives.ReadUInt16BigEndian(ack1.Buffer.AsSpan(0, 2)).Should().Be(4);
            BinaryPrimitives.ReadUInt16BigEndian(ack1.Buffer.AsSpan(2, 2)).Should().Be(1);

            // Block 2: remaining 88
            await SendDataAsync(server, remote, 2, payload.AsMemory(512));
            var ack2 = await server.ReceiveAsync();
            BinaryPrimitives.ReadUInt16BigEndian(ack2.Buffer.AsSpan(2, 2)).Should().Be(2);
        });

        var sourceUri = new CopySourceUri
        {
            Kind = CopySourceKind.Tftp,
            DisplayUri = $"tftp://127.0.0.1:{endpoint.Port}/fw.bin",
            Host = "127.0.0.1",
            Port = endpoint.Port,
            Path = "/fw.bin"
        };

        await using var source = await TftpCopyFileSource.OpenAsync(
            sourceUri,
            NullLogger.Instance,
            CancellationToken.None);

        var ms = new MemoryStream();
        await source.Content.CopyToAsync(ms);
        await serverTask;

        ms.ToArray().Should().Equal(payload);
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
