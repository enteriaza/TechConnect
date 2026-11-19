// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    LogDirectoryResolver.cs
//
// Purpose:
// Resolves the rolling Serilog file directory from explicit config, the
// repository .artifacts tree, or LocalApplicationData as a fallback.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Hosting;

/// <summary>
/// Resolves the on-disk directory used for TechConnect.Client rolling log files.
/// </summary>
internal static class LogDirectoryResolver
{
    /// <summary>
    /// Resolves the rolling log directory.
    /// </summary>
    /// <remarks>
    /// Prefers an explicit config path; otherwise repository
    /// <c>.artifacts/TechConnect.Client/logs</c> when the repo root is discoverable,
    /// else <c>%LocalAppData%\TechConnect.Client\logs</c>.
    /// </remarks>
    /// <param name="configuredDirectory">Optional path from file-logging options (<c>Directory</c>).</param>
    /// <param name="contentRoot">Application content root used for relative paths and repo walk.</param>
    /// <returns>Absolute directory path (not created by this method).</returns>
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
            return Path.Combine(repoRoot, ".artifacts", "TechConnect.Client", "logs");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "TechConnect.Client", "logs");
    }

    /// <summary>
    /// Walks parents of <paramref name="startDirectory"/> looking for <c>TechConnect.slnx</c> or <c>.git</c>.
    /// </summary>
    /// <param name="startDirectory">Directory to begin the upward search from.</param>
    /// <returns>Repository root path, or <see langword="null"/> when not found.</returns>
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
