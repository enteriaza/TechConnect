// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialPortEnumerator.cs
//
// Purpose:
// Enumerates available COM port names without requiring WMI, typically via
// System.IO.Ports.SerialPort.GetPortNames.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Enumerates available COM port names without requiring WMI.
/// </summary>
/// <remarks>
/// Production typically wraps <see cref="System.IO.Ports.SerialPort.GetPortNames"/>.
/// Friendly device descriptions are resolved separately via
/// <see cref="ISerialPortFriendlyNameLookup"/>.
/// </remarks>
internal interface ISerialPortEnumerator
{
    /// <summary>
    /// Returns currently known COM port names.
    /// </summary>
    /// <returns>
    /// Port name array as reported by the platform; may include duplicates or
    /// whitespace that callers should normalize.
    /// </returns>
    string[] GetPortNames();
}
