// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortConfigurationValidator.cs
//
// Purpose:
// Validates SerialPortConfiguration ranges/enums and formats safe log strings.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using TechConnect.Client.Models;

namespace TechConnect.Client.Services;

/// <summary>
/// Static validation and logging helpers for <see cref="SerialPortConfiguration"/>.
/// </summary>
/// <remarks>
/// Validation is application-level only: passing validation does not guarantee the
/// serial driver or attached device will accept the settings. Thread-safe.
/// </remarks>
internal static class SerialPortConfigurationValidator
{
    /// <summary>
    /// Throws when <paramref name="configuration"/> is outside supported ranges or enums.
    /// </summary>
    /// <param name="configuration">Configuration to validate; must not be <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A field is outside the supported set.</exception>
    /// <remarks>
    /// Baud 50–4_000_000; data bits 5–8; parity must be defined;
    /// stop bits must be One / Two / OnePointFive (not None); handshake must be defined.
    /// </remarks>
    public static void Validate(SerialPortConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.BaudRate is < 50 or > 4_000_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.BaudRate,
                "BaudRate must be between 50 and 4000000.");
        }

        if (configuration.DataBits is < 5 or > 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.DataBits,
                "DataBits must be between 5 and 8.");
        }

        if (!Enum.IsDefined(configuration.Parity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.Parity,
                "Parity value is not supported.");
        }

        if (!Enum.IsDefined(configuration.StopBits) || configuration.StopBits == StopBits.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.StopBits,
                "StopBits must be One, Two, or OnePointFive.");
        }

        if (!Enum.IsDefined(configuration.Handshake))
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.Handshake,
                "Handshake value is not supported.");
        }
    }

    /// <summary>
    /// Formats framing fields for structured logs without payload or credential content.
    /// </summary>
    /// <param name="configuration">Configuration to summarize.</param>
    /// <returns>Compact key=value string of baud, data bits, parity, stop bits, and handshake.</returns>
    public static string ToLogString(SerialPortConfiguration configuration) =>
        $"BaudRate={configuration.BaudRate}, DataBits={configuration.DataBits}, Parity={configuration.Parity}, StopBits={configuration.StopBits}, Handshake={configuration.Handshake}";
}
