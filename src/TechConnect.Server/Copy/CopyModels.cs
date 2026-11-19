// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CopyModels.cs
//
// Purpose:
// Shared copy-subsystem DTOs, source-kind enum, and file-source contracts used by
// parsers, factories, and the serial transfer path.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Copy;

/// <summary>
/// Supported <c>copy</c> source schemes.
/// </summary>
internal enum CopySourceKind
{
    /// <summary>Plain HTTP GET source.</summary>
    Http = 1,

    /// <summary>HTTPS GET source.</summary>
    Https = 2,

    /// <summary>TFTP RRQ (RFC 1350) source.</summary>
    Tftp = 3,

    /// <summary>SCP over outbound SSH (password auth).</summary>
    Scp = 4,

    /// <summary>XMODEM receive from the operator SSH terminal.</summary>
    Xmodem = 5,

    /// <summary>YMODEM receive from the operator SSH terminal.</summary>
    Ymodem = 6
}

/// <summary>
/// Parsed copy source with credentials separated from the display/log URL.
/// </summary>
/// <remarks>
/// <see cref="DisplayUri"/> must never include a cleartext password.
/// <see cref="Username"/> and <see cref="Password"/> are retained for auth only
/// and must not be written to logs or command history.
/// </remarks>
internal sealed class CopySourceUri
{
    /// <summary>Scheme/kind of the copy source.</summary>
    public required CopySourceKind Kind { get; init; }

    /// <summary>URL safe for display/logs (credentials removed).</summary>
    public required string DisplayUri { get; init; }

    /// <summary>Host for network sources (null for modem schemes).</summary>
    public string? Host { get; init; }

    /// <summary>Explicit port when non-default; null means scheme default.</summary>
    public int? Port { get; init; }

    /// <summary>
    /// Remote path or filename. For SCP the leading slash is stripped;
    /// for HTTP(S)/TFTP paths typically begin with <c>/</c>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Optional username extracted from userinfo or interactive completion.</summary>
    public string? Username { get; init; }

    /// <summary>
    /// Optional password from URL userinfo or later prompt.
    /// Never include in <see cref="DisplayUri"/>, logs, or history.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// True when the source is XMODEM or YMODEM (no network host).
    /// </summary>
    public bool IsModem => Kind is CopySourceKind.Xmodem or CopySourceKind.Ymodem;
}

/// <summary>
/// Dispatch payload for an authorized <c>copy</c> CLI action.
/// </summary>
internal sealed class CopyRequest
{
    /// <summary>Parsed (or interactively completed) source.</summary>
    public required CopySourceUri Source { get; init; }

    /// <summary>Target connector serial attachment session id (<c>serial:&lt;id&gt;</c>).</summary>
    public required string SessionId { get; init; }
}

/// <summary>
/// Outcome of streaming payload bytes into the connector serial write path.
/// </summary>
/// <remarks>
/// <see cref="Success"/> with a positive <see cref="BytesWrittenToSerialTransport"/>
/// confirms transport delivery only — not device firmware acceptance or flash success.
/// </remarks>
internal sealed class CopyTransferResult
{
    /// <summary>True when the full source stream was written without error or cancel.</summary>
    public required bool Success { get; init; }

    /// <summary>Operator-facing status message (may start with <c>%</c> on failure).</summary>
    public required string Message { get; init; }

    /// <summary>Count of raw payload bytes handed to the serial writer.</summary>
    public long BytesWrittenToSerialTransport { get; init; }

    /// <summary>Wall-clock duration of the transfer attempt.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>True when the caller cancellation token aborted the transfer.</summary>
    public bool Cancelled { get; init; }
}

/// <summary>Opened source stream of raw file payload bytes.</summary>
/// <remarks>
/// Implementations must expose binary payload as-is — never decode as text.
/// <see cref="ContentLength"/> may be null when the protocol has no size metadata
/// (classic XMODEM, TFTP) or when size is not yet known at open time.
/// </remarks>
internal interface ICopyFileSource : IAsyncDisposable
{
    /// <summary>Safe display name (typically <see cref="CopySourceUri.DisplayUri"/>).</summary>
    string DisplayName { get; }

    /// <summary>Advertised content length when known; otherwise null.</summary>
    long? ContentLength { get; }

    /// <summary>Readable stream of raw payload bytes.</summary>
    Stream Content { get; }
}

/// <summary>
/// Opens network (non-modem) copy sources for transfer.
/// </summary>
internal interface ICopyFileSourceFactory
{
    /// <summary>
    /// Opens a file source for the given parsed URI.
    /// </summary>
    /// <param name="source">Parsed source; modem kinds must not be opened here.</param>
    /// <param name="promptPasswordAsync">
    /// Interactive password prompt used by SCP when URL password is absent
    /// (invoked only after host-key approval).
    /// </param>
    /// <param name="confirmScpHostKeyAsync">
    /// Per-connection SCP host-key confirmation; required for SCP, ignored otherwise.
    /// Approval is not persisted across connections.
    /// </param>
    /// <param name="cancellationToken">Cancels open/connect.</param>
    /// <returns>An opened <see cref="ICopyFileSource"/> owning any network resources.</returns>
    Task<ICopyFileSource> OpenAsync(
        CopySourceUri source,
        Func<string, CancellationToken, Task<string?>> promptPasswordAsync,
        Func<ScpHostKeyInfo, CancellationToken, Task<bool>>? confirmScpHostKeyAsync,
        CancellationToken cancellationToken);
}
