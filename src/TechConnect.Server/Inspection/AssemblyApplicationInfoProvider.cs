// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AssemblyApplicationInfoProvider.cs
//
// Purpose:
// Builds ApplicationInfo from the TechConnect.Server assembly metadata and a
// TimeProvider-based start time for deterministic uptime in tests.
//
// -----------------------------------------------------------------------------

using System.Reflection;
using TechConnect.Protocol;

namespace TechConnect.Server.Inspection;

/// <summary>
/// <see cref="IApplicationInfoProvider"/> that reads assembly attributes and tracks start time.
/// </summary>
/// <remarks>
/// <see cref="_startedAtUtc"/> is fixed at construction (or injected for tests).
/// Uptime is <c>now − started</c>, clamped to non-negative if the clock moves backwards.
/// </remarks>
internal sealed class AssemblyApplicationInfoProvider : IApplicationInfoProvider
{
    /// <summary>Clock used for uptime calculation.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>UTC start time for this provider instance.</summary>
    private readonly DateTimeOffset _startedAtUtc;

    /// <summary>
    /// Initializes the provider, capturing the current UTC time as process start.
    /// </summary>
    /// <param name="timeProvider">Clock for uptime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is null.</exception>
    public AssemblyApplicationInfoProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedAtUtc = timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Test / DI helper: pin started-at for deterministic uptime.
    /// </summary>
    /// <param name="timeProvider">Clock for uptime.</param>
    /// <param name="startedAtUtc">Fixed start timestamp.</param>
    /// <exception cref="ArgumentNullException"><paramref name="timeProvider"/> is null.</exception>
    internal AssemblyApplicationInfoProvider(TimeProvider timeProvider, DateTimeOffset startedAtUtc)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startedAtUtc = startedAtUtc;
    }

    /// <inheritdoc />
    public ApplicationInfo GetInfo()
    {
        var assembly = typeof(AssemblyApplicationInfoProvider).Assembly;
        var name = assembly.GetName();
        var version = name.Version?.ToString() ?? "unknown";
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
                      ?? name.Name
                      ?? "TechConnect.Server";

        var now = _timeProvider.GetUtcNow();
        var uptime = now - _startedAtUtc;
        if (uptime < TimeSpan.Zero)
        {
            uptime = TimeSpan.Zero;
        }

        return new ApplicationInfo
        {
            ApplicationName = product,
            Version = version,
            InformationalVersion = informational,
            RuntimeVersion = Environment.Version.ToString(),
            ProtocolVersion = ControlProtocol.Version,
            StartedAtUtc = _startedAtUtc,
            Uptime = uptime
        };
    }
}
