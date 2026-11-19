// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialPortFactory.cs
//
// Purpose:
// Creates ISerialPort instances for a named COM port so serial ownership can be
// tested without constructing System.IO.Ports.SerialPort directly.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Creates <see cref="ISerialPort"/> instances.
/// </summary>
/// <remarks>
/// Production wraps <see cref="System.IO.Ports.SerialPort"/>. The caller owns the
/// returned instance and must dispose it.
/// </remarks>
internal interface ISerialPortFactory
{
    /// <summary>
    /// Creates a closed serial-port wrapper for <paramref name="portName"/>.
    /// </summary>
    /// <param name="portName">COM port name (for example <c>COM3</c>).</param>
    /// <returns>A new <see cref="ISerialPort"/> that is not yet open.</returns>
    /// <exception cref="ArgumentException"><paramref name="portName"/> is empty or whitespace.</exception>
    ISerialPort Create(string portName);
}
