// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolJsonContext.cs
//
// Purpose:
// System.Text.Json source-generation context for Native AOT / trimmed publishes.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Serialization;
using TechConnect.Protocol.Messages;

namespace TechConnect.Protocol.Serialization;

/// <summary>
/// Source-generated JSON metadata for protocol control DTOs and legacy envelopes.
/// </summary>
/// <remarks>
/// Required for Native AOT publishing. Wire JSON shape (camelCase, null omission)
/// matches the historical reflection-based <see cref="ControlMessageSerializer.JsonOptions"/>.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(HelloResultMessage))]
[JsonSerializable(typeof(SerialConfigurationMessage))]
[JsonSerializable(typeof(SetBaudMessage))]
[JsonSerializable(typeof(SetBaudResultMessage))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(DisconnectMessage))]
[JsonSerializable(typeof(OperatorStatusMessage))]
[JsonSerializable(typeof(SessionPingMessage))]
[JsonSerializable(typeof(SessionPingResultMessage))]
[JsonSerializable(typeof(ControlMessage))]
[JsonSerializable(typeof(ConnectorHelloPayload))]
[JsonSerializable(typeof(SerialConfigPayload))]
[JsonSerializable(typeof(SetBaudPayload))]
[JsonSerializable(typeof(SetBaudResultPayload))]
[JsonSerializable(typeof(ErrorPayload))]
[JsonSerializable(typeof(SessionPingResultPayload))]
internal partial class ProtocolJsonContext : JsonSerializerContext
{
}
