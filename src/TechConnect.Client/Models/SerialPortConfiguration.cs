// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortConfiguration.cs
//
// Purpose:
// Holds mutable serial communication parameters applied locally when instructed
// by an authorized remote session (technicians do not configure these manually).
//
// -----------------------------------------------------------------------------

using System.IO.Ports;

namespace TechConnect.Client.Models;

/// <summary>
/// Serial communication parameters applied to the physical port.
/// </summary>
/// <remarks>
/// Applied locally when instructed by an authorized remote session.
/// Field technicians never configure these values manually.
/// Defaults match common console settings (115200 8N1, no handshake).
/// </remarks>
public sealed class SerialPortConfiguration
{
    /// <summary>
    /// Baud rate in bits per second.
    /// </summary>
    /// <value>Defaults to 115200.</value>
    public int BaudRate { get; init; } = 115200;

    /// <summary>
    /// Number of data bits per frame.
    /// </summary>
    /// <value>Defaults to 8.</value>
    public int DataBits { get; init; } = 8;

    /// <summary>
    /// Parity mode.
    /// </summary>
    /// <value>Defaults to <see cref="Parity.None"/>.</value>
    public Parity Parity { get; init; } = Parity.None;

    /// <summary>
    /// Stop-bit configuration.
    /// </summary>
    /// <value>Defaults to <see cref="StopBits.One"/>.</value>
    public StopBits StopBits { get; init; } = StopBits.One;

    /// <summary>
    /// Hardware or software handshake mode.
    /// </summary>
    /// <value>Defaults to <see cref="Handshake.None"/>.</value>
    public Handshake Handshake { get; init; } = Handshake.None;

    /// <summary>
    /// Creates a configuration with the type's default property values.
    /// </summary>
    /// <returns>A new default <see cref="SerialPortConfiguration"/>.</returns>
    public static SerialPortConfiguration CreateDefault() => new();
}
