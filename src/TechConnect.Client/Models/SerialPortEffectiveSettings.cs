// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortEffectiveSettings.cs
//
// Purpose:
// Captures effective settings read from the live serial-port instance after
// Open (not merely the requested configuration) for diagnostics.
//
// -----------------------------------------------------------------------------

using System.IO.Ports;
using System.Text;

namespace TechConnect.Client.Models;

/// <summary>
/// Effective settings read from the live serial-port instance after Open.
/// </summary>
/// <remarks>
/// Reflects driver-reported values, not merely the requested
/// <see cref="SerialPortConfiguration"/>. Safe to log via
/// <see cref="ToDiagnosticString"/> (no payloads or secrets).
/// </remarks>
public sealed class SerialPortEffectiveSettings
{
    /// <summary>
    /// COM port name.
    /// </summary>
    public string PortName { get; init; } = string.Empty;

    /// <summary>
    /// Whether the port reported open when settings were captured.
    /// </summary>
    public bool IsOpen { get; init; }

    /// <summary>
    /// Effective baud rate.
    /// </summary>
    public int BaudRate { get; init; }

    /// <summary>
    /// Effective data bits.
    /// </summary>
    public int DataBits { get; init; }

    /// <summary>
    /// Effective parity.
    /// </summary>
    public Parity Parity { get; init; }

    /// <summary>
    /// Effective stop bits.
    /// </summary>
    public StopBits StopBits { get; init; }

    /// <summary>
    /// Effective handshake mode.
    /// </summary>
    public Handshake Handshake { get; init; }

    /// <summary>
    /// Effective DTR line state.
    /// </summary>
    public bool DtrEnable { get; init; }

    /// <summary>
    /// Effective RTS line state.
    /// </summary>
    public bool RtsEnable { get; init; }

    /// <summary>
    /// Effective read timeout in milliseconds.
    /// </summary>
    public int ReadTimeout { get; init; }

    /// <summary>
    /// Effective write timeout in milliseconds.
    /// </summary>
    public int WriteTimeout { get; init; }

    /// <summary>
    /// Effective driver input buffer size in bytes.
    /// </summary>
    public int ReadBufferSize { get; init; }

    /// <summary>
    /// Effective driver output buffer size in bytes.
    /// </summary>
    public int WriteBufferSize { get; init; }

    /// <summary>
    /// Formats all settings as a single comma-separated diagnostic string.
    /// </summary>
    /// <returns>Log-safe key=value list with no payload bytes.</returns>
    public string ToDiagnosticString()
    {
        var sb = new StringBuilder();
        sb.Append("PortName=").Append(PortName);
        sb.Append(", IsOpen=").Append(IsOpen);
        sb.Append(", BaudRate=").Append(BaudRate);
        sb.Append(", DataBits=").Append(DataBits);
        sb.Append(", Parity=").Append(Parity);
        sb.Append(", StopBits=").Append(StopBits);
        sb.Append(", Handshake=").Append(Handshake);
        sb.Append(", DtrEnable=").Append(DtrEnable);
        sb.Append(", RtsEnable=").Append(RtsEnable);
        sb.Append(", ReadTimeout=").Append(ReadTimeout);
        sb.Append(", WriteTimeout=").Append(WriteTimeout);
        sb.Append(", ReadBufferSize=").Append(ReadBufferSize);
        sb.Append(", WriteBufferSize=").Append(WriteBufferSize);
        return sb.ToString();
    }
}
