// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ApplicationInfo.cs
//
// Purpose:
// Defines the ApplicationInfo snapshot and IApplicationInfoProvider contract
// used by NOC version/uptime display.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Inspection;

/// <summary>
/// Immutable process/application identity and uptime snapshot for NOC display.
/// </summary>
internal sealed class ApplicationInfo
{
    /// <summary>Product / assembly display name.</summary>
    public required string ApplicationName { get; init; }

    /// <summary>Assembly version string.</summary>
    public required string Version { get; init; }

    /// <summary>Optional informational version when distinct from <see cref="Version"/>.</summary>
    public string? InformationalVersion { get; init; }

    /// <summary>.NET runtime version string from <see cref="Environment.Version"/>.</summary>
    public required string RuntimeVersion { get; init; }

    /// <summary>TechConnect control protocol version constant.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>UTC time when the provider instance was constructed (process start proxy).</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Elapsed time since <see cref="StartedAtUtc"/> (clamped to non-negative).</summary>
    public required TimeSpan Uptime { get; init; }
}

/// <summary>
/// Supplies the current <see cref="ApplicationInfo"/> snapshot.
/// </summary>
internal interface IApplicationInfoProvider
{
    /// <summary>
    /// Returns a fresh application identity and uptime snapshot.
    /// </summary>
    /// <returns>Current application info.</returns>
    ApplicationInfo GetInfo();
}
