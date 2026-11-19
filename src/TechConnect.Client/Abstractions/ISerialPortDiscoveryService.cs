// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialPortDiscoveryService.cs
//
// Purpose:
// Enumerates available COM ports and optionally enriches them with Windows
// friendly names for technician UI selection.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Models;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Discovers available COM ports for technician selection.
/// </summary>
/// <remarks>
/// Friendly-name enrichment may use WMI and can block; UI code should prefer
/// <see cref="GetAvailablePortsAsync"/>. Enumeration failures surface as
/// <see cref="InvalidOperationException"/> from the sync path.
/// </remarks>
public interface ISerialPortDiscoveryService
{
    /// <summary>
    /// Enumerates available COM ports and enriches them with Windows friendly names when possible.
    /// </summary>
    /// <returns>
    /// Ports ordered by COM name. Friendly names may be null when WMI lookup fails;
    /// discovery still returns port names.
    /// </returns>
    /// <remarks>
    /// Prefer <see cref="GetAvailablePortsAsync"/> from UI code so WMI work does not block the UI thread.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Underlying port enumeration failed.</exception>
    IReadOnlyList<SerialPortInfo> GetAvailablePorts();

    /// <summary>
    /// Asynchronously enumerates available COM ports (WMI-friendly for UI threads).
    /// </summary>
    /// <param name="cancellationToken">Token that cancels enumeration / friendly-name lookup.</param>
    /// <returns>Ports ordered by COM name, same shape as <see cref="GetAvailablePorts"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="InvalidOperationException">Underlying port enumeration failed.</exception>
    Task<IReadOnlyList<SerialPortInfo>> GetAvailablePortsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns whether <paramref name="portName"/> is present in the current enumeration.
    /// </summary>
    /// <param name="portName">COM port name to test (comparison is ordinal-ignore-case after trim).</param>
    /// <returns>
    /// <see langword="true"/> when the port is listed; <see langword="false"/> when empty,
    /// not found, or enumeration fails.
    /// </returns>
    bool IsPortAvailable(string portName);
}
