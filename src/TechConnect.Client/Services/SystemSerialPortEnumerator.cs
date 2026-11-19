// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SystemSerialPortEnumerator.cs
//
// Purpose:
// Enumerates Windows COM port names via SerialPort.GetPortNames().
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Production <see cref="ISerialPortEnumerator"/> using <see cref="SerialPort.GetPortNames"/>.
/// </summary>
/// <remarks>
/// <para>
/// Returns names Windows currently exposes as serial ports. Presence in the list
/// does not guarantee the port can be opened or that a device is attached.
/// </para>
/// <para>
/// Thread-safe for concurrent calls; each call performs a fresh OS query.
/// Friendly descriptions are resolved separately by
/// <see cref="SerialPortFriendlyNameLookup"/>.
/// </para>
/// </remarks>
internal sealed class SystemSerialPortEnumerator : ISerialPortEnumerator
{
    /// <inheritdoc />
    public string[] GetPortNames() => SerialPort.GetPortNames();
}
