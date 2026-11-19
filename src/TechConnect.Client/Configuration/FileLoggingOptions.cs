// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    FileLoggingOptions.cs
//
// Purpose:
// Binds Logging:File options for Serilog file sink directory, file-name prefix,
// retention count, and size limit.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Configuration;

/// <summary>
/// Options for TechConnect.Client rolling file logging.
/// </summary>
/// <remarks>
/// Bound under <see cref="SectionName"/>. Directory resolution may fall back to
/// repository <c>.artifacts/TechConnect.Client/logs</c> or
/// <c>%LocalAppData%\TechConnect.Client\logs</c> when <see cref="Directory"/> is empty.
/// </remarks>
public sealed class FileLoggingOptions
{
    /// <summary>
    /// Configuration section name: <c>Logging:File</c>.
    /// </summary>
    public const string SectionName = "Logging:File";

    /// <summary>
    /// Directory that receives rolling log files.
    /// </summary>
    /// <remarks>
    /// When empty, defaults to repository <c>.artifacts/TechConnect.Client/logs</c> when discoverable,
    /// otherwise <c>%LocalAppData%\TechConnect.Client\logs</c>.
    /// </remarks>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// Prefix for rolling log file names (for example <c>techconnect.client</c>).
    /// </summary>
    /// <value>Defaults to <c>techconnect.client</c>; required non-whitespace by the validator.</value>
    public string FileNamePrefix { get; set; } = "techconnect.client";

    /// <summary>
    /// Maximum number of retained rolled log files.
    /// </summary>
    /// <value>Defaults to 14; must be at least 1.</value>
    public int RetainedFileCountLimit { get; set; } = 14;

    /// <summary>
    /// Maximum size in bytes of a single log file before rolling.
    /// </summary>
    /// <value>Defaults to 10 MiB; must be at least 1024.</value>
    public long FileSizeLimitBytes { get; set; } = 10 * 1024 * 1024;
}
