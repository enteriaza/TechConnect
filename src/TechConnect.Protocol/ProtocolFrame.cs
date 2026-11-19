// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolFrame.cs
//
// Purpose:
// Represents a fully decoded TechConnect protocol frame, transferring payload
// ownership to the caller after a successful read.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol;

/// <summary>
/// A decoded protocol frame consisting of message type, protocol version, and payload bytes.
/// </summary>
/// <remarks>
/// Payload ownership is transferred to the caller: the byte array is not shared
/// with the reader after construction. The payload is never <see langword="null"/>
/// but may be empty when the frame header declares a zero-length payload.
/// </remarks>
public readonly struct ProtocolFrame
{
    /// <summary>
    /// Initializes a new frame with the specified header fields and payload.
    /// </summary>
    /// <param name="messageType">Decoded message type from the frame header.</param>
    /// <param name="protocolVersion">Protocol version byte from the frame header.</param>
    /// <param name="payload">Exact payload bytes for this frame; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public ProtocolFrame(MessageType messageType, byte protocolVersion, byte[] payload)
    {
        MessageType = messageType;
        ProtocolVersion = protocolVersion;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    /// <summary>
    /// Gets the message type decoded from the frame header.
    /// </summary>
    public MessageType MessageType { get; }

    /// <summary>
    /// Gets the protocol version byte decoded from the frame header.
    /// </summary>
    public byte ProtocolVersion { get; }

    /// <summary>
    /// Gets the exact payload bytes for this frame.
    /// </summary>
    /// <value>
    /// Never <see langword="null"/>; may be empty. For control frames these are
    /// UTF-8 JSON octets; for <see cref="MessageType.SerialData"/> they are
    /// opaque raw binary octets with no text interpretation.
    /// </value>
    public byte[] Payload { get; }

    /// <summary>
    /// Gets a <see cref="ReadOnlyMemory{T}"/> view over <see cref="Payload"/>.
    /// </summary>
    /// <remarks>
    /// The memory remains valid for as long as the caller retains the
    /// <see cref="Payload"/> array; no copy is performed.
    /// </remarks>
    public ReadOnlyMemory<byte> PayloadMemory => Payload;
}
