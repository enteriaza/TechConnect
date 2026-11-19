// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolLimits.cs
//
// Purpose:
// Defines configurable maximum payload sizes enforced by frame readers and
// writers before allocating or transmitting payload bytes.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol;

/// <summary>
/// Bounded payload limits enforced before allocation or transmission.
/// </summary>
/// <remarks>
/// Defaults keep continuous serial traffic workable while capping control-plane
/// abuse. <see cref="Framing.FrameReader"/> and <see cref="Framing.FrameWriter"/>
/// apply these limits after inspecting the message type and before reading or
/// writing the payload. Instances are typically treated as immutable configuration
/// after construction; the type itself is not thread-safe for concurrent mutation
/// of init-only properties during use.
/// </remarks>
public sealed class ProtocolLimits
{
    /// <summary>
    /// Default maximum size in bytes for a control-message (UTF-8 JSON) payload (16 KiB).
    /// </summary>
    public const int DefaultMaxControlPayloadBytes = 16 * 1024;

    /// <summary>
    /// Default maximum size in bytes for a <see cref="MessageType.SerialData"/> payload (64 KiB).
    /// </summary>
    public const int DefaultMaxSerialDataPayloadBytes = 64 * 1024;

    /// <summary>
    /// Default hard ceiling in bytes for any single frame payload, regardless of message type (64 KiB).
    /// </summary>
    public const int DefaultMaxFramePayloadBytes = 64 * 1024;

    /// <summary>
    /// Maximum permitted payload length for control (non-serial) frames.
    /// </summary>
    /// <value>
    /// Byte count compared against the frame's declared payload length. The
    /// effective limit is the minimum of this value and <see cref="MaxFramePayloadBytes"/>.
    /// </value>
    public int MaxControlPayloadBytes { get; init; } = DefaultMaxControlPayloadBytes;

    /// <summary>
    /// Maximum permitted payload length for <see cref="MessageType.SerialData"/> frames.
    /// </summary>
    /// <value>
    /// Byte count compared against the frame's declared payload length. The
    /// effective limit is the minimum of this value and <see cref="MaxFramePayloadBytes"/>.
    /// </value>
    public int MaxSerialDataPayloadBytes { get; init; } = DefaultMaxSerialDataPayloadBytes;

    /// <summary>
    /// Absolute maximum payload length allowed for any frame type.
    /// </summary>
    /// <value>
    /// Hard ceiling applied together with the type-specific maximum.
    /// </value>
    public int MaxFramePayloadBytes { get; init; } = DefaultMaxFramePayloadBytes;

    /// <summary>
    /// Shared default limits instance using the constant defaults.
    /// </summary>
    /// <remarks>
    /// Safe to reuse concurrently because callers are expected not to mutate it.
    /// </remarks>
    public static ProtocolLimits Default { get; } = new();

    /// <summary>
    /// Resolves the effective maximum payload size for the specified message type.
    /// </summary>
    /// <param name="messageType">The frame message type whose limit is requested.</param>
    /// <returns>
    /// For <see cref="MessageType.SerialData"/>, the minimum of
    /// <see cref="MaxSerialDataPayloadBytes"/> and <see cref="MaxFramePayloadBytes"/>;
    /// otherwise the minimum of <see cref="MaxControlPayloadBytes"/> and
    /// <see cref="MaxFramePayloadBytes"/>.
    /// </returns>
    public int GetMaxPayloadBytes(MessageType messageType) =>
        messageType == MessageType.SerialData
            ? Math.Min(MaxSerialDataPayloadBytes, MaxFramePayloadBytes)
            : Math.Min(MaxControlPayloadBytes, MaxFramePayloadBytes);
}
