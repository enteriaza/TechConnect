// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialControlMapping.cs
//
// Purpose:
// Maps between local SerialPortConfiguration and TechConnect.Protocol control
// payloads, including fail-closed enum parsing for SetBaud requests.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using TechConnect.Protocol;
using TechConnect.Client.Models;

namespace TechConnect.Client.Transport;

/// <summary>
/// Converts between local serial configuration models and protocol control payloads.
/// </summary>
internal static class SerialControlMapping
{
    /// <summary>
    /// Projects a local configuration into a protocol <see cref="SerialConfigPayload"/>.
    /// </summary>
    /// <param name="configuration">Local serial port settings.</param>
    /// <returns>Payload with enum fields rendered as invariant strings.</returns>
    public static SerialConfigPayload ToPayload(SerialPortConfiguration configuration) =>
        new()
        {
            BaudRate = configuration.BaudRate,
            DataBits = configuration.DataBits,
            Parity = configuration.Parity.ToString(),
            StopBits = configuration.StopBits.ToString(),
            Handshake = configuration.Handshake.ToString()
        };

    /// <summary>
    /// Returns a copy of <paramref name="current"/> with only the baud rate replaced.
    /// </summary>
    /// <param name="current">Existing configuration.</param>
    /// <param name="baudRate">New baud rate.</param>
    /// <returns>Updated configuration value.</returns>
    public static SerialPortConfiguration WithBaudRate(SerialPortConfiguration current, int baudRate) =>
        new()
        {
            BaudRate = baudRate,
            DataBits = current.DataBits,
            Parity = current.Parity,
            StopBits = current.StopBits,
            Handshake = current.Handshake
        };

    /// <summary>
    /// Builds a configuration from a SetBaud / SetBaudPayload request.
    /// </summary>
    /// <remarks>Fails closed on invalid parity, stop-bits, or handshake enum strings.</remarks>
    /// <param name="baudRate">Requested baud rate.</param>
    /// <param name="dataBits">Requested data bits.</param>
    /// <param name="parity">Parity name (case-insensitive).</param>
    /// <param name="stopBits">Stop-bits name (case-insensitive); <see cref="StopBits.None"/> is rejected.</param>
    /// <param name="handshake">Handshake name (case-insensitive).</param>
    /// <param name="configuration">Parsed configuration when the method returns <see langword="true"/>.</param>
    /// <param name="error">Failure reason when the method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when all fields parse to defined enum values.</returns>
    public static bool TryFromSetBaud(
        int baudRate,
        int dataBits,
        string parity,
        string stopBits,
        string handshake,
        out SerialPortConfiguration configuration,
        out string? error)
    {
        configuration = default!;
        error = null;

        if (!TryParseParity(parity, out var parsedParity))
        {
            error = $"Parity '{parity}' is not supported.";
            return false;
        }

        if (!TryParseStopBits(stopBits, out var parsedStopBits))
        {
            error = $"StopBits '{stopBits}' is not supported.";
            return false;
        }

        if (!TryParseHandshake(handshake, out var parsedHandshake))
        {
            error = $"Handshake '{handshake}' is not supported.";
            return false;
        }

        configuration = new SerialPortConfiguration
        {
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = parsedParity,
            StopBits = parsedStopBits,
            Handshake = parsedHandshake
        };
        return true;
    }

    /// <summary>
    /// Parses a parity string into a defined <see cref="Parity"/> value.
    /// </summary>
    /// <param name="value">Case-insensitive enum name.</param>
    /// <param name="parity">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds and the value is defined.</returns>
    public static bool TryParseParity(string value, out Parity parity) =>
        Enum.TryParse(value, ignoreCase: true, out parity) && Enum.IsDefined(parity);

    /// <summary>
    /// Parses a stop-bits string into a defined <see cref="StopBits"/> value other than <see cref="StopBits.None"/>.
    /// </summary>
    /// <param name="value">Case-insensitive enum name.</param>
    /// <param name="stopBits">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds and the value is usable.</returns>
    public static bool TryParseStopBits(string value, out StopBits stopBits) =>
        Enum.TryParse(value, ignoreCase: true, out stopBits) &&
        Enum.IsDefined(stopBits) &&
        stopBits != StopBits.None;

    /// <summary>
    /// Parses a handshake string into a defined <see cref="Handshake"/> value.
    /// </summary>
    /// <param name="value">Case-insensitive enum name.</param>
    /// <param name="handshake">Parsed value when successful.</param>
    /// <returns><see langword="true"/> when parsing succeeds and the value is defined.</returns>
    public static bool TryParseHandshake(string value, out Handshake handshake) =>
        Enum.TryParse(value, ignoreCase: true, out handshake) && Enum.IsDefined(handshake);
}
