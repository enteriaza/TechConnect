// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialPortFriendlyNameLookup.cs
//
// Purpose:
// Resolves Windows friendly device descriptions for COM ports via WMI.
//
// -----------------------------------------------------------------------------

using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Resolves Windows friendly device descriptions for COM ports via WMI.
/// Failures are non-fatal; missing entries simply omit a description.
/// </summary>
/// <remarks>
/// <para>
/// Primary source is <c>Win32_SerialPort</c>; ports still missing are attempted via
/// a narrowed <c>Win32_PnPEntity</c> query (USB/Bluetooth/AMT edge cases). Neither
/// query opens a serial handle or proves device readiness.
/// </para>
/// <para>
/// Honors <see cref="CancellationToken"/> between WMI result rows. Concurrent calls
/// each open their own WMI searchers. Partial results may be returned when a
/// secondary query fails after a successful primary query.
/// </para>
/// </remarks>
internal sealed partial class SerialPortFriendlyNameLookup : ISerialPortFriendlyNameLookup
{
    /// <summary>Logger for non-fatal WMI failures.</summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes with a non-generic logger (test-friendly).
    /// </summary>
    /// <param name="logger">Structured logger.</param>
    public SerialPortFriendlyNameLookup(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes with a typed logger for DI registration.
    /// </summary>
    /// <param name="logger">Typed structured logger.</param>
    public SerialPortFriendlyNameLookup(ILogger<SerialPortFriendlyNameLookup> logger)
        : this((ILogger)logger)
    {
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> GetFriendlyNames(
        IReadOnlyCollection<string> portNames,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (portNames.Count == 0)
        {
            return map;
        }

        try
        {
            AddFromSerialPortClass(map, cancellationToken);

            var missing = portNames
                .Where(name => !map.ContainsKey(name))
                .ToArray();

            if (missing.Length > 0)
            {
                AddFromPnPEntities(map, missing, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to query Windows for serial port friendly names");
        }

        return map;
    }

    /// <summary>
    /// Populates <paramref name="map"/> from <c>Win32_SerialPort</c> DeviceID/Name/Description.
    /// </summary>
    /// <param name="map">Destination keyed by COM name.</param>
    /// <param name="cancellationToken">Checked per result row.</param>
    private void AddFromSerialPortClass(
        IDictionary<string, string> map,
        CancellationToken cancellationToken)
    {
        try
        {
            using var searcher = CreateSearcher("SELECT DeviceID, Name, Description FROM Win32_SerialPort");
            using var results = searcher.Get();

            foreach (ManagementBaseObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (item)
                {
                    var deviceId = item["DeviceID"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(deviceId))
                    {
                        continue;
                    }

                    var description =
                        FirstNonEmpty(item["Description"]?.ToString(), item["Name"]?.ToString());
                    if (string.IsNullOrWhiteSpace(description))
                    {
                        continue;
                    }

                    var cleaned = CleanFriendlyName(description, deviceId);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        map[deviceId] = cleaned;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Win32_SerialPort friendly-name query failed");
        }
    }

    /// <summary>
    /// Best-effort fill for ports missing from <c>Win32_SerialPort</c> via PnP captions.
    /// </summary>
    /// <param name="map">Destination keyed by COM name.</param>
    /// <param name="missingPortNames">COM names still unresolved.</param>
    /// <param name="cancellationToken">Checked per result row.</param>
    private void AddFromPnPEntities(
        IDictionary<string, string> map,
        IReadOnlyList<string> missingPortNames,
        CancellationToken cancellationToken)
    {
        try
        {
            // Narrow query used only for ports missing from Win32_SerialPort (USB/Bluetooth/AMT edge cases).
            using var searcher = CreateSearcher(
                "SELECT Name, Caption FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            using var results = searcher.Get();

            var missing = new HashSet<string>(missingPortNames, StringComparer.OrdinalIgnoreCase);

            foreach (ManagementBaseObject item in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (missing.Count == 0)
                {
                    break;
                }

                using (item)
                {
                    var caption = FirstNonEmpty(item["Name"]?.ToString(), item["Caption"]?.ToString());
                    if (string.IsNullOrWhiteSpace(caption))
                    {
                        continue;
                    }

                    if (!TryParseCaption(caption, out var portName, out var friendlyName))
                    {
                        continue;
                    }

                    if (!missing.Contains(portName) || map.ContainsKey(portName))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(friendlyName))
                    {
                        map[portName] = friendlyName;
                        missing.Remove(portName);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Win32_PnPEntity friendly-name query failed");
        }
    }

    /// <summary>
    /// Creates a non-rewindable, return-immediately WMI searcher against root\cimv2.
    /// </summary>
    /// <param name="query">WQL select statement.</param>
    /// <returns>Configured <see cref="ManagementObjectSearcher"/>.</returns>
    private static ManagementObjectSearcher CreateSearcher(string query) =>
        new(
            new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery(query),
            new System.Management.EnumerationOptions
            {
                Rewindable = false,
                ReturnImmediately = true
            });

    /// <summary>
    /// Parses a PnP caption of the form <c>Device Name (COMn)</c>.
    /// </summary>
    /// <param name="caption">Raw Name/Caption string.</param>
    /// <param name="portName">Uppercased <c>COMn</c> when parsing succeeds.</param>
    /// <param name="friendlyName">Device text with the COM suffix removed.</param>
    /// <returns><see langword="true"/> when a COM token was extracted.</returns>
    internal static bool TryParseCaption(string caption, out string portName, out string friendlyName)
    {
        portName = string.Empty;
        friendlyName = string.Empty;

        var match = ComCaptionRegex().Match(caption);
        if (!match.Success)
        {
            return false;
        }

        portName = match.Groups["port"].Value.ToUpperInvariant();
        friendlyName = CleanFriendlyName(match.Groups["name"].Value, portName);
        return !string.IsNullOrWhiteSpace(portName);
    }

    /// <summary>
    /// Strips trailing <c>(COMn)</c> suffixes and discards names that equal the port alone.
    /// </summary>
    /// <param name="value">Raw description or caption fragment.</param>
    /// <param name="portName">Associated COM name used for equality checks.</param>
    /// <returns>Cleaned friendly text, or empty when there is nothing useful beyond the port name.</returns>
    private static string CleanFriendlyName(string value, string portName)
    {
        var cleaned = value.Trim();
        cleaned = ComSuffixRegex().Replace(cleaned, string.Empty).Trim();
        cleaned = cleaned.TrimEnd('-', '—', ' ').Trim();

        if (cleaned.Equals(portName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return cleaned;
    }

    /// <summary>
    /// Returns the first non-blank trimmed string among <paramref name="values"/>.
    /// </summary>
    /// <param name="values">Candidate strings.</param>
    /// <returns>First useful value, or <see langword="null"/>.</returns>
    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>Matches <c>name (COMn)</c> captions from PnP entities.</summary>
    [GeneratedRegex(@"^(?<name>.*)\((?<port>COM\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComCaptionRegex();

    /// <summary>Removes a trailing <c>(COMn)</c> suffix from a description.</summary>
    [GeneratedRegex(@"\s*\(COM\d+\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComSuffixRegex();
}
