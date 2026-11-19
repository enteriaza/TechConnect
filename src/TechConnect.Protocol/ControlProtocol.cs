// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ControlProtocol.cs
//
// Purpose:
// Control JSON envelope and related payload DTOs used by TechConnect.Client
// session code paths alongside binary TechConnect.Protocol framing.
//
// -----------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TechConnect.Protocol;

/// <summary>
/// Control JSON envelope helpers used by TechConnect.Client session code.
/// </summary>
/// <remarks>
/// TCP/mTLS traffic uses <see cref="Framing.FrameReader"/> /
/// <see cref="Framing.FrameWriter"/> with typed payloads under <see cref="Messages"/>.
/// This static helper serializes a versioned JSON envelope where the logical message
/// type is a string inside JSON rather than a binary frame header byte. The Client
/// maps between this envelope and binary control frames on the wire.
/// </remarks>
public static class ControlProtocol
{
    /// <summary>
    /// Supported legacy control envelope version written into <see cref="ControlMessage.V"/>.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Envelope type string for a connector hello registration message.
    /// </summary>
    public const string TypeConnectorHello = "connector.hello";

    /// <summary>
    /// Envelope type string for a serial configuration message.
    /// </summary>
    public const string TypeSerialConfig = "serial.config";

    /// <summary>
    /// Envelope type string for a set-baud request.
    /// </summary>
    public const string TypeSetBaud = "serial.setBaud";

    /// <summary>
    /// Envelope type string for a set-baud result.
    /// </summary>
    public const string TypeSetBaudResult = "serial.setBaudResult";

    /// <summary>
    /// Envelope type string for a session ping probe.
    /// </summary>
    public const string TypeSessionPing = "session.ping";

    /// <summary>
    /// Envelope type string for a session ping result.
    /// </summary>
    public const string TypeSessionPingResult = "session.pingResult";

    /// <summary>
    /// Envelope type string for an error message.
    /// </summary>
    public const string TypeError = "error";

    /// <summary>
    /// Baud rates accepted by <see cref="IsSupportedBaudRate"/>.
    /// </summary>
    public static readonly int[] SupportedBaudRates =
    [
        9600,
        19200,
        38400,
        57600,
        115200,
        230400,
        460800,
        921600
    ];

    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> for legacy envelope serialization.
    /// </summary>
    /// <remarks>
    /// Uses camelCase names, case-insensitive property matching, omits nulls when writing,
    /// and does not indent.
    /// </remarks>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Returns whether <paramref name="baudRate"/> appears in <see cref="SupportedBaudRates"/>.
    /// </summary>
    /// <param name="baudRate">Baud rate to test.</param>
    /// <returns><see langword="true"/> when the rate is in the supported list.</returns>
    public static bool IsSupportedBaudRate(int baudRate) =>
        SupportedBaudRates.Contains(baudRate);

    /// <summary>
    /// Serializes a control envelope to a JSON string using <see cref="JsonOptions"/>.
    /// </summary>
    /// <param name="message">Envelope to serialize; must not be <see langword="null"/>.</param>
    /// <returns>Compact UTF-16 JSON string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    public static string Serialize(ControlMessage message) =>
        JsonSerializer.Serialize(message ?? throw new ArgumentNullException(nameof(message)), JsonOptions);

    /// <summary>
    /// Attempts to deserialize and validate a legacy control envelope JSON string.
    /// </summary>
    /// <param name="json">JSON text to parse.</param>
    /// <param name="message">Receives the envelope on success; otherwise <see langword="null"/>.</param>
    /// <param name="error">Receives a human-readable failure reason on failure.</param>
    /// <returns>
    /// <see langword="true"/> when the JSON is a valid envelope with
    /// <see cref="Version"/> and a non-empty <see cref="ControlMessage.Type"/>.
    /// </returns>
    public static bool TryParse(string json, out ControlMessage? message, out string? error)
    {
        message = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Control message is empty.";
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<ControlMessage>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "Control message is not valid JSON: " + ex.Message;
            return false;
        }

        if (message is null)
        {
            error = "Control message deserialized to null.";
            return false;
        }

        if (message.V != Version)
        {
            error = $"Unsupported control protocol version {message.V}; expected {Version}.";
            message = null;
            return false;
        }

        if (string.IsNullOrWhiteSpace(message.Type))
        {
            error = "Control message type is required.";
            message = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Creates a versioned control envelope wrapping a typed payload as a <see cref="JsonElement"/>.
    /// </summary>
    /// <typeparam name="TPayload">CLR type of the nested payload object.</typeparam>
    /// <param name="type">Envelope type string; must be non-whitespace.</param>
    /// <param name="payload">Payload object serialized into <see cref="ControlMessage.Payload"/>.</param>
    /// <param name="correlationId">Optional correlation identifier copied onto the envelope.</param>
    /// <returns>A new <see cref="ControlMessage"/> with <see cref="Version"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="type"/> is null or whitespace.</exception>
    public static ControlMessage Create<TPayload>(string type, TPayload payload, string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        return new ControlMessage
        {
            V = Version,
            Type = type,
            CorrelationId = correlationId,
            Payload = JsonSerializer.SerializeToElement(payload, JsonOptions)
        };
    }

    /// <summary>
    /// Attempts to deserialize the nested <see cref="ControlMessage.Payload"/> as <typeparamref name="TPayload"/>.
    /// </summary>
    /// <typeparam name="TPayload">Expected nested payload CLR type.</typeparam>
    /// <param name="message">Envelope whose payload element is read.</param>
    /// <param name="payload">Receives the typed payload on success.</param>
    /// <param name="error">Receives a human-readable failure reason on failure.</param>
    /// <returns><see langword="true"/> when the payload deserializes to a non-null value.</returns>
    public static bool TryGetPayload<TPayload>(ControlMessage message, out TPayload? payload, out string? error)
    {
        payload = default;
        error = null;

        if (message.Payload is null || message.Payload.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = "Control message payload is missing.";
            return false;
        }

        try
        {
            payload = message.Payload.Value.Deserialize<TPayload>(JsonOptions);
        }
        catch (JsonException ex)
        {
            error = "Control message payload is invalid: " + ex.Message;
            return false;
        }

        if (payload is null)
        {
            error = "Control message payload deserialized to null.";
            return false;
        }

        return true;
    }
}

/// <summary>
/// Versioned control JSON envelope used by TechConnect.Client session code.
/// </summary>
public sealed class ControlMessage
{
    /// <summary>
    /// Gets or sets the envelope protocol version; must equal <see cref="ControlProtocol.Version"/>.
    /// </summary>
    public int V { get; set; }

    /// <summary>
    /// Gets or sets the logical message type string (for example <see cref="ControlProtocol.TypeConnectorHello"/>).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional correlation identifier for request/response matching.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the nested JSON payload element for the typed body.
    /// </summary>
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// Nested payload for a legacy connector hello registration message.
/// </summary>
public sealed class ConnectorHelloPayload
{
    /// <summary>
    /// Gets or sets the client-assigned session identifier.
    /// </summary>
    public string ClientSessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the connector hostname.
    /// </summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets an optional descriptive application version.
    /// </summary>
    public string? ApplicationVersion { get; set; }

    /// <summary>
    /// Gets or sets the serial port name being offered.
    /// </summary>
    public string PortName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the initial serial configuration nested under the hello.
    /// </summary>
    public SerialConfigPayload Config { get; set; } = new();
}

/// <summary>
/// Nested serial configuration fields used by legacy control envelopes.
/// </summary>
public sealed class SerialConfigPayload
{
    /// <summary>
    /// Gets or sets the baud rate.
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// Gets or sets the number of data bits (defaults to 8).
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
/// Nested payload for a legacy set-baud request.
/// </summary>
public sealed class SetBaudPayload
{
    /// <summary>
    /// Gets or sets the requested baud rate.
    /// </summary>
    public int BaudRate { get; set; }

    /// <summary>
    /// Gets or sets the number of data bits (defaults to 8).
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
/// Nested payload for a legacy set-baud result.
/// </summary>
public sealed class SetBaudResultPayload
{
    /// <summary>
    /// Gets or sets whether the baud/configuration change succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets an optional failure description.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets the effective configuration when provided.
    /// </summary>
    public SerialConfigPayload? Config { get; set; }
}

/// <summary>
/// Nested payload for a legacy error envelope.
/// </summary>
public sealed class ErrorPayload
{
    /// <summary>
    /// Gets or sets the machine-oriented error code.
    /// </summary>
    public string Code { get; set; } = "error";

    /// <summary>
    /// Gets or sets the human-readable error message.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Nested payload for a legacy session ping result.
/// </summary>
public sealed class SessionPingResultPayload
{
    /// <summary>
    /// Gets or sets whether the session probe succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets an optional failure description.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets an optional hostname reported with the result.
    /// </summary>
    public string? Hostname { get; set; }
}
