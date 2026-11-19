// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CopyFileSourceFactory.cs
//
// Purpose:
// Dispatches network copy sources (HTTP/HTTPS, TFTP, SCP) to the matching
// ICopyFileSource opener; rejects modem kinds that use the SSH receive path.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Copy;

/// <summary>
/// Default <see cref="ICopyFileSourceFactory"/> that opens HTTP(S), TFTP, or SCP sources.
/// </summary>
/// <remarks>
/// XMODEM/YMODEM must be opened via <see cref="ModemCopyFileSource"/> on the SSH
/// session path; this factory throws if those kinds are requested.
/// SCP requires a non-null host-key confirmation callback.
/// </remarks>
internal sealed class CopyFileSourceFactory : ICopyFileSourceFactory
{
    /// <summary>Creates category loggers for each transport.</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes the factory with a logger factory for transport-specific loggers.
    /// </summary>
    /// <param name="loggerFactory">Logger factory; must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is null.</exception>
    public CopyFileSourceFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// SCP without host-key confirmation, modem kind, or unsupported kind.
    /// </exception>
    public Task<ICopyFileSource> OpenAsync(
        CopySourceUri source,
        Func<string, CancellationToken, Task<string?>> promptPasswordAsync,
        Func<ScpHostKeyInfo, CancellationToken, Task<bool>>? confirmScpHostKeyAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Kind switch
        {
            CopySourceKind.Http or CopySourceKind.Https => HttpCopyFileSource.OpenAsync(
                source,
                _loggerFactory.CreateLogger("TechConnect.Copy.Http"),
                cancellationToken),
            CopySourceKind.Tftp => TftpCopyFileSource.OpenAsync(
                source,
                _loggerFactory.CreateLogger("TechConnect.Copy.Tftp"),
                cancellationToken),
            CopySourceKind.Scp => ScpCopyFileSource.OpenAsync(
                source,
                promptPasswordAsync,
                confirmScpHostKeyAsync
                ?? throw new InvalidOperationException(
                    "% SCP host-key confirmation is required."),
                _loggerFactory.CreateLogger("TechConnect.Copy.Scp"),
                cancellationToken),
            CopySourceKind.Xmodem or CopySourceKind.Ymodem =>
                throw new InvalidOperationException(
                    "% Modem sources must be opened via the SSH session receive path."),
            _ => throw new InvalidOperationException("% Unsupported copy source.")
        };
    }
}
