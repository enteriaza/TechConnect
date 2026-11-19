// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ControlMessageSerializer.cs
//
// Purpose:
// Serializes and deserializes UTF-8 JSON control-plane payloads for binary
// TechConnect frames, including field validation and serial-data copy helpers.
//
// -----------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;
using TechConnect.Protocol.Messages;

namespace TechConnect.Protocol.Serialization;

/// <summary>
/// UTF-8 JSON serialization for control-plane message payloads.
/// </summary>
/// <remarks>
/// Message type and protocol version live in the frame header — never in JSON.
/// <see cref="MessageType.SerialData"/> must not be routed through control JSON
/// APIs; use <see cref="CopySerialData"/> or raw frame writes instead.
/// This type is stateless aside from shared <see cref="JsonOptions"/> and is safe
/// for concurrent use of its public methods.
/// </remarks>
public static class ControlMessageSerializer
{
    /// <summary>
    /// Shared JSON options: camelCase, case-insensitive reads, nulls omitted when writing, no indentation.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes a <see cref="HelloMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Hello DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload suitable for a <see cref="MessageType.Hello"/> frame.</returns>
    public static byte[] SerializeHello(HelloMessage message) =>
        SerializeValidated(message, ValidateHello);

    /// <summary>
    /// Deserializes and validates a <see cref="HelloMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated hello message.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static HelloMessage DeserializeHello(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<HelloMessage>(payload, ValidateHello);

    /// <summary>
    /// Serializes a <see cref="HelloResultMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Hello result DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.HelloResult"/> frame.</returns>
    public static byte[] SerializeHelloResult(HelloResultMessage message) =>
        SerializeValidated(message, ValidateHelloResult);

    /// <summary>
    /// Deserializes and validates a <see cref="HelloResultMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated hello result.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static HelloResultMessage DeserializeHelloResult(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<HelloResultMessage>(payload, ValidateHelloResult);

    /// <summary>
    /// Serializes a <see cref="SerialConfigurationMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Serial configuration DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.SerialConfiguration"/> frame.</returns>
    public static byte[] SerializeSerialConfiguration(SerialConfigurationMessage message) =>
        SerializeValidated(message, ValidateSerialConfiguration);

    /// <summary>
    /// Deserializes and validates a <see cref="SerialConfigurationMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated serial configuration.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static SerialConfigurationMessage DeserializeSerialConfiguration(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<SerialConfigurationMessage>(payload, ValidateSerialConfiguration);

    /// <summary>
    /// Serializes a <see cref="SetBaudMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Set-baud request DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.SetBaud"/> frame.</returns>
    public static byte[] SerializeSetBaud(SetBaudMessage message) =>
        SerializeValidated(message, ValidateSetBaud);

    /// <summary>
    /// Deserializes and validates a <see cref="SetBaudMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated set-baud request.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static SetBaudMessage DeserializeSetBaud(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<SetBaudMessage>(payload, ValidateSetBaud);

    /// <summary>
    /// Serializes a <see cref="SetBaudResultMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Set-baud result DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.SetBaudResult"/> frame.</returns>
    public static byte[] SerializeSetBaudResult(SetBaudResultMessage message) =>
        SerializeValidated(message, ValidateSetBaudResult);

    /// <summary>
    /// Deserializes and validates a <see cref="SetBaudResultMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated set-baud result.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static SetBaudResultMessage DeserializeSetBaudResult(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<SetBaudResultMessage>(payload, ValidateSetBaudResult);

    /// <summary>
    /// Serializes an <see cref="ErrorMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Error DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.Error"/> frame.</returns>
    public static byte[] SerializeError(ErrorMessage message) =>
        SerializeValidated(message, ValidateError);

    /// <summary>
    /// Deserializes and validates an <see cref="ErrorMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated error message.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static ErrorMessage DeserializeError(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<ErrorMessage>(payload, ValidateError);

    /// <summary>
    /// Serializes a <see cref="DisconnectMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Disconnect DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.Disconnect"/> frame.</returns>
    public static byte[] SerializeDisconnect(DisconnectMessage message) =>
        SerializeValidated(message, ValidateDisconnect);

    /// <summary>
    /// Deserializes a <see cref="DisconnectMessage"/> from a frame payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes; may be empty.</param>
    /// <returns>
    /// A new empty <see cref="DisconnectMessage"/> when <paramref name="payload"/> is empty;
    /// otherwise the validated deserialized message.
    /// </returns>
    /// <exception cref="ProtocolInvalidPayloadException">Non-empty payload is not JSON or fails validation.</exception>
    /// <remarks>
    /// Zero-length Disconnect payloads are permitted by the protocol.
    /// </remarks>
    public static DisconnectMessage DeserializeDisconnect(ReadOnlySpan<byte> payload)
    {
        // Zero-length Disconnect payload is permitted.
        if (payload.IsEmpty)
        {
            return new DisconnectMessage();
        }

        return DeserializeValidated<DisconnectMessage>(payload, ValidateDisconnect);
    }

    /// <summary>
    /// Serializes an <see cref="OperatorStatusMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Operator status DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.OperatorStatus"/> frame.</returns>
    public static byte[] SerializeOperatorStatus(OperatorStatusMessage message) =>
        SerializeValidated(message, ValidateOperatorStatus);

    /// <summary>
    /// Deserializes and validates an <see cref="OperatorStatusMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated operator status.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static OperatorStatusMessage DeserializeOperatorStatus(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<OperatorStatusMessage>(payload, ValidateOperatorStatus);

    /// <summary>
    /// Serializes a <see cref="SessionPingMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Session ping DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.SessionPing"/> frame.</returns>
    public static byte[] SerializeSessionPing(SessionPingMessage message) =>
        SerializeValidated(message, ValidateSessionPing);

    /// <summary>
    /// Deserializes and validates a <see cref="SessionPingMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated session ping.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static SessionPingMessage DeserializeSessionPing(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<SessionPingMessage>(payload, ValidateSessionPing);

    /// <summary>
    /// Serializes a <see cref="SessionPingResultMessage"/> to UTF-8 JSON bytes after validation.
    /// </summary>
    /// <param name="message">Session ping result DTO to serialize.</param>
    /// <returns>UTF-8 JSON payload for a <see cref="MessageType.SessionPingResult"/> frame.</returns>
    public static byte[] SerializeSessionPingResult(SessionPingResultMessage message) =>
        SerializeValidated(message, ValidateSessionPingResult);

    /// <summary>
    /// Deserializes and validates a <see cref="SessionPingResultMessage"/> from a UTF-8 JSON payload.
    /// </summary>
    /// <param name="payload">Raw frame payload bytes.</param>
    /// <returns>The validated session ping result.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">Payload is empty, not JSON, or fails validation.</exception>
    public static SessionPingResultMessage DeserializeSessionPingResult(ReadOnlySpan<byte> payload) =>
        DeserializeValidated<SessionPingResultMessage>(payload, ValidateSessionPingResult);

    /// <summary>
    /// Returns a defensive copy of opaque serial-data octets.
    /// </summary>
    /// <param name="payload">Raw serial bytes; not interpreted as UTF-8 or JSON.</param>
    /// <returns>A newly allocated array containing the same bytes as <paramref name="payload"/>.</returns>
    /// <remarks>
    /// SerialData is opaque binary — every octet is preserved exactly, including
    /// <c>0x00</c>, <c>0xFF</c>, CR/LF, ESC, and sequences that are invalid UTF-8.
    /// </remarks>
    public static byte[] CopySerialData(ReadOnlySpan<byte> payload) => payload.ToArray();

    /// <summary>
    /// Dispatches serialization to the typed helper matching <paramref name="messageType"/>.
    /// </summary>
    /// <param name="messageType">Control message type identifying the expected DTO shape.</param>
    /// <param name="message">DTO instance; must match <paramref name="messageType"/> and be non-null.</param>
    /// <returns>UTF-8 JSON payload bytes for the corresponding control frame.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">
    /// <paramref name="messageType"/> is <see cref="MessageType.SerialData"/>, or validation fails.
    /// </exception>
    /// <exception cref="ProtocolUnknownMessageTypeException">
    /// <paramref name="messageType"/> is not a known control type handled here.
    /// </exception>
    /// <exception cref="InvalidCastException">
    /// <paramref name="message"/> is not the CLR type expected for <paramref name="messageType"/>.
    /// </exception>
    public static byte[] Serialize(MessageType messageType, object message) =>
        messageType switch
        {
            MessageType.Hello => SerializeHello((HelloMessage)message),
            MessageType.HelloResult => SerializeHelloResult((HelloResultMessage)message),
            MessageType.SerialConfiguration => SerializeSerialConfiguration((SerialConfigurationMessage)message),
            MessageType.SetBaud => SerializeSetBaud((SetBaudMessage)message),
            MessageType.SetBaudResult => SerializeSetBaudResult((SetBaudResultMessage)message),
            MessageType.Error => SerializeError((ErrorMessage)message),
            MessageType.Disconnect => SerializeDisconnect((DisconnectMessage)message),
            MessageType.OperatorStatus => SerializeOperatorStatus((OperatorStatusMessage)message),
            MessageType.SessionPing => SerializeSessionPing((SessionPingMessage)message),
            MessageType.SessionPingResult => SerializeSessionPingResult((SessionPingResultMessage)message),
            MessageType.SerialData => throw new ProtocolInvalidPayloadException(
                "SerialData must be written as raw bytes, not via control JSON serialization."),
            _ => throw new ProtocolUnknownMessageTypeException((byte)messageType)
        };

    /// <summary>
    /// Validates then serializes <paramref name="message"/> to UTF-8 JSON bytes.
    /// </summary>
    /// <typeparam name="T">Control message DTO type.</typeparam>
    /// <param name="message">Instance to validate and serialize; must not be <see langword="null"/>.</param>
    /// <param name="validate">Semantic validator invoked before serialization.</param>
    /// <returns>UTF-8 JSON bytes produced with <see cref="JsonOptions"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ProtocolInvalidPayloadException"><paramref name="validate"/> rejects the message.</exception>
    private static byte[] SerializeValidated<T>(T message, Action<T> validate)
    {
        ArgumentNullException.ThrowIfNull(message);
        validate(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
    }

    /// <summary>
    /// Deserializes a non-empty UTF-8 JSON payload and runs the supplied semantic validator.
    /// </summary>
    /// <typeparam name="T">Control message DTO type.</typeparam>
    /// <param name="payload">Raw JSON octets; must not be empty.</param>
    /// <param name="validate">Semantic validator invoked after successful deserialize.</param>
    /// <returns>The validated DTO instance.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">
    /// Payload is empty, not valid JSON, deserializes to <see langword="null"/>, or fails validation.
    /// </exception>
    private static T DeserializeValidated<T>(ReadOnlySpan<byte> payload, Action<T> validate)
    {
        if (payload.IsEmpty)
        {
            throw new ProtocolInvalidPayloadException("Control message payload is empty.");
        }

        T? message;
        try
        {
            message = JsonSerializer.Deserialize<T>(payload, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ProtocolInvalidPayloadException("Control message payload is not valid JSON.", ex);
        }

        if (message is null)
        {
            throw new ProtocolInvalidPayloadException("Control message payload deserialized to null.");
        }

        validate(message);
        return message;
    }

    /// <summary>
    /// Maximum permitted length of <see cref="HelloMessage.ClientSessionId"/>.
    /// </summary>
    public const int MaxClientSessionIdLength = 64;

    /// <summary>
    /// Maximum permitted length of <see cref="HelloMessage.Hostname"/>.
    /// </summary>
    public const int MaxHostnameLength = 128;

    /// <summary>
    /// Maximum permitted length of <see cref="HelloMessage.ApplicationVersion"/>.
    /// </summary>
    public const int MaxApplicationVersionLength = 64;

    /// <summary>
    /// Maximum permitted length of <see cref="HelloMessage.PortName"/>.
    /// </summary>
    public const int MaxPortNameLength = 32;

    /// <summary>
    /// Maximum permitted length of <see cref="HelloResultMessage.SessionId"/> when success is true.
    /// </summary>
    public const int MaxSessionIdLength = 32;

    /// <summary>
    /// Validates required fields and length/range constraints on a hello message.
    /// </summary>
    /// <param name="message">Hello message under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A required field or constraint fails.</exception>
    private static void ValidateHello(HelloMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ClientSessionId))
        {
            throw new ProtocolInvalidPayloadException("Hello.clientSessionId is required.");
        }

        if (message.ClientSessionId.Length > MaxClientSessionIdLength)
        {
            throw new ProtocolInvalidPayloadException(
                $"Hello.clientSessionId must be at most {MaxClientSessionIdLength} characters.");
        }

        if (message.Hostname.Length > MaxHostnameLength)
        {
            throw new ProtocolInvalidPayloadException(
                $"Hello.hostname must be at most {MaxHostnameLength} characters.");
        }

        if (message.ApplicationVersion is { Length: > MaxApplicationVersionLength })
        {
            throw new ProtocolInvalidPayloadException(
                $"Hello.applicationVersion must be at most {MaxApplicationVersionLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(message.PortName))
        {
            throw new ProtocolInvalidPayloadException("Hello.portName is required.");
        }

        if (message.PortName.Length > MaxPortNameLength)
        {
            throw new ProtocolInvalidPayloadException(
                $"Hello.portName must be at most {MaxPortNameLength} characters.");
        }

        if (message.BaudRate < 1)
        {
            throw new ProtocolInvalidPayloadException("Hello.baudRate must be at least 1.");
        }

        if (message.DataBits is < 5 or > 8)
        {
            throw new ProtocolInvalidPayloadException("Hello.dataBits must be between 5 and 8.");
        }
    }

    /// <summary>
    /// Validates success/error/session-id consistency on a hello result.
    /// </summary>
    /// <param name="message">Hello result under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A required field or constraint fails.</exception>
    private static void ValidateHelloResult(HelloResultMessage message)
    {
        if (!message.Success && string.IsNullOrWhiteSpace(message.Error))
        {
            throw new ProtocolInvalidPayloadException("HelloResult.error is required when success is false.");
        }

        if (message.Success)
        {
            if (string.IsNullOrWhiteSpace(message.SessionId))
            {
                throw new ProtocolInvalidPayloadException("HelloResult.sessionId is required when success is true.");
            }

            if (message.SessionId.Length > MaxSessionIdLength)
            {
                throw new ProtocolInvalidPayloadException(
                    $"HelloResult.sessionId must be at most {MaxSessionIdLength} characters.");
            }
        }
    }

    /// <summary>
    /// Validates baud rate and data-bits ranges on a serial configuration message.
    /// </summary>
    /// <param name="message">Serial configuration under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A range constraint fails.</exception>
    private static void ValidateSerialConfiguration(SerialConfigurationMessage message)
    {
        if (message.BaudRate < 1)
        {
            throw new ProtocolInvalidPayloadException("SerialConfiguration.baudRate must be at least 1.");
        }

        if (message.DataBits is < 5 or > 8)
        {
            throw new ProtocolInvalidPayloadException("SerialConfiguration.dataBits must be between 5 and 8.");
        }
    }

    /// <summary>
    /// Validates required serial settings on a set-baud request.
    /// </summary>
    /// <param name="message">Set-baud request under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A required field or constraint fails.</exception>
    private static void ValidateSetBaud(SetBaudMessage message)
    {
        if (message.BaudRate < 1)
        {
            throw new ProtocolInvalidPayloadException("SetBaud.baudRate must be at least 1.");
        }

        if (message.DataBits is < 5 or > 8)
        {
            throw new ProtocolInvalidPayloadException("SetBaud.dataBits must be between 5 and 8.");
        }

        if (string.IsNullOrWhiteSpace(message.Parity))
        {
            throw new ProtocolInvalidPayloadException("SetBaud.parity is required.");
        }

        if (string.IsNullOrWhiteSpace(message.StopBits))
        {
            throw new ProtocolInvalidPayloadException("SetBaud.stopBits is required.");
        }

        if (string.IsNullOrWhiteSpace(message.Handshake))
        {
            throw new ProtocolInvalidPayloadException("SetBaud.handshake is required.");
        }
    }

    /// <summary>
    /// Validates success/error consistency and optional nested config on a set-baud result.
    /// </summary>
    /// <param name="message">Set-baud result under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A required field or nested config fails.</exception>
    private static void ValidateSetBaudResult(SetBaudResultMessage message)
    {
        if (!message.Success && string.IsNullOrWhiteSpace(message.Error))
        {
            throw new ProtocolInvalidPayloadException("SetBaudResult.error is required when success is false.");
        }

        if (message.Config is not null)
        {
            ValidateSerialConfiguration(message.Config);
        }
    }

    /// <summary>
    /// Validates that error code and message are present.
    /// </summary>
    /// <param name="message">Error message under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">Code or message is missing.</exception>
    private static void ValidateError(ErrorMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Code))
        {
            throw new ProtocolInvalidPayloadException("Error.code is required.");
        }

        if (string.IsNullOrWhiteSpace(message.Message))
        {
            throw new ProtocolInvalidPayloadException("Error.message is required.");
        }
    }

    /// <summary>
    /// Validates a disconnect message.
    /// </summary>
    /// <param name="message">Disconnect message under validation.</param>
    /// <remarks>
    /// Reason is optional; an empty payload object is valid. The parameter is retained
    /// for a uniform <see cref="Action{T}"/> validation signature.
    /// </remarks>
    private static void ValidateDisconnect(DisconnectMessage message)
    {
        // Reason is optional; empty payload object is valid.
        _ = message;
    }

    /// <summary>
    /// Validates operator-status version and attached/terminal consistency.
    /// </summary>
    /// <param name="message">Operator status under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">Version is zero or terminal is active while detached.</exception>
    private static void ValidateOperatorStatus(OperatorStatusMessage message)
    {
        if (message.StateVersion == 0)
        {
            throw new ProtocolInvalidPayloadException("OperatorStatus.stateVersion must be at least 1.");
        }

        if (message.TerminalActive && !message.Attached)
        {
            throw new ProtocolInvalidPayloadException(
                "OperatorStatus.terminalActive requires OperatorStatus.attached to be true.");
        }
    }

    /// <summary>
    /// Validates that a session ping carries a correlation identifier.
    /// </summary>
    /// <param name="message">Session ping under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">Correlation id is missing.</exception>
    private static void ValidateSessionPing(SessionPingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.CorrelationId))
        {
            throw new ProtocolInvalidPayloadException("SessionPing.correlationId is required.");
        }
    }

    /// <summary>
    /// Validates correlation id and success/error consistency on a session ping result.
    /// </summary>
    /// <param name="message">Session ping result under validation.</param>
    /// <exception cref="ProtocolInvalidPayloadException">A required field fails.</exception>
    private static void ValidateSessionPingResult(SessionPingResultMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.CorrelationId))
        {
            throw new ProtocolInvalidPayloadException("SessionPingResult.correlationId is required.");
        }

        if (!message.Success && string.IsNullOrWhiteSpace(message.Error))
        {
            throw new ProtocolInvalidPayloadException("SessionPingResult.error is required when success is false.");
        }
    }
}
