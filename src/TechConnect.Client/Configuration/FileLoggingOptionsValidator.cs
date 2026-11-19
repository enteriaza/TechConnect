// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    FileLoggingOptionsValidator.cs
//
// Purpose:
// Startup validation for Logging:File options (prefix, retention, size limit)
// without logging secrets.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;

namespace TechConnect.Client.Configuration;

/// <summary>
/// Startup validator for <see cref="FileLoggingOptions"/>.
/// </summary>
/// <remarks>
/// Does not require <see cref="FileLoggingOptions.Directory"/> to be set; empty
/// directory is resolved at host startup. Does not log secrets.
/// </remarks>
public sealed class FileLoggingOptionsValidator : IValidateOptions<FileLoggingOptions>
{
    /// <summary>
    /// Validates the bound <see cref="FileLoggingOptions"/>.
    /// </summary>
    /// <param name="name">Options name (unused).</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Success, or a failure listing all collected messages.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ValidateOptionsResult Validate(string? name, FileLoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.FileNamePrefix))
        {
            failures.Add("Logging:File:FileNamePrefix is required.");
        }

        if (options.RetainedFileCountLimit < 1)
        {
            failures.Add("Logging:File:RetainedFileCountLimit must be at least 1.");
        }

        if (options.FileSizeLimitBytes < 1024)
        {
            failures.Add("Logging:File:FileSizeLimitBytes must be at least 1024.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
