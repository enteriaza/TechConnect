// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    LogDirectoryResolver.cs
//
// Purpose:
// Resolves the on-disk directory for TechConnect.Server rolling file logs from
// optional configuration, repository layout, or LocalApplicationData fallback.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Hosting;

/// <summary>
/// Resolves the absolute directory used for Serilog rolling file output.
/// </summary>
/// <remarks>
/// Preference order:
/// <list type="number">
/// <item><description>Configured directory (absolute, or relative to content root).</description></item>
/// <item><description>Repository <c>.artifacts/TechConnect.Server/logs</c> when <c>TechConnect.slnx</c> or <c>.git</c> is found walking upward from the content root.</description></item>
/// <item><description><c>%LocalAppData%/TechConnect.Server/logs</c> (or the platform equivalent).</description></item>
/// </list>
/// </remarks>
internal static class LogDirectoryResolver
{
    /// <summary>
    /// Resolves the absolute log directory for the current process.
    /// </summary>
    /// <param name="configuredDirectory">
    /// Optional directory from <c>Logging:File:Directory</c>. Null, empty, or whitespace
    /// triggers repository / LocalApplicationData fallback.
    /// </param>
    /// <param name="contentRoot">Application content root used to resolve relative configured paths and walk for the repository root.</param>
    /// <returns>Absolute path to the log directory (not necessarily created).</returns>
    public static string Resolve(string? configuredDirectory, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(
                Path.IsPathRooted(configuredDirectory)
                    ? configuredDirectory
                    : Path.Combine(contentRoot, configuredDirectory));
        }

        var repoRoot = TryFindRepositoryRoot(contentRoot);
        if (repoRoot is not null)
        {
            return Path.Combine(repoRoot, ".artifacts", "TechConnect.Server", "logs");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "TechConnect.Server", "logs");
    }

    /// <summary>
    /// Walks parents of <paramref name="startDirectory"/> looking for <c>TechConnect.slnx</c> or a <c>.git</c> marker.
    /// </summary>
    /// <param name="startDirectory">Directory to begin the upward search.</param>
    /// <returns>Repository root path when found; otherwise <see langword="null"/>.</returns>
    private static string? TryFindRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            var slnx = Path.Combine(current.FullName, "TechConnect.slnx");
            var git = Path.Combine(current.FullName, ".git");
            if (File.Exists(slnx) || Directory.Exists(git) || File.Exists(git))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }
}
