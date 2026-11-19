// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolConstants.cs
//
// Purpose:
// Defines fixed wire-format constants for the TechConnect binary framing
// protocol, including header size and the supported protocol version.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol;

/// <summary>
/// Wire-format constants for the TechConnect binary framing protocol.
/// </summary>
/// <remarks>
/// Every frame begins with a 6-byte header followed by an optional payload.
/// Multi-byte integer fields use network byte order (big-endian).
/// <code>
/// Offset  Size  Field
/// 0       4     PayloadLength (UInt32 BE) — length of payload only; excludes this 6-byte header
/// 4       1     MessageType
/// 5       1     ProtocolVersion
/// 6       N     Payload (N = PayloadLength)
/// </code>
/// </remarks>
public static class ProtocolConstants
{
    /// <summary>
    /// Size in bytes of the fixed frame header that precedes every payload.
    /// </summary>
    /// <remarks>
    /// The header contains the 4-byte big-endian payload length, the 1-byte
    /// <see cref="MessageType"/>, and the 1-byte protocol version.
    /// </remarks>
    public const int HeaderSize = 6;

    /// <summary>
    /// Protocol version written into and accepted from the frame header.
    /// </summary>
    /// <remarks>
    /// Frames whose version byte differs from this value cause
    /// <see cref="ProtocolUnsupportedVersionException"/>.
    /// </remarks>
    public const byte ProtocolVersion = 1;
}
