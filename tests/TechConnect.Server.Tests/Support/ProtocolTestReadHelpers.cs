// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ProtocolTestReadHelpers.cs
//
// Purpose:
// FrameReader extensions that skip OperatorStatus noise and deserialize HelloResult
// for connector/SSH integration assertions.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Helpers for reading TechConnect protocol frames in tests.
/// </summary>
internal static class ProtocolTestReadHelpers
{
    /// <summary>
    /// Reads the next non-<see cref="MessageType.OperatorStatus"/> frame.
    /// </summary>
    /// <remarks>
    /// Status notifications are on-change and may appear after HelloResult or attach transitions.
    /// </remarks>
    /// <param name="reader">Protocol frame reader.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Next non-status frame.</returns>
    public static async Task<ProtocolFrame> ReadSkippingOperatorStatusAsync(
        this FrameReader reader,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frame = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            frame.Should().NotBeNull("expected a protocol frame");
            if (frame!.Value.MessageType != MessageType.OperatorStatus)
            {
                return frame.Value;
            }
        }
    }

    /// <summary>
    /// Reads and deserializes the next frame as <see cref="HelloResultMessage"/>.
    /// </summary>
    /// <param name="reader">Protocol frame reader.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deserialized hello result.</returns>
    public static async Task<HelloResultMessage> ReadHelloResultAsync(
        this FrameReader reader,
        CancellationToken cancellationToken = default)
    {
        var frame = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        frame.Should().NotBeNull();
        frame!.Value.MessageType.Should().Be(MessageType.HelloResult);
        return ControlMessageSerializer.DeserializeHelloResult(frame.Value.Payload);
    }
}
