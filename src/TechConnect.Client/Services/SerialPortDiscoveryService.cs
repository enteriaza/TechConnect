// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortDiscoveryService.cs
//
// Purpose:
// Discovers available COM ports and optionally attaches Windows friendly names.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;

namespace TechConnect.Client.Services;

/// <summary>
/// Enumerates COM ports and merges optional WMI-friendly descriptions for UI display.
/// </summary>
/// <remarks>
/// <para>
/// Port names come from <see cref="ISerialPortEnumerator"/>; friendly names from
/// <see cref="ISerialPortFriendlyNameLookup"/>. Enumeration success does not imply
/// a port can be opened or that hardware is attached.
/// </para>
/// <para>
/// <see cref="GetAvailablePortsAsync"/> runs work on the thread pool so WMI does not
/// block a WinForms UI thread. Concurrent calls are supported; each performs a fresh query.
/// Cancellation is honored during friendly-name lookup; enumeration failures surface as
/// <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
internal sealed class SerialPortDiscoveryService : ISerialPortDiscoveryService
{
    /// <summary>Logger for discovery and friendly-name fallback warnings.</summary>
    private readonly ILogger<SerialPortDiscoveryService> _logger;

    /// <summary>OS port-name enumerator (typically <see cref="SystemSerialPortEnumerator"/>).</summary>
    private readonly ISerialPortEnumerator _portEnumerator;

    /// <summary>Optional WMI friendly-name resolver.</summary>
    private readonly ISerialPortFriendlyNameLookup _friendlyNameLookup;

    /// <summary>
    /// Initializes discovery with enumerator and friendly-name dependencies.
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    /// <param name="portEnumerator">Source of COM port names.</param>
    /// <param name="friendlyNameLookup">Source of Windows device descriptions.</param>
    public SerialPortDiscoveryService(
        ILogger<SerialPortDiscoveryService> logger,
        ISerialPortEnumerator portEnumerator,
        ISerialPortFriendlyNameLookup friendlyNameLookup)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _portEnumerator = portEnumerator ?? throw new ArgumentNullException(nameof(portEnumerator));
        _friendlyNameLookup = friendlyNameLookup ?? throw new ArgumentNullException(nameof(friendlyNameLookup));
    }

    /// <inheritdoc />
    public IReadOnlyList<SerialPortInfo> GetAvailablePorts() =>
        GetAvailablePorts(CancellationToken.None);

    /// <inheritdoc />
    /// <remarks>
    /// Offloads to a thread-pool work item (<c>Task.Run</c>) because WMI queries can be costly on the UI thread.
    /// </remarks>
    public Task<IReadOnlyList<SerialPortInfo>> GetAvailablePortsAsync(CancellationToken cancellationToken = default) =>
        // WMI is cheaper than blocking the WinForms UI thread; System.Management is MTA-safe here.
        Task.Run(() => GetAvailablePorts(cancellationToken), cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see langword="false"/> for blank names or when enumeration throws
    /// <see cref="InvalidOperationException"/>.
    /// </remarks>
    public bool IsPortAvailable(string portName)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        try
        {
            return EnumeratePortNames()
                .Any(name => string.Equals(name, portName.Trim(), StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the sorted port list with best-effort friendly names.
    /// </summary>
    /// <param name="cancellationToken">Cancels during friendly-name WMI work.</param>
    /// <returns>Discovered ports ordered by <see cref="ComPortNameComparer"/>.</returns>
    private IReadOnlyList<SerialPortInfo> GetAvailablePorts(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var portNames = EnumeratePortNames()
            .OrderBy(static name => name, ComPortNameComparer.Instance)
            .ToArray();

        IReadOnlyDictionary<string, string> friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (portNames.Length > 0)
        {
            try
            {
                friendlyNames = _friendlyNameLookup.GetFriendlyNames(portNames, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Serial port friendly-name lookup failed for {PortCount} COM port(s); displaying port names only",
                    portNames.Length);
                friendlyNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            if (friendlyNames.Count == 0)
            {
                _logger.LogWarning(
                    "No Windows friendly names were resolved for {PortCount} COM port(s); displaying port names only",
                    portNames.Length);
            }
        }

        var ports = portNames
            .Select(name =>
            {
                friendlyNames.TryGetValue(name, out var friendly);
                if (string.IsNullOrWhiteSpace(friendly))
                {
                    friendly = null;
                }

                return new SerialPortInfo
                {
                    PortName = name,
                    FriendlyName = friendly
                };
            })
            .ToArray();

        _logger.LogInformation(
            "Serial port discovery found {PortCount} port(s): {Ports}",
            ports.Length,
            ports.Length == 0
                ? "(none)"
                : string.Join(", ", ports.Select(static p => p.DisplayName)));

        return ports;
    }

    /// <summary>
    /// Queries the enumerator and normalizes/deduplicates non-blank names.
    /// </summary>
    /// <returns>Distinct trimmed COM names (ordinal ignore-case uniqueness).</returns>
    /// <exception cref="InvalidOperationException">Underlying enumeration failed.</exception>
    private string[] EnumeratePortNames()
    {
        try
        {
            return _portEnumerator.GetPortNames()
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial port discovery failed");
            throw new InvalidOperationException("Unable to enumerate serial ports.", ex);
        }
    }
}
