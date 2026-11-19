// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol.Tests
// File:    FrameReaderWriterTests.cs
//
// Purpose:
// Verifies TechConnect binary frame encoding, decoding, fragmentation handling,
// payload limits, concurrency, cancellation, and stream ownership behaviour.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Tests.Support;

namespace TechConnect.Protocol.Tests.Framing;

/// <summary>
/// Regression tests for <see cref="FrameReader"/> and <see cref="FrameWriter"/> wire behaviour.
/// </summary>
public sealed class FrameReaderWriterTests
{
    /// <summary>
    /// Verifies that a written frame encodes big-endian payload length, message type,
    /// protocol version, and exact payload bytes in the documented header layout.
    /// </summary>
    [Fact]
    public async Task Write_ProducesCorrectHeaderAndPayload()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream);
        var payload = Encoding.UTF8.GetBytes("{\"ok\":true}");

        await writer.WriteAsync(MessageType.HelloResult, payload);

        var bytes = stream.ToArray();
        bytes.Length.Should().Be(ProtocolConstants.HeaderSize + payload.Length);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)).Should().Be((uint)payload.Length);
        bytes[4].Should().Be((byte)MessageType.HelloResult);
        bytes[5].Should().Be(ProtocolConstants.ProtocolVersion);
        bytes.AsSpan(6).ToArray().Should().Equal(payload);
    }

    /// <summary>
    /// Verifies that a zero-length payload produces a header-only frame with length field 0.
    /// </summary>
    [Fact]
    public async Task Write_ZeroLengthPayload_WritesHeaderOnly()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.SerialData, ReadOnlyMemory<byte>.Empty);

        var bytes = stream.ToArray();
        bytes.Should().HaveCount(ProtocolConstants.HeaderSize);
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)).Should().Be(0u);
        bytes[4].Should().Be((byte)MessageType.SerialData);
    }

    /// <summary>
    /// Verifies that a complete serial-data frame delivered in one underlying read
    /// round-trips payload bytes (including NUL, 0xFF, CR/LF, ESC) and then returns null at EOF.
    /// </summary>
    [Fact]
    public async Task Read_CompleteFrameInOneRead()
    {
        await using var stream = new SegmentedMemoryStream(readChunkSize: 4096);
        await using var writer = new FrameWriter(stream);
        var payload = new byte[] { 0x00, 0xFF, 0x0A, 0x0D, 0x1B };
        await writer.WriteAsync(MessageType.SerialData, payload);

        var reader = new FrameReader(stream);
        var frame = await reader.ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(MessageType.SerialData);
        frame.Value.Payload.Should().Equal(payload);
        (await reader.ReadAsync()).Should().BeNull();
    }

    /// <summary>
    /// Verifies that a frame reader reconstructs a frame when the header arrives one byte at a time.
    /// </summary>
    [Fact]
    public async Task Read_HeaderOneByteAtATime()
    {
        await using var stream = new SegmentedMemoryStream(readChunkSize: 1);
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.Disconnect, Encoding.UTF8.GetBytes("{}"));

        var frame = await new FrameReader(stream).ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(MessageType.Disconnect);
    }

    /// <summary>
    /// Verifies that consecutive frames written to one stream are each returned by successive reads,
    /// then null at clean EOF.
    /// </summary>
    [Fact]
    public async Task Read_MultipleFramesInOneBuffer()
    {
        await using var stream = new SegmentedMemoryStream();
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.SerialData, new byte[] { 1, 2 });
        await writer.WriteAsync(MessageType.SerialData, new byte[] { 3 });

        var reader = new FrameReader(stream);
        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        first!.Value.Payload.Should().Equal(1, 2);
        second!.Value.Payload.Should().Equal(3);
        (await reader.ReadAsync()).Should().BeNull();
    }

    /// <summary>
    /// Verifies each successful read transfers a distinct payload array that remains valid after
    /// subsequent reads (caller ownership; no aliasing with the next frame).
    /// </summary>
    [Fact]
    public async Task Read_PayloadOwnership_DistinctArraysAcrossReads()
    {
        await using var stream = new SegmentedMemoryStream();
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.SerialData, new byte[] { 10, 11 });
        await writer.WriteAsync(MessageType.SerialData, new byte[] { 20, 21, 22 });

        var reader = new FrameReader(stream);
        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        ReferenceEquals(first!.Value.Payload, second!.Value.Payload).Should().BeFalse();
        first.Value.Payload.Should().Equal(10, 11);
        second.Value.Payload.Should().Equal(20, 21, 22);
    }

    /// <summary>
    /// Verifies a maximum-size SerialData payload round-trips under default limits.
    /// </summary>
    [Fact]
    public async Task Read_MaxSerialDataPayload_RoundTrips()
    {
        var max = ProtocolLimits.Default.GetMaxPayloadBytes(MessageType.SerialData);
        var payload = new byte[max];
        payload[0] = 0xA5;
        payload[^1] = 0x5A;

        await using var stream = new SegmentedMemoryStream(readChunkSize: 4096);
        await using var writer = new FrameWriter(stream);
        await writer.WriteAsync(MessageType.SerialData, payload);

        var frame = await new FrameReader(stream).ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.Payload.Should().HaveCount(max);
        frame.Value.Payload[0].Should().Be(0xA5);
        frame.Value.Payload[^1].Should().Be(0x5A);
    }

    /// <summary>
    /// Verifies that OperatorStatus frames round-trip through fragmented framing with intact JSON payloads.
    /// </summary>
    [Fact]
    public async Task OperatorStatus_RoundTripsThroughFraming()
    {
        await using var stream = new SegmentedMemoryStream(readChunkSize: 3);
        await using var writer = new FrameWriter(stream);
        var payload = Encoding.UTF8.GetBytes(
            """{"stateVersion":2,"attached":true,"terminalActive":false}""");
        await writer.WriteAsync(MessageType.OperatorStatus, payload);
        await writer.WriteAsync(MessageType.OperatorStatus, payload);

        var reader = new FrameReader(stream);
        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        first!.Value.MessageType.Should().Be(MessageType.OperatorStatus);
        second!.Value.MessageType.Should().Be(MessageType.OperatorStatus);
        first.Value.Payload.Should().Equal(payload);
        (await reader.ReadAsync()).Should().BeNull();
    }

    /// <summary>
    /// Verifies that reading from an empty stream returns null (clean EOF before any frame byte).
    /// </summary>
    [Fact]
    public async Task Read_CleanEofBetweenFrames_ReturnsNull()
    {
        await using var stream = new SegmentedMemoryStream();
        var reader = new FrameReader(stream);
        (await reader.ReadAsync()).Should().BeNull();
    }

    /// <summary>
    /// Verifies that EOF after a partial header raises <see cref="ProtocolTruncatedFrameException"/>.
    /// </summary>
    [Fact]
    public async Task Read_EofPartwayThroughHeader_ThrowsTruncated()
    {
        await using var stream = new MemoryStream(new byte[] { 0x00, 0x00 });
        var reader = new FrameReader(stream);
        var act = async () => await reader.ReadAsync();
        await act.Should().ThrowAsync<ProtocolTruncatedFrameException>();
    }

    /// <summary>
    /// Verifies that EOF after a complete header but incomplete payload raises
    /// <see cref="ProtocolTruncatedFrameException"/> mentioning the shortfall.
    /// </summary>
    [Fact]
    public async Task Read_EofPartwayThroughPayload_ThrowsTruncated()
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 4);
        header[4] = (byte)MessageType.SerialData;
        header[5] = ProtocolConstants.ProtocolVersion;
        await using var stream = new MemoryStream(header.Concat(new byte[] { 0x01, 0x02 }).ToArray());
        var reader = new FrameReader(stream);
        var act = async () => await reader.ReadAsync();
        await act.Should().ThrowAsync<ProtocolTruncatedFrameException>()
            .WithMessage("*End of stream after reading*");
    }

    /// <summary>
    /// Verifies that an undefined message-type byte raises <see cref="ProtocolUnknownMessageTypeException"/>.
    /// </summary>
    [Fact]
    public async Task Read_UnknownMessageType_Throws()
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0);
        header[4] = 0x55;
        header[5] = ProtocolConstants.ProtocolVersion;
        await using var stream = new MemoryStream(header);
        var act = async () => await new FrameReader(stream).ReadAsync();
        await act.Should().ThrowAsync<ProtocolUnknownMessageTypeException>();
    }

    /// <summary>
    /// Verifies that an unsupported protocol version byte raises <see cref="ProtocolUnsupportedVersionException"/>.
    /// </summary>
    [Fact]
    public async Task Read_UnsupportedVersion_Throws()
    {
        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0);
        header[4] = (byte)MessageType.Hello;
        header[5] = 99;
        await using var stream = new MemoryStream(header);
        var act = async () => await new FrameReader(stream).ReadAsync();
        await act.Should().ThrowAsync<ProtocolUnsupportedVersionException>();
    }

    /// <summary>
    /// Verifies that an oversized declared payload length is rejected before the reader
    /// attempts to allocate or consume payload bytes.
    /// </summary>
    [Fact]
    public async Task Read_OversizedPayload_RejectedBeforeAllocation()
    {
        var limits = new ProtocolLimits { MaxControlPayloadBytes = 8, MaxFramePayloadBytes = 8 };
        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1024);
        header[4] = (byte)MessageType.Hello;
        header[5] = ProtocolConstants.ProtocolVersion;
        // Deliberately omit payload bytes — reader must fail on length before reading payload.
        await using var stream = new MemoryStream(header);
        var act = async () => await new FrameReader(stream, limits).ReadAsync();
        await act.Should().ThrowAsync<ProtocolPayloadTooLargeException>();
    }

    /// <summary>
    /// Verifies that concurrent writers produce non-interleaved frames that each decode
    /// to a unique intact payload.
    /// </summary>
    [Fact]
    public async Task Write_ConcurrentWrites_DoNotInterleave()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream);
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            var payload = Encoding.UTF8.GetBytes($"msg-{i:0000}");
            await writer.WriteAsync(MessageType.SerialData, payload);
        });

        await Task.WhenAll(tasks);

        stream.Position = 0;
        var reader = new FrameReader(stream);
        var payloads = new List<string>();
        while (await reader.ReadAsync() is { } frame)
        {
            payloads.Add(Encoding.UTF8.GetString(frame.Payload));
        }

        payloads.Should().HaveCount(20);
        payloads.Should().OnlyHaveUniqueItems();
        payloads.Should().OnlyContain(p => p.StartsWith("msg-", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies that disposing a <see cref="FrameWriter"/> leaves the caller's stream open and writable.
    /// </summary>
    [Fact]
    public async Task Write_DoesNotDisposeCallerStream()
    {
        await using var stream = new MemoryStream();
        await using (var writer = new FrameWriter(stream))
        {
            await writer.WriteAsync(MessageType.SerialData, new byte[] { 1 });
        }

        stream.CanWrite.Should().BeTrue();
        stream.ToArray().Should().NotBeEmpty();
    }

    /// <summary>
    /// Verifies that a frame write succeeds when the underlying stream completes writes one byte at a time.
    /// </summary>
    [Fact]
    public async Task Write_HandlesPartialUnderlyingWrites()
    {
        await using var stream = new ChunkedWriteStream(writeChunkSize: 1);
        await using var writer = new FrameWriter(stream);
        var payload = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await writer.WriteAsync(MessageType.SerialData, payload);

        await using var read = new MemoryStream(stream.ToArray());
        var frame = await new FrameReader(read).ReadAsync();
        frame!.Value.Payload.Should().Equal(payload);
    }

    /// <summary>
    /// Verifies that a pre-canceled token causes <see cref="FrameWriter.WriteAsync(MessageType, byte[], CancellationToken)"/>
    /// to throw <see cref="OperationCanceledException"/>.
    /// </summary>
    [Fact]
    public async Task Write_Cancellation_Throws()
    {
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var act = async () => await writer.WriteAsync(MessageType.SerialData, new byte[] { 1 }, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies that a serial payload exactly equal to the configured maximum is accepted.
    /// </summary>
    [Fact]
    public async Task Write_MaximumPermittedSerialPayload_Succeeds()
    {
        var limits = new ProtocolLimits
        {
            MaxSerialDataPayloadBytes = 64,
            MaxFramePayloadBytes = 64
        };
        await using var stream = new MemoryStream();
        await using var writer = new FrameWriter(stream, limits);
        var payload = new byte[64];
        await writer.WriteAsync(MessageType.SerialData, payload);
        stream.Length.Should().Be(ProtocolConstants.HeaderSize + 64);
    }
}
