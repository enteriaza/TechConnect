// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PlatformRoutingTableInspector.cs
//
// Purpose:
// Selects the platform-specific IRoutingTableInspector (Windows/Linux/macOS)
// or an unsupported stub for other hosts.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Inspection;

/// <summary>Selects the platform-specific <see cref="IRoutingTableInspector"/> implementation.</summary>
/// <remarks>
/// Windows → <see cref="WindowsRoutingTableInspector"/>;
/// Linux → <see cref="LinuxRoutingTableInspector"/>;
/// macOS → <see cref="MacRoutingTableInspector"/>;
/// otherwise → <see cref="UnsupportedRoutingTableInspector"/>.
/// </remarks>
internal sealed class PlatformRoutingTableInspector : IRoutingTableInspector
{
    /// <summary>Delegating platform inspector.</summary>
    private readonly IRoutingTableInspector _inner;

    /// <summary>
    /// Creates the platform-appropriate inspector using <paramref name="loggerFactory"/>.
    /// </summary>
    /// <param name="loggerFactory">Logger factory for the selected implementation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    public PlatformRoutingTableInspector(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _inner = Create(loggerFactory);
    }

    /// <summary>
    /// Test constructor that injects a prebuilt inner inspector.
    /// </summary>
    /// <param name="inner">Inspector to delegate to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    internal PlatformRoutingTableInspector(IRoutingTableInspector inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public RoutingTableSnapshot GetRoutes() => _inner.GetRoutes();

    /// <summary>
    /// Creates the platform-specific inspector for the current OS.
    /// </summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <returns>Platform inspector instance.</returns>
    internal static IRoutingTableInspector Create(ILoggerFactory loggerFactory)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsRoutingTableInspector(
                loggerFactory.CreateLogger<WindowsRoutingTableInspector>());
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxRoutingTableInspector(
                loggerFactory.CreateLogger<LinuxRoutingTableInspector>());
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacRoutingTableInspector(
                loggerFactory.CreateLogger<MacRoutingTableInspector>());
        }

        return new UnsupportedRoutingTableInspector();
    }
}

/// <summary>
/// Stub inspector used when the host OS has no routing table implementation.
/// </summary>
internal sealed class UnsupportedRoutingTableInspector : IRoutingTableInspector
{
    /// <inheritdoc />
    /// <returns>Snapshot with <see cref="RoutingTableSnapshot.UnavailableReason"/> set.</returns>
    public RoutingTableSnapshot GetRoutes() =>
        new()
        {
            UnavailableReason = "Routing table inspection is not available on this platform."
        };
}
