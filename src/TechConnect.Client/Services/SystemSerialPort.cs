// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SystemSerialPort.cs
//
// Purpose:
// Thin ISerialPort adapter over System.IO.Ports.SerialPort; owns and disposes
// the underlying port instance.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Production <see cref="ISerialPort"/> wrapping <see cref="SerialPort"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type owns the wrapped <see cref="SerialPort"/> for its lifetime and
/// disposes it from <see cref="Dispose"/>. Callers that create instances via
/// <see cref="SystemSerialPortFactory"/> are responsible for disposing the
/// returned <see cref="ISerialPort"/>.
/// </para>
/// <para>
/// Thread-safety matches <see cref="SerialPort"/>: concurrent open/close/read/write
/// on the same instance is not supported by this adapter.
/// </para>
/// <para>
/// Property getters/setters and <see cref="Open"/> / <see cref="Close"/> forward
/// directly to the driver wrapper. Successful <see cref="Open"/> does not prove
/// that a remote serial device is present or responding — only that Windows
/// accepted the open of the named COM port.
/// </para>
/// </remarks>
internal sealed class SystemSerialPort : ISerialPort
{
    /// <summary>
    /// Owned <see cref="SerialPort"/> instance for the configured COM name.
    /// </summary>
    private readonly SerialPort _port;

    /// <summary>
    /// Creates a closed port bound to <paramref name="portName"/>.
    /// </summary>
    /// <param name="portName">Windows COM port name (for example <c>COM3</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="portName"/> is null, empty, or whitespace.</exception>
    public SystemSerialPort(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("A COM port name is required.", nameof(portName));
        }

        _port = new SerialPort(portName);
    }

    /// <inheritdoc />
    public string PortName => _port.PortName;

    /// <inheritdoc />
    public bool IsOpen => _port.IsOpen;

    /// <inheritdoc />
    public int BaudRate
    {
        get => _port.BaudRate;
        set => _port.BaudRate = value;
    }

    /// <inheritdoc />
    public int DataBits
    {
        get => _port.DataBits;
        set => _port.DataBits = value;
    }

    /// <inheritdoc />
    public Parity Parity
    {
        get => _port.Parity;
        set => _port.Parity = value;
    }

    /// <inheritdoc />
    public StopBits StopBits
    {
        get => _port.StopBits;
        set => _port.StopBits = value;
    }

    /// <inheritdoc />
    public Handshake Handshake
    {
        get => _port.Handshake;
        set => _port.Handshake = value;
    }

    /// <inheritdoc />
    public bool DtrEnable
    {
        get => _port.DtrEnable;
        set => _port.DtrEnable = value;
    }

    /// <inheritdoc />
    public bool RtsEnable
    {
        get => _port.RtsEnable;
        set => _port.RtsEnable = value;
    }

    /// <inheritdoc />
    public int ReadTimeout
    {
        get => _port.ReadTimeout;
        set => _port.ReadTimeout = value;
    }

    /// <inheritdoc />
    public int WriteTimeout
    {
        get => _port.WriteTimeout;
        set => _port.WriteTimeout = value;
    }

    /// <inheritdoc />
    public int ReadBufferSize
    {
        get => _port.ReadBufferSize;
        set => _port.ReadBufferSize = value;
    }

    /// <inheritdoc />
    public int WriteBufferSize
    {
        get => _port.WriteBufferSize;
        set => _port.WriteBufferSize = value;
    }

    /// <summary>
    /// Bytes still held in the serial driver output queue (not yet shifted out by the UART/driver).
    /// </summary>
    /// <remarks>
    /// A zero value means the driver reports an empty output queue. It does not prove that a
    /// remote device received or processed the bytes.
    /// </remarks>
    public int BytesToWrite => _port.BytesToWrite;

    /// <summary>
    /// Stream used for asynchronous read/write against the open port.
    /// </summary>
    /// <remarks>
    /// Ownership remains with this adapter / the wrapped <see cref="SerialPort"/>.
    /// Callers must not dispose <see cref="BaseStream"/> independently.
    /// </remarks>
    public Stream BaseStream => _port.BaseStream;

    /// <inheritdoc />
    public void Open() => _port.Open();

    /// <inheritdoc />
    public void Close() => _port.Close();

    /// <summary>
    /// Disposes the owned <see cref="SerialPort"/> (closes the handle if still open).
    /// </summary>
    public void Dispose() => _port.Dispose();
}
