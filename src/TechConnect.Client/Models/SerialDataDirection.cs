// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialDataDirection.cs
//
// Purpose:
// Distinguishes RX (device→host) and TX (host→device) serial traffic chunks for
// UI display and stats.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Direction of a <see cref="SerialDataChunk"/> relative to the field host.
/// </summary>
public enum SerialDataDirection
{
    /// <summary>
    /// Data received from the physical serial device (RX).
    /// </summary>
    Rx = 0,

    /// <summary>
    /// Data written to the physical serial device (TX).
    /// </summary>
    Tx = 1
}
