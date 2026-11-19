// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialPortFriendlyNameLookup.cs
//
// Purpose:
// Resolves Windows-friendly device descriptions for COM port names (WMI-backed
// in production) without failing discovery when lookup is unavailable.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Resolves Windows-friendly device descriptions for COM ports.
/// </summary>
/// <remarks>
/// Failures are expected to be non-fatal at the discovery layer: missing entries
/// simply omit a description. Prefer calling from a background thread because
/// WMI work can block.
/// </remarks>
internal interface ISerialPortFriendlyNameLookup
{
    /// <summary>
    /// Looks up friendly device descriptions for the supplied COM port names.
    /// </summary>
    /// <param name="portNames">Port names to resolve (for example <c>COM3</c>).</param>
    /// <param name="cancellationToken">Token that can cancel WMI enumeration.</param>
    /// <returns>
    /// Case-insensitive map of port name to friendly description. Ports without a
    /// resolved description are omitted.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    IReadOnlyDictionary<string, string> GetFriendlyNames(
        IReadOnlyCollection<string> portNames,
        CancellationToken cancellationToken);
}
