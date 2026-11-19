// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    XmodemYmodemQualificationTests.cs
//
// Purpose:
// Qualification coverage for XMODEM padding retention / CRC / checksum / retransmit
// and YMODEM size trim, malformed size, multi-file reject, oversized header.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Qualification tests for XmodemReceiver and YmodemReceiver payload semantics.
/// </summary>
/// <remarks>
/// XMODEM has no file-size metadata so padding is kept; YMODEM trims when size is known.
/// </remarks>
public sealed class XmodemYmodemQualificationTests
{
    /// <summary>
    /// Verifies classic XMODEM keeps final-block 0x1A padding (no file-size metadata).
    /// </summary>
    [Fact]
    public async Task Xmodem_ForwardsFinalBlockIncluding1APadding_NotStripped()
    {
        // Classic XMODEM: no file size. Final block padded with 0x1A — must NOT be stripped
        // because 0x1A can be legitimate payload.
        var file = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var block = new byte[128];
        file.CopyTo(block, 0);
        block.AsSpan(100).Fill(0x1A);

        var dest = await ReceiveXmodemCrcAsync(block, blockSize: 128);
        dest.Should().Equal(block);
        dest.Length.Should().Be(128);
        dest.AsSpan(100).ToArray().Should().OnlyContain(b => b == 0x1A);
    }

    /// <summary>
    /// Verifies: Xmodem 1K Crc And Duplicate Block Ignored.
    /// </summary>
    [Fact]
    public async Task Xmodem_1K_Crc_AndDuplicateBlock_Ignored()
    {
        var block = new byte[1024];
        Random.Shared.NextBytes(block);
        var crc = XmodemReceiver.Crc16(block);

        var sender = new Queue<byte>();
        void EnqueueData()
        {
            sender.Enqueue(XmodemReceiver.Stx);
            sender.Enqueue(1);
            sender.Enqueue(0xFE);
            foreach (var b in block)
            {
                sender.Enqueue(b);
            }

            sender.Enqueue((byte)(crc >> 8));
            sender.Enqueue((byte)(crc & 0xFF));
        }

        EnqueueData();
        EnqueueData(); // duplicate
        sender.Enqueue(XmodemReceiver.Eot);

        var dest = new MemoryStream();
        var x = new XmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);
        await x.ReceiveAsync(dest, CancellationToken.None);
        dest.ToArray().Should().Equal(block);
    }

    /// <summary>
    /// Verifies: Xmodem Checksum Mode 128.
    /// </summary>
    [Fact]
    public async Task Xmodem_ChecksumMode_128()
    {
        var block = new byte[128];
        for (var i = 0; i < 128; i++)
        {
            block[i] = (byte)(255 - i);
        }

        var sum = XmodemReceiver.Checksum(block);
        // Force checksum mode: respond only after NAK (not to 'C')
        var phase = 0;
        var sender = new Queue<byte>();
        var x = new XmodemReceiver(
            _ =>
            {
                if (sender.Count > 0)
                {
                    return Task.FromResult<byte?>(sender.Dequeue());
                }

                return Task.FromResult<byte?>(null);
            },
            (data, _) =>
            {
                var b = data.Span[0];
                if (b == XmodemReceiver.C)
                {
                    // Ignore CRC probes — fall through to NAK handshake.
                    return Task.CompletedTask;
                }

                if (b == XmodemReceiver.Nak && phase == 0)
                {
                    phase = 1;
                    sender.Enqueue(XmodemReceiver.SoH);
                    sender.Enqueue(1);
                    sender.Enqueue(0xFE);
                    foreach (var d in block)
                    {
                        sender.Enqueue(d);
                    }

                    sender.Enqueue(sum);
                    sender.Enqueue(XmodemReceiver.Eot);
                }

                return Task.CompletedTask;
            });

        var dest = new MemoryStream();
        await x.ReceiveAsync(dest, CancellationToken.None);
        dest.ToArray().Should().Equal(block);
    }

    /// <summary>
    /// Verifies: Xmodem Invalid Crc Retransmits Then Succeeds.
    /// </summary>
    [Fact]
    public async Task Xmodem_InvalidCrc_RetransmitsThenSucceeds()
    {
        var block = new byte[128];
        block.AsSpan().Fill(0x42);
        var goodCrc = XmodemReceiver.Crc16(block);
        var sender = new Queue<byte>();

        // Bad CRC first
        sender.Enqueue(XmodemReceiver.SoH);
        sender.Enqueue(1);
        sender.Enqueue(0xFE);
        foreach (var b in block)
        {
            sender.Enqueue(b);
        }

        sender.Enqueue(0x00);
        sender.Enqueue(0x00);

        // Good retry
        sender.Enqueue(XmodemReceiver.SoH);
        sender.Enqueue(1);
        sender.Enqueue(0xFE);
        foreach (var b in block)
        {
            sender.Enqueue(b);
        }

        sender.Enqueue((byte)(goodCrc >> 8));
        sender.Enqueue((byte)(goodCrc & 0xFF));
        sender.Enqueue(XmodemReceiver.Eot);

        var naks = 0;
        var x = new XmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (data, _) =>
            {
                if (data.Span[0] == XmodemReceiver.Nak)
                {
                    naks++;
                }

                return Task.CompletedTask;
            });

        var dest = new MemoryStream();
        await x.ReceiveAsync(dest, CancellationToken.None);
        dest.ToArray().Should().Equal(block);
        naks.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Verifies YMODEM trims final-block padding when block-0 advertises file size.
    /// </summary>
    /// <param name="fileSize">Advertised and expected payload length.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(128)]
    [InlineData(129)]
    [InlineData(256)]
    public async Task Ymodem_ExactSize_TrimsPadding(int fileSize)
    {
        var file = new byte[fileSize];
        for (var i = 0; i < fileSize; i++)
        {
            file[i] = (byte)(i ^ 0xA5);
        }

        // Ensure padding would be wrong if forwarded: fill remainder with 0x1A and 0xFF mix
        var blocks = new List<byte[]>();
        var offset = 0;
        while (offset < Math.Max(fileSize, 1) || (fileSize == 0 && blocks.Count == 0))
        {
            if (fileSize == 0)
            {
                break;
            }

            var chunk = new byte[128];
            chunk.AsSpan().Fill(0x1A);
            var take = Math.Min(128, fileSize - offset);
            file.AsSpan(offset, take).CopyTo(chunk);
            if (take < 128)
            {
                for (var i = take; i < 128; i++)
                {
                    chunk[i] = (byte)(i % 2 == 0 ? 0x1A : 0xFF);
                }
            }

            blocks.Add(chunk);
            offset += take;
            if (take < 128)
            {
                break;
            }
        }

        var dest = await ReceiveYmodemAsync("q.bin", fileSize, blocks);
        dest.Should().Equal(file);
    }

    /// <summary>
    /// Verifies: Ymodem Malformed Size Forwards Full Blocks.
    /// </summary>
    [Fact]
    public async Task Ymodem_MalformedSize_ForwardsFullBlocks()
    {
        var data = new byte[128];
        data.AsSpan().Fill(0x55);
        // Header with non-numeric size → FileSize null → full block forwarded
        var header = BuildHeader("x.bin", sizeText: "not-a-number");
        var dest = await ReceiveYmodemRawAsync(header, [data], expectEmptyBatch: true);
        dest.Should().Equal(data);
    }

    /// <summary>
    /// Verifies: Ymodem Multi File Batch Rejected.
    /// </summary>
    [Fact]
    public async Task Ymodem_MultiFileBatch_Rejected()
    {
        var header1 = BuildHeader("a.bin", sizeText: "1");
        var data = new byte[128];
        data[0] = 0x01;
        var header2 = BuildHeader("b.bin", sizeText: "1");

        var sender = new Queue<byte>();
        EnqueueBlock(sender, 0, header1);
        EnqueueBlock(sender, 1, data);
        sender.Enqueue(XmodemReceiver.Eot);
        EnqueueBlock(sender, 0, header2);

        var y = new YmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);

        var act = async () => await y.ReceiveAsync(new MemoryStream(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*multi-file*");
    }

    /// <summary>
    /// Verifies: Ymodem Oversized Advertised Size Rejected.
    /// </summary>
    [Fact]
    public async Task Ymodem_OversizedAdvertisedSize_Rejected()
    {
        var header = BuildHeader("huge.bin", sizeText: (YmodemReceiver.MaxAdvertisedFileSize + 1).ToString());
        var sender = new Queue<byte>();
        EnqueueBlock(sender, 0, header);
        var y = new YmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);

        var act = async () => await y.ReceiveAsync(new MemoryStream(), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unreasonably large*");
    }

    /// <summary>
    /// Receives a single CRC XMODEM block (plus EOT) into a byte array.
    /// </summary>
    private static async Task<byte[]> ReceiveXmodemCrcAsync(byte[] block, int blockSize)
    {
        var crc = XmodemReceiver.Crc16(block);
        var sender = new Queue<byte>();
        sender.Enqueue(blockSize == 1024 ? XmodemReceiver.Stx : XmodemReceiver.SoH);
        sender.Enqueue(1);
        sender.Enqueue(0xFE);
        foreach (var b in block)
        {
            sender.Enqueue(b);
        }

        sender.Enqueue((byte)(crc >> 8));
        sender.Enqueue((byte)(crc & 0xFF));
        sender.Enqueue(XmodemReceiver.Eot);

        var dest = new MemoryStream();
        var x = new XmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);
        await x.ReceiveAsync(dest, CancellationToken.None);
        return dest.ToArray();
    }

    /// <summary>
    /// Receives a YMODEM file with known size and optional empty end-of-batch block.
    /// </summary>
    private static async Task<byte[]> ReceiveYmodemAsync(string name, int fileSize, List<byte[]> dataBlocks)
    {
        var header = BuildHeader(name, sizeText: fileSize.ToString());
        return await ReceiveYmodemRawAsync(header, dataBlocks, expectEmptyBatch: true);
    }

    /// <summary>
    /// Receives YMODEM using a prebuilt header and data blocks.
    /// </summary>
    private static async Task<byte[]> ReceiveYmodemRawAsync(
        byte[] header,
        List<byte[]> dataBlocks,
        bool expectEmptyBatch)
    {
        var sender = new Queue<byte>();
        EnqueueBlock(sender, 0, header);
        byte blk = 1;
        foreach (var data in dataBlocks)
        {
            EnqueueBlock(sender, blk++, data);
        }

        sender.Enqueue(XmodemReceiver.Eot);
        if (expectEmptyBatch)
        {
            EnqueueBlock(sender, 0, new byte[128]);
        }

        var dest = new MemoryStream();
        var y = new YmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);
        await y.ReceiveAsync(dest, CancellationToken.None);
        return dest.ToArray();
    }

    /// <summary>
    /// Builds a YMODEM block-0 header with filename and size text.
    /// </summary>
    private static byte[] BuildHeader(string name, string sizeText)
    {
        var header = new byte[128];
        var nameBytes = Encoding.ASCII.GetBytes(name);
        nameBytes.CopyTo(header, 0);
        var sizeBytes = Encoding.ASCII.GetBytes(sizeText);
        sizeBytes.CopyTo(header, nameBytes.Length + 1);
        return header;
    }

    /// <summary>
    /// Enqueues a CRC-protected SOH block into the simulated sender queue.
    /// </summary>
    private static void EnqueueBlock(Queue<byte> sender, byte blockNumber, byte[] payload)
    {
        var crc = XmodemReceiver.Crc16(payload);
        sender.Enqueue(XmodemReceiver.SoH);
        sender.Enqueue(blockNumber);
        sender.Enqueue((byte)(blockNumber ^ 0xFF));
        foreach (var b in payload)
        {
            sender.Enqueue(b);
        }

        sender.Enqueue((byte)(crc >> 8));
        sender.Enqueue((byte)(crc & 0xFF));
    }
}
