// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortInfo.cs
//
// Purpose:
// Display-friendly serial port descriptor for technician UI selection without
// exposing System.IO.Ports types.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Models;

/// <summary>
/// Display-friendly serial port descriptor.
/// </summary>
/// <remarks>
/// Does not expose <c>System.IO.Ports</c> types. Equality is not overridden;
/// compare <see cref="PortName"/> when needed.
/// </remarks>
public sealed class SerialPortInfo
{
    /// <summary>
    /// COM port name (for example <c>COM3</c>).
    /// </summary>
    public required string PortName { get; init; }

    /// <summary>
    /// Windows device description when available; otherwise null or empty.
    /// </summary>
    public string? FriendlyName { get; init; }

    /// <summary>
    /// Human-readable dropdown text.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="PortName"/> alone when no friendly name is present;
    /// returns the friendly name alone when it already contains the port name;
    /// otherwise formats <c>COMn — friendly</c>.
    /// </remarks>
    /// <value>Example: <c>COM3 — Intel(R) Active Management Technology - SOL</c>.</value>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(FriendlyName))
            {
                return PortName;
            }

            var friendly = FriendlyName.Trim();
            if (friendly.Contains(PortName, StringComparison.OrdinalIgnoreCase))
            {
                return friendly;
            }

            return $"{PortName} — {friendly}";
        }
    }

    /// <summary>
    /// Returns <see cref="DisplayName"/> for UI binding and logging.
    /// </summary>
    /// <returns>The same string as <see cref="DisplayName"/>.</returns>
    public override string ToString() => DisplayName;
}
