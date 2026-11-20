// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AppsettingsConfigurationFileStore.cs
//
// Purpose:
// Reads and atomically replaces the content-root appsettings.json used as the
// startup configuration by the SSH configuration CLI.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Hosting;

namespace TechConnect.Server.Configuration.Management;

/// <summary>
/// Persistence seam for the startup configuration file.
/// </summary>
internal interface IConfigurationFileStore
{
    /// <summary>Absolute path of the startup configuration file.</summary>
    string FilePath { get; }

    /// <summary>Reads the whole file, or returns <see langword="null"/> when it does not exist.</summary>
    byte[]? ReadAllBytes();

    /// <summary>
    /// Replaces the file atomically. On failure or cancellation the original file is left intact.
    /// </summary>
    /// <param name="content">Complete new file content.</param>
    /// <param name="cancellationToken">Cancels before the replace step only.</param>
    Task WriteAtomicallyAsync(byte[] content, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IConfigurationFileStore"/> over <c>{ContentRoot}/appsettings.json</c>.
/// </summary>
/// <remarks>
/// The path is fixed at construction and never derived from operator input. Writes go to a
/// uniquely named temporary file in the same directory, are flushed to disk, and then replace
/// the target with <see cref="File.Replace(string, string, string?, bool)"/> (or a move when the
/// target is absent), so readers observe either the old or the new complete file.
/// </remarks>
internal sealed class AppsettingsConfigurationFileStore : IConfigurationFileStore
{
    /// <summary>Optional test hook invoked with the temp path immediately before the replace step.</summary>
    private readonly Action<string>? _beforeReplace;

    /// <summary>
    /// Creates a store for the host content root.
    /// </summary>
    /// <param name="environment">Host environment supplying the content root.</param>
    public AppsettingsConfigurationFileStore(IHostEnvironment environment)
        : this((environment ?? throw new ArgumentNullException(nameof(environment))).ContentRootPath)
    {
    }

    /// <summary>
    /// Creates a store for an explicit content root (tests).
    /// </summary>
    /// <param name="contentRoot">Directory containing <c>appsettings.json</c>.</param>
    /// <param name="beforeReplace">Optional hook run before the replace step (tests inject failures).</param>
    internal AppsettingsConfigurationFileStore(string contentRoot, Action<string>? beforeReplace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        FilePath = Path.GetFullPath(Path.Combine(contentRoot, AppsettingsHostKeyStore.BaseFileName));
        _beforeReplace = beforeReplace;
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public byte[]? ReadAllBytes() => File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;

    /// <inheritdoc />
    public async Task WriteAtomicallyAsync(byte[] content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(FilePath)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _beforeReplace?.Invoke(tempPath);

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup; the target file is unaffected.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the target file is unaffected.
            }
        }
    }
}
