// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    HelloMessage.cs
//
// Purpose:
// Defines the UTF-8 JSON control-message DTOs carried inside TechConnect binary
// frames (hello, serial configuration, operator status, session ping, and related).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Protocol.Messages;

/// <summary>
/// Client → server registration handshake payload for <see cref="MessageType.Hello"/>.
/// </summary>
/// <remarks>
/// Serialized as camelCase UTF-8 JSON. Message type and protocol version live in
/// the frame header and are not repeated in this payload.
/// </remarks>
public sealed class HelloMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier for matching request/response pairs.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the client-assigned session identifier required for registration.
    /// </summary>
    /// <remarks>
    /// Must be non-whitespace and at most
    /// <see cref="Serialization.ControlMessageSerializer.MaxClientSessionIdLength"/> characters.
    /// </remarks>
    public string ClientSessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional connector hostname reported during registration.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a descriptive connector/application version string.
    /// </summary>
    /// <remarks>Not used for identity; length-limited when present.</remarks>
    public string? ApplicationVersion { get; set; }

    /// <summary>
    /// Gets or sets the COM/serial port name being offered (for example <c>COM3</c>).
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the initial baud rate for the serial port.
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// Gets or sets the number of data bits (validated range 5–8).
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// Gets or sets the parity mode name (for example <c>None</c>).
    /// </summary>
    public string Parity { get; set; } = "None";

    /// <summary>
    /// Gets or sets the stop-bits mode name (for example <c>One</c>).
    /// </summary>
    public string StopBits { get; set; } = "One";

    /// <summary>
    /// Gets or sets the handshake mode name (for example <c>None</c>).
    /// </summary>
    public string Handshake { get; set; } = "None";
}

/// <summary>
/// Server → client response payload for <see cref="MessageType.HelloResult"/>.
/// </summary>
public sealed class HelloResultMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier echoing the request.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets whether registration succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the failure description; required when <see cref="Success"/> is <see langword="false"/>.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the optional server display name.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Gets or sets the server-assigned session ID; required when <see cref="Success"/> is <see langword="true"/>.
    /// </summary>
    public string? SessionId { get; set; }
}

/// <summary>
/// Serial port configuration payload for <see cref="MessageType.SerialConfiguration"/>
/// and nested configuration on baud results.
/// </summary>
public sealed class SerialConfigurationMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the baud rate (must be at least 1 when validated).
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// Gets or sets the number of data bits (validated range 5–8).
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// Gets or sets the parity mode name.
    /// </summary>
    public string Parity { get; set; } = "None";

    /// <summary>
    /// Gets or sets the stop-bits mode name.
    /// </summary>
    public string StopBits { get; set; } = "One";

    /// <summary>
    /// Gets or sets the handshake mode name.
    /// </summary>
    public string Handshake { get; set; } = "None";
}

/// <summary>
/// Server → connector request to apply serial settings for <see cref="MessageType.SetBaud"/>.
/// </summary>
/// <remarks>
/// Historically baud-only; now carries a complete configuration (defaults apply for
/// omitted fields on older senders). The result is still <see cref="SetBaudResultMessage"/>.
/// </remarks>
public sealed class SetBaudMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the requested baud rate (must be at least 1).
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// Gets or sets the number of data bits (validated range 5–8).
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// Gets or sets the required parity mode name.
    /// </summary>
    public string Parity { get; set; } = "None";

    /// <summary>
    /// Gets or sets the required stop-bits mode name.
    /// </summary>
    public string StopBits { get; set; } = "One";

    /// <summary>
    /// Gets or sets the required handshake mode name.
    /// </summary>
    public string Handshake { get; set; } = "None";
}

/// <summary>
/// Response payload for <see cref="MessageType.SetBaudResult"/>.
/// </summary>
public sealed class SetBaudResultMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets whether the serial settings were applied successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the failure description; required when <see cref="Success"/> is <see langword="false"/>.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the effective serial configuration when provided by the sender.
    /// </summary>
    public SerialConfigurationMessage? Config { get; set; }
}

/// <summary>
/// Protocol-level error payload for <see cref="MessageType.Error"/>.
/// </summary>
public sealed class ErrorMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the machine-oriented error code (required, non-whitespace).
    /// </summary>
    public string Code { get; set; } = "error";

    /// <summary>
    /// Gets or sets the human-readable error message (required, non-whitespace).
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Disconnect notification payload for <see cref="MessageType.Disconnect"/>.
/// </summary>
/// <remarks>
/// A zero-length frame payload is permitted and deserializes to an empty instance.
/// </remarks>
public sealed class DisconnectMessage
{
    /// <summary>
    /// Gets or sets an optional correlation identifier.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets an optional human-readable disconnect reason.
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Server → connector notification of NOC operator attachment / terminal mode for
/// <see cref="MessageType.OperatorStatus"/>.
/// </summary>
/// <remarks>
/// <see cref="TerminalActive"/> may be <see langword="true"/> only when
/// <see cref="Attached"/> is <see langword="true"/>. This is an on-change
/// notification, not a heartbeat.
/// </remarks>
public sealed class OperatorStatusMessage
{
    /// <summary>
    /// Gets or sets the monotonic state version within one TCP connection generation.
    /// </summary>
    /// <remarks>Must be at least 1; clients typically apply only strictly increasing versions.</remarks>
    public uint StateVersion { get; set; }

    /// <summary>
    /// Gets or sets whether an operator currently owns this connector session.
    /// </summary>
    public bool Attached { get; set; }

    /// <summary>
    /// Gets or sets whether the operator terminal is active; requires <see cref="Attached"/>.
    /// </summary>
    public bool TerminalActive { get; set; }
}

/// <summary>
/// Server → connector application health probe for <see cref="MessageType.SessionPing"/>.
/// </summary>
/// <remarks>
/// Must not touch the COM port or serial stream. Distinct from ICMP and serial traffic.
/// </remarks>
public sealed class SessionPingMessage
{
    /// <summary>
    /// Gets or sets the required correlation identifier for matching the ping result.
    /// </summary>
    public string? CorrelationId { get; set; }
}

/// <summary>
/// Connector → server response to <see cref="SessionPingMessage"/> for
/// <see cref="MessageType.SessionPingResult"/>.
/// </summary>
public sealed class SessionPingResultMessage
{
    /// <summary>
    /// Gets or sets the required correlation identifier matching the ping request.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets whether the session probe succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets the failure description; required when <see cref="Success"/> is <see langword="false"/>.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets an optional hostname reported with a successful probe.
    /// </summary>
    public string? Hostname { get; set; }
}
