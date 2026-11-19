// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SystemSerialPortFactory.cs
//
// Purpose:
// Creates production ISerialPort instances backed by System.IO.Ports.SerialPort.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Default <see cref="ISerialPortFactory"/> that returns <see cref="SystemSerialPort"/>.
/// </summary>
/// <remarks>
/// Each call creates a new closed port adapter. The caller owns disposal of the
/// returned instance. This factory is thread-safe; individual ports are not shared.
/// </remarks>
internal sealed class SystemSerialPortFactory : ISerialPortFactory
{
    /// <inheritdoc />
    public ISerialPort Create(string portName) => new SystemSerialPort(portName);
}
