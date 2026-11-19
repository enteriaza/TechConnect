// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    TcpMtlsProtocolLimitsTests.cs
//
// Purpose:
// Verifies framed payload acceptance at the configured MaximumMessageBytes ceiling
// and rejection before oversized allocation (SEC-02).
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using FluentAssertions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;

namespace TechConnect.Client.Tests.Transport;

/// <summary>
/// Frame-limit enforcement tests using the transport's protocol-limit mapping.
/// </summary>
public sealed class TcpMtlsProtocolLimitsTests
{
    /// <summary>
    /// Scenario: serial frame payload exactly at the configured maximum.
    /// Expected: FrameReader accepts and returns the payload.
    /// </summary>
    [Fact]
    public async Task FrameAtConfiguredMaximum_IsAccepted()
    {
        const int max = 4096;
        var limits = TcpMtlsRemoteConsoleTransport.CreateProtocolLimits(new RemoteServiceOptions
        {
            MaximumMessageBytes = max
        });

        var payload = new byte[max];
        Array.Fill(payload, (byte)0x5A);

        await using var stream = new MemoryStream();
        await using (var writer = new FrameWriter(stream, limits))
        {
            await writer.WriteAsync(MessageType.SerialData, payload);
        }

        stream.Position = 0;
        var frame = await new FrameReader(stream, limits).ReadAsync();
        frame.Should().NotBeNull();
        frame!.Value.Payload.Should().Equal(payload);
    }

    /// <summary>
    /// Scenario: declared payload length exceeds the configured maximum.
    /// Expected: ProtocolPayloadTooLargeException before payload allocation/read.
    /// </summary>
    [Fact]
    public async Task FrameAboveConfiguredMaximum_RejectedBeforeAllocation()
    {
        const int max = 4096;
        var limits = TcpMtlsRemoteConsoleTransport.CreateProtocolLimits(new RemoteServiceOptions
        {
            MaximumMessageBytes = max
        });

        var header = new byte[ProtocolConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)(max + 1));
        header[4] = (byte)MessageType.SerialData;
        header[5] = ProtocolConstants.ProtocolVersion;

        // Deliberately omit payload bytes — reader must fail on length before reading payload.
        await using var stream = new MemoryStream(header);
        var act = async () => await new FrameReader(stream, limits).ReadAsync();
        await act.Should().ThrowAsync<ProtocolPayloadTooLargeException>();
    }

    /// <summary>
    /// Scenario: absolute configuration ceiling equals protocol default frame ceiling.
    /// Expected: Client cannot configure above ProtocolLimits.DefaultMaxFramePayloadBytes.
    /// </summary>
    [Fact]
    public void AbsoluteMaximum_MatchesProtocolDefaultFrameCeiling()
    {
        RemoteServiceOptions.AbsoluteMaximumMessageBytes
            .Should().Be(ProtocolLimits.DefaultMaxFramePayloadBytes);
    }
}
