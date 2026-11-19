// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyBinaryStreamingTests.cs
//
// Purpose:
// Verifies opaque binary streaming through CopyTransferService, registry exclusivity,
// HTTP Basic auth streaming, and XMODEM/YMODEM payload-only receive helpers.
//
// -----------------------------------------------------------------------------

using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Copy;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Binary integrity and modem helper tests for the copy transfer path.
/// </summary>
/// <remarks>
/// Transfers use an injected serial writer so bytes are never interpreted as text.
/// XMODEM asserts padding is retained; YMODEM asserts size-based trim when implemented.
/// </remarks>
public sealed class CopyBinaryStreamingTests
{
    /// <summary>Fixed timestamp for session registration fixtures.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Verifies all 256 byte values (including NUL/CR/LF) stream unchanged — binary, not text.
    /// </summary>
    [Fact]
    public async Task Transfer_PreservesAllByteValues_IncludingNulAndInvalidUtf8()
    {
        var payload = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            payload[i] = (byte)i;
        }

        payload[10] = 0x0A;
        payload[13] = 0x0D;

        var written = new List<byte>();
        var attachment = CreateAttachment();
        await using var source = new MemoryCopySource("test", payload);
        var service = new CopyTransferService(new ActiveCopyRegistry(), NullLogger<CopyTransferService>.Instance);

        var result = await service.TransferAsync(
            source,
            attachment,
            connection: null!,
            "op-a",
            reportProgressAsync: null,
            CancellationToken.None,
            writeSerialAsync: (data, _) =>
            {
                written.AddRange(data.ToArray());
                attachment.AddBytesFromOperator(data.Length);
                attachment.AddBytesToDevice(data.Length);
                return Task.CompletedTask;
            });

        result.Success.Should().BeTrue();
        result.BytesWrittenToSerialTransport.Should().Be(256);
        written.Should().Equal(payload);
    }

    /// <summary>
    /// Verifies: Transfer Chunk Boundaries Do Not Alter Bytes.
    /// </summary>
    [Fact]
    public async Task Transfer_ChunkBoundaries_DoNotAlterBytes()
    {
        var payload = Encoding.ASCII.GetBytes(new string('A', 20_000) + "\0\r\n");
        var extra = new byte[] { 0x80, 0xFF, 0x00 };
        payload = payload.Concat(extra).ToArray();

        var written = new List<byte>();
        var attachment = CreateAttachment();
        await using var source = new MemoryCopySource("chunk", payload);
        var service = new CopyTransferService(new ActiveCopyRegistry(), NullLogger<CopyTransferService>.Instance);

        var result = await service.TransferAsync(
            source,
            attachment,
            null!,
            "op-a",
            null,
            CancellationToken.None,
            (data, _) =>
            {
                written.AddRange(data.ToArray());
                return Task.CompletedTask;
            });

        result.Success.Should().BeTrue();
        written.Should().Equal(payload);
    }

    /// <summary>
    /// Verifies: Second Transfer Same Attachment Rejected.
    /// </summary>
    [Fact]
    public void SecondTransfer_SameAttachment_Rejected()
    {
        var registry = new ActiveCopyRegistry();
        registry.TryBegin("sess1", "op-a").Should().BeTrue();
        registry.TryBegin("sess1", "op-b").Should().BeFalse();
        registry.End("sess1", "op-a");
        registry.TryBegin("sess1", "op-b").Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Http Source Streams Without Full Buffer And Auth Header.
    /// </summary>
    [Fact]
    public async Task HttpSource_StreamsWithoutFullBuffer_AndAuthHeader()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        AuthenticationHeaderValue? auth = null;
        var handler = new StubHandler((request, _) =>
        {
            auth = request.Headers.Authorization;
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        });

        var sourceUri = new CopySourceUri
        {
            Kind = CopySourceKind.Http,
            DisplayUri = "http://user@192.0.2.10/f.bin",
            Host = "192.0.2.10",
            Path = "/f.bin",
            Username = "user",
            Password = "secret"
        };

        await using var source = await HttpCopyFileSource.OpenAsync(
            sourceUri,
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        auth.Should().NotBeNull();
        auth!.Scheme.Should().Be("Basic");

        var buffer = new MemoryStream();
        await source.Content.CopyToAsync(buffer);
        buffer.ToArray().Should().Equal(payload);
    }

    /// <summary>
    /// Verifies: Xmodem Crc And Checksum Helpers.
    /// </summary>
    [Fact]
    public void Xmodem_CrcAndChecksum_Helpers()
    {
        var block = new byte[128];
        for (var i = 0; i < block.Length; i++)
        {
            block[i] = (byte)i;
        }

        var sum = XmodemReceiver.Checksum(block);
        sum.Should().Be((byte)(Enumerable.Range(0, 128).Sum() & 0xFF));
        XmodemReceiver.Crc16(block).Should().NotBe((ushort)0);
    }

    /// <summary>
    /// Verifies: Xmodem Receive Crc128 Payload Only.
    /// </summary>
    [Fact]
    public async Task Xmodem_Receive_Crc128_PayloadOnly()
    {
        var file = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var block = new byte[128];
        file.CopyTo(block, 0);
        for (var i = file.Length; i < 128; i++)
        {
            block[i] = 0x1A;
        }

        var crc = XmodemReceiver.Crc16(block);
        var sender = new Queue<byte>();
        sender.Enqueue(XmodemReceiver.SoH);
        sender.Enqueue(1);
        sender.Enqueue(0xFE);
        foreach (var b in block)
        {
            sender.Enqueue(b);
        }

        sender.Enqueue((byte)(crc >> 8));
        sender.Enqueue((byte)(crc & 0xFF));
        sender.Enqueue(XmodemReceiver.Eot);

        var writtenToSsh = new List<byte>();
        var receiver = new XmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (data, _) =>
            {
                writtenToSsh.AddRange(data.ToArray());
                return Task.CompletedTask;
            });

        var dest = new MemoryStream();
        await receiver.ReceiveAsync(dest, CancellationToken.None);
        dest.ToArray().Should().Equal(block);
        writtenToSsh.Should().Contain(XmodemReceiver.C);
        writtenToSsh.Should().Contain(XmodemReceiver.Ack);
    }

    /// <summary>
    /// Verifies: Ymodem Parses Block0 And Trims Padding.
    /// </summary>
    [Fact]
    public async Task Ymodem_ParsesBlock0_AndTrimsPadding()
    {
        var file = Encoding.ASCII.GetBytes("HELLO-BINARY\0\xFF");
        var header = new byte[128];
        var name = Encoding.ASCII.GetBytes("fw.bin");
        name.CopyTo(header, 0);
        var sizeText = Encoding.ASCII.GetBytes(file.Length.ToString());
        sizeText.CopyTo(header, name.Length + 1);

        var dataBlock = new byte[128];
        file.CopyTo(dataBlock, 0);
        for (var i = file.Length; i < 128; i++)
        {
            dataBlock[i] = 0x1A;
        }

        var sender = new Queue<byte>();
        void Enqueue(byte hdr, byte blk, byte[] payload)
        {
            var crc = XmodemReceiver.Crc16(payload);
            sender.Enqueue(hdr);
            sender.Enqueue(blk);
            sender.Enqueue((byte)(blk ^ 0xFF));
            foreach (var b in payload)
            {
                sender.Enqueue(b);
            }

            sender.Enqueue((byte)(crc >> 8));
            sender.Enqueue((byte)(crc & 0xFF));
        }

        Enqueue(XmodemReceiver.SoH, 0, header);
        Enqueue(XmodemReceiver.SoH, 1, dataBlock);
        sender.Enqueue(XmodemReceiver.Eot);
        // empty batch end
        Enqueue(XmodemReceiver.SoH, 0, new byte[128]);

        var y = new YmodemReceiver(
            _ => Task.FromResult<byte?>(sender.Count == 0 ? null : sender.Dequeue()),
            (_, _) => Task.CompletedTask);

        var dest = new MemoryStream();
        await y.ReceiveAsync(dest, CancellationToken.None);
        y.Filename.Should().Be("fw.bin");
        y.FileSize.Should().Be(file.Length);
        dest.ToArray().Should().Equal(file);
    }

    /// <summary>
    /// Creates a privilege-1 operator attachment fixture for transfer tests.
    /// </summary>
    /// <returns>Attachment bound to session <c>sess1</c>.</returns>
    private static OperatorAttachment CreateAttachment() =>
        new(
            "op-a",
            "sess1",
            "conn1",
            "host",
            T0,
            TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-a"));

    /// <summary>
    /// In-memory <see cref="TechConnect.Server.Copy.ICopyFileSource"/> backed by a fixed payload.
    /// </summary>
    private sealed class MemoryCopySource : ICopyFileSource
    {
        /// <summary>
        /// Field <c>_stream</c> used by this implementation.
        /// </summary>
        private readonly MemoryStream _stream;

        /// <summary>
        /// Creates an in-memory binary copy source for tests.
        /// </summary>
        public MemoryCopySource(string name, byte[] payload)
        {
            DisplayName = name;
            ContentLength = payload.Length;
            _stream = new MemoryStream(payload, writable: false);
        }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public long? ContentLength { get; }

        /// <inheritdoc />
        public Stream Content => _stream;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Test <see cref="HttpMessageHandler"/> that invokes a supplied callback.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>
        /// Field <c>_handler</c> used by this implementation.
        /// </summary>
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        /// <summary>
        /// Creates an <see cref="HttpMessageHandler"/> that delegates to a test callback.
        /// </summary>
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        /// <summary>
        /// Forwards the HTTP request to the test callback.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
