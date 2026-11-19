// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialPort.cs
//
// Purpose:
// Testable seam over System.IO.Ports.SerialPort for open/close, line settings,
// buffer sizes, modem-control lines, and BaseStream I/O ownership.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Thin serial-port seam over <see cref="System.IO.Ports.SerialPort"/> for deterministic testing.
/// </summary>
/// <remarks>
/// <para>
/// Production wraps a real <see cref="System.IO.Ports.SerialPort"/>. The owner that
/// created the instance (typically via <see cref="ISerialPortFactory"/>) is responsible
/// for <see cref="IDisposable.Dispose"/>; disposing closes the port.
/// </para>
/// <para>
/// Concurrent use of a single instance is not supported unless the caller serializes
/// access. Settings should be applied before <see cref="Open"/>; some drivers reset
/// modem-control lines on open, so callers may re-assert <see cref="DtrEnable"/> /
/// <see cref="RtsEnable"/> afterward.
/// </para>
/// </remarks>
internal interface ISerialPort : IDisposable
{
    /// <summary>
    /// COM port name assigned at construction (for example <c>COM3</c>).
    /// </summary>
    string PortName { get; }

    /// <summary>
    /// Whether the underlying port handle is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Baud rate in bits per second.
    /// </summary>
    int BaudRate { get; set; }

    /// <summary>
    /// Number of data bits per frame (typically 7 or 8).
    /// </summary>
    int DataBits { get; set; }

    /// <summary>
    /// Parity mode applied to each frame.
    /// </summary>
    Parity Parity { get; set; }

    /// <summary>
    /// Stop-bit configuration.
    /// </summary>
    StopBits StopBits { get; set; }

    /// <summary>
    /// Hardware or software handshake mode.
    /// </summary>
    Handshake Handshake { get; set; }

    /// <summary>
    /// Data Terminal Ready modem-control line state.
    /// </summary>
    bool DtrEnable { get; set; }

    /// <summary>
    /// Request To Send modem-control line state.
    /// </summary>
    bool RtsEnable { get; set; }

    /// <summary>
    /// Read timeout in milliseconds for synchronous reads on the port API.
    /// </summary>
    /// <remarks>
    /// Async consumers typically read via <see cref="BaseStream"/> and may not rely on this value.
    /// </remarks>
    int ReadTimeout { get; set; }

    /// <summary>
    /// Write timeout in milliseconds for synchronous writes on the port API.
    /// </summary>
    int WriteTimeout { get; set; }

    /// <summary>
    /// Driver input buffer size in bytes.
    /// </summary>
    int ReadBufferSize { get; set; }

    /// <summary>
    /// Driver output buffer size in bytes.
    /// </summary>
    int WriteBufferSize { get; set; }

    /// <summary>
    /// Bytes still held in the serial driver output queue (not yet clocked onto the wire).
    /// </summary>
    /// <remarks>
    /// Used after write/flush to wait for driver drain before counting transmitted bytes.
    /// Does not prove device-level acceptance of the data.
    /// </remarks>
    int BytesToWrite { get; }

    /// <summary>
    /// Underlying byte stream for asynchronous read/write while the port is open.
    /// </summary>
    /// <remarks>
    /// Owned by the serial-port instance; callers must not dispose it separately.
    /// Accessing the stream when the port is closed may throw.
    /// </remarks>
    Stream BaseStream { get; }

    /// <summary>
    /// Opens the port with the currently configured settings.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The port is in use or access is denied.</exception>
    /// <exception cref="IOException">The port cannot be opened.</exception>
    /// <exception cref="ArgumentException">Settings are invalid for the driver.</exception>
    void Open();

    /// <summary>
    /// Closes the port if open. Safe to call when already closed.
    /// </summary>
    void Close();
}
