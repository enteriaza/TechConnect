// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    MessageType.cs
//
// Purpose:
// Declares the fixed wire-level message type identities used in the TechConnect
// binary frame header.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol;

/// <summary>
/// Explicit binary frame message types. Values are fixed wire assignments — do not renumber.
/// </summary>
/// <remarks>
/// The message type occupies one byte at offset 4 of the frame header.
/// Unknown values are fatal framing errors
/// (<see cref="ProtocolUnknownMessageTypeException"/>).
/// Control message types carry UTF-8 JSON payloads;
/// <see cref="SerialData"/> carries opaque raw bytes.
/// </remarks>
public enum MessageType : byte
{
    /// <summary>
    /// Client → server registration handshake control message (<c>0x01</c>).
    /// </summary>
    Hello = 0x01,

    /// <summary>
    /// Server → client response to <see cref="Hello"/> (<c>0x02</c>).
    /// </summary>
    HelloResult = 0x02,

    /// <summary>
    /// Opaque raw serial octets with no UTF-8 or JSON interpretation (<c>0x10</c>).
    /// </summary>
    SerialData = 0x10,

    /// <summary>
    /// Serial port configuration control message (<c>0x11</c>).
    /// </summary>
    SerialConfiguration = 0x11,

    /// <summary>
    /// Request to apply serial settings (historically baud-only; now a full configuration) (<c>0x20</c>).
    /// </summary>
    SetBaud = 0x20,

    /// <summary>
    /// Result of a <see cref="SetBaud"/> request (<c>0x21</c>).
    /// </summary>
    SetBaudResult = 0x21,

    /// <summary>
    /// Server → connector: operator attachment / terminal-mode status (on-change) (<c>0x30</c>).
    /// </summary>
    OperatorStatus = 0x30,

    /// <summary>
    /// Server → connector: application-level session health probe (not ICMP, not serial) (<c>0x40</c>).
    /// </summary>
    SessionPing = 0x40,

    /// <summary>
    /// Connector → server: response to <see cref="SessionPing"/> (<c>0x41</c>).
    /// </summary>
    SessionPingResult = 0x41,

    /// <summary>
    /// Protocol-level error control message (<c>0x7E</c>).
    /// </summary>
    Error = 0x7E,

    /// <summary>
    /// Session disconnect control message; empty payload is permitted (<c>0x7F</c>).
    /// </summary>
    Disconnect = 0x7F
}
