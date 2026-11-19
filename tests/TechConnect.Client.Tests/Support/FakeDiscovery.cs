// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    FakeDiscovery.cs
//
// Purpose:
// In-memory fakes for serial-port enumeration and friendly-name lookup used by
// discovery-service unit tests. Does not touch real COM ports or WMI.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Controllable <see cref="ISerialPortEnumerator"/> that returns a fixed port-name list
/// or throws a configured exception.
/// </summary>
/// <remarks>
/// Limitation: does not simulate OS reordering, hot-plug races, or real SerialPort.GetPortNames semantics beyond the injected list.
/// </remarks>
internal sealed class FakeSerialPortEnumerator : ISerialPortEnumerator
{
    /// <summary>
    /// Port names returned by GetPortNames.
    /// </summary>
    private string[] _ports;

    /// <summary>
    /// Creates an enumerator seeded with the given port names.
    /// </summary>
    /// <param name="ports">Initial port names returned by <see cref="GetPortNames"/>.</param>
    public FakeSerialPortEnumerator(params string[] ports)
    {
        _ports = ports;
    }

    /// <summary>
    /// When set, <see cref="GetPortNames"/> throws this exception instead of returning ports.
    /// </summary>
    public Exception? ThrowOnEnumerate { get; set; }

    /// <summary>
    /// Replaces the port-name list returned by subsequent enumerations.
    /// </summary>
    /// <param name="ports">New port names.</param>
    public void SetPorts(params string[] ports) => _ports = ports;

    /// <summary>
    /// Returns a copy of the configured port names, or throws <see cref="ThrowOnEnumerate"/> when set.
    /// </summary>
    public string[] GetPortNames()
    {
        if (ThrowOnEnumerate is not null)
        {
            throw ThrowOnEnumerate;
        }

        return _ports.ToArray();
    }
}

/// <summary>
/// Controllable <see cref="ISerialPortFriendlyNameLookup"/> backed by an in-memory dictionary.
/// </summary>
/// <remarks>
/// Limitation: never queries WMI/SetupAPI; missing map entries simply omit friendly names.
/// </remarks>
internal sealed class FakeFriendlyNameLookup : ISerialPortFriendlyNameLookup
{
    /// <summary>
    /// Port-name to friendly-name map.
    /// </summary>
    private readonly Dictionary<string, string> _map;
    /// <summary>
    /// When true, GetFriendlyNames throws.
    /// </summary>
    private readonly bool _throw;

    /// <summary>
    /// Creates a lookup with an optional port→friendly-name map and optional failure mode.
    /// </summary>
    /// <param name="map">Initial friendly-name map (case-insensitive keys).</param>
    /// <param name="throwOnLookup">When true, every lookup throws <see cref="InvalidOperationException"/>.</param>
    public FakeFriendlyNameLookup(IDictionary<string, string>? map = null, bool throwOnLookup = false)
    {
        _map = new Dictionary<string, string>(map ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        _throw = throwOnLookup;
    }

    /// <summary>
    /// Number of times <see cref="GetFriendlyNames"/> has been invoked.
    /// </summary>
    public int LookupCount { get; private set; }

    /// <summary>
    /// Sets or overwrites the friendly name for a port.
    /// </summary>
    public void Set(string portName, string friendlyName) => _map[portName] = friendlyName;

    /// <summary>
    /// Clears all mapped friendly names.
    /// </summary>
    public void Clear() => _map.Clear();

    /// <summary>
    /// Returns friendly names for ports present in the map; honouring cancellation and optional throw mode.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetFriendlyNames(
        IReadOnlyCollection<string> portNames,
        CancellationToken cancellationToken)
    {
        LookupCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (_throw)
        {
            throw new InvalidOperationException("Friendly name lookup failed.");
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var port in portNames)
        {
            if (_map.TryGetValue(port, out var friendly))
            {
                result[port] = friendly;
            }
        }

        return result;
    }
}
