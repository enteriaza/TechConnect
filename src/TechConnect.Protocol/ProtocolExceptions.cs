// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolExceptions.cs
//
// Purpose:
// Defines the fatal protocol and framing exception hierarchy raised when a
// TechConnect stream becomes desynchronized or a payload fails validation.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol;

/// <summary>
/// Fatal framing or protocol error.
/// </summary>
/// <remarks>
/// After this exception (or a subclass) is thrown from a frame reader or writer,
/// the stream is considered desynchronized and the connection must be terminated.
/// Callers must not attempt to resynchronize or continue reading subsequent frames.
/// </remarks>
public class ProtocolException : Exception
{
    /// <summary>
    /// Initializes a new instance with the specified error message.
    /// </summary>
    /// <param name="message">Human-readable description of the protocol failure.</param>
    public ProtocolException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">Human-readable description of the protocol failure.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when a frame header contains a protocol version other than
/// <see cref="ProtocolConstants.ProtocolVersion"/>.
/// </summary>
public sealed class ProtocolUnsupportedVersionException : ProtocolException
{
    /// <summary>
    /// Initializes a new instance for the unsupported version byte read from the wire.
    /// </summary>
    /// <param name="version">The protocol version byte that was rejected.</param>
    public ProtocolUnsupportedVersionException(byte version)
        : base($"Unsupported protocol version {version}; expected {ProtocolConstants.ProtocolVersion}.")
    {
        Version = version;
    }

    /// <summary>
    /// Gets the unsupported protocol version byte from the frame header.
    /// </summary>
    public byte Version { get; }
}

/// <summary>
/// Thrown when a frame header contains a message type that is not a defined
/// <see cref="MessageType"/> value.
/// </summary>
public sealed class ProtocolUnknownMessageTypeException : ProtocolException
{
    /// <summary>
    /// Initializes a new instance for the unrecognized message-type byte.
    /// </summary>
    /// <param name="messageType">The raw message-type byte that was rejected.</param>
    public ProtocolUnknownMessageTypeException(byte messageType)
        : base($"Unknown message type 0x{messageType:X2}.")
    {
        MessageTypeValue = messageType;
    }

    /// <summary>
    /// Gets the unrecognized message-type byte from the frame header.
    /// </summary>
    public byte MessageTypeValue { get; }
}

/// <summary>
/// Thrown when a frame's declared or supplied payload length exceeds the
/// applicable <see cref="ProtocolLimits"/> maximum.
/// </summary>
/// <remarks>
/// Readers raise this after parsing the header and before allocating or reading
/// the payload, so an oversized length cannot force an unbounded allocation.
/// </remarks>
public sealed class ProtocolPayloadTooLargeException : ProtocolException
{
    /// <summary>
    /// Initializes a new instance describing the oversized payload.
    /// </summary>
    /// <param name="payloadLength">The payload length that was rejected.</param>
    /// <param name="maxAllowed">The maximum number of bytes permitted for that message type.</param>
    public ProtocolPayloadTooLargeException(uint payloadLength, int maxAllowed)
        : base($"Payload length {payloadLength} exceeds the maximum permitted size of {maxAllowed} bytes.")
    {
        PayloadLength = payloadLength;
        MaxAllowed = maxAllowed;
    }

    /// <summary>
    /// Gets the payload length that exceeded the limit.
    /// </summary>
    public uint PayloadLength { get; }

    /// <summary>
    /// Gets the maximum number of bytes that were permitted.
    /// </summary>
    public int MaxAllowed { get; }
}

/// <summary>
/// Thrown when the underlying stream ends after a frame has partially begun
/// (mid-header or mid-payload), rather than at a clean frame boundary.
/// </summary>
public sealed class ProtocolTruncatedFrameException : ProtocolException
{
    /// <summary>
    /// Initializes a new instance describing the truncated read.
    /// </summary>
    /// <param name="message">Details of how many bytes were expected versus received.</param>
    public ProtocolTruncatedFrameException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when a control payload fails JSON deserialization or semantic validation,
/// or when an API is misused for serial-data payloads.
/// </summary>
public sealed class ProtocolInvalidPayloadException : ProtocolException
{
    /// <summary>
    /// Initializes a new instance with the specified validation failure message.
    /// </summary>
    /// <param name="message">Description of the invalid payload or misuse.</param>
    public ProtocolInvalidPayloadException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception.
    /// </summary>
    /// <param name="message">Description of the invalid payload or misuse.</param>
    /// <param name="innerException">Underlying failure (for example a <see cref="System.Text.Json.JsonException"/>).</param>
    public ProtocolInvalidPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
