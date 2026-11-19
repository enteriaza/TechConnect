// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ScpCopyFileSource.cs
//
// Purpose:
// Outbound SCP sink (remote → local) over Microsoft.DevTunnels.Ssh with
// per-connection host-key confirmation and streaming file payload bytes.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Copy;

/// <summary>
/// SCP sink (remote → local) over an outbound SSH session using Microsoft.DevTunnels.Ssh.
/// Streams file payload bytes without buffering the entire remote file.
/// Server host keys require per-connection operator confirmation (never persisted).
/// </summary>
/// <remarks>
/// Authentication order: server host-key confirmation first, then password auth
/// (URL password or interactive prompt after approval). Host-key approval is not
/// remembered for later connections. Content length comes from the SCP <c>C</c>
/// header when valid. Payload bytes are opaque binary.
/// </remarks>
internal sealed class ScpCopyFileSource : ICopyFileSource
{
    /// <summary>Maximum time allowed for operator host-key confirmation.</summary>
    public static readonly TimeSpan HostKeyConfirmationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Limited-length payload stream from the SCP channel.</summary>
    private readonly Stream _stream;

    /// <summary>Owns SSH client/session/channel for dispose.</summary>
    private readonly IAsyncDisposable _resources;

    /// <summary>
    /// Constructs a source wrapping a length-limited SCP payload stream.
    /// </summary>
    /// <param name="displayName">Credential-free display URI.</param>
    /// <param name="length">File size from the SCP header.</param>
    /// <param name="stream">Payload stream.</param>
    /// <param name="resources">SSH resources disposed with the source.</param>
    private ScpCopyFileSource(string displayName, long? length, Stream stream, IAsyncDisposable resources)
    {
        DisplayName = displayName;
        ContentLength = length;
        _stream = stream;
        _resources = resources;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public long? ContentLength { get; }

    /// <inheritdoc />
    public Stream Content => _stream;

    /// <summary>
    /// Opens an outbound SSH session, confirms the host key, authenticates, and starts SCP <c>-f</c>.
    /// </summary>
    /// <param name="source">Parsed SCP source with host, path, and username.</param>
    /// <param name="promptPasswordAsync">
    /// Password prompt used only when <see cref="CopySourceUri.Password"/> is empty,
    /// and only after host-key approval.
    /// </param>
    /// <param name="confirmHostKeyAsync">
    /// Per-connection host-key confirmation; approval is not persisted.
    /// </param>
    /// <param name="logger">Logger using <see cref="CopySourceUri.DisplayUri"/> only.</param>
    /// <param name="cancellationToken">Cancels connect/auth/header read.</param>
    /// <returns>Streaming source limited to the advertised SCP file length.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Kind is not SCP or host is missing.</exception>
    /// <exception cref="InvalidOperationException">
    /// Missing path/username/password, host-key rejection, auth failure, or SCP protocol error.
    /// </exception>
    public static async Task<ICopyFileSource> OpenAsync(
        CopySourceUri source,
        Func<string, CancellationToken, Task<string?>> promptPasswordAsync,
        Func<ScpHostKeyInfo, CancellationToken, Task<bool>> confirmHostKeyAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(promptPasswordAsync);
        ArgumentNullException.ThrowIfNull(confirmHostKeyAsync);
        if (source.Kind != CopySourceKind.Scp || string.IsNullOrWhiteSpace(source.Host))
        {
            throw new ArgumentException("SCP source with host required.", nameof(source));
        }

        var remotePath = (source.Path ?? string.Empty).TrimStart('/');
        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw new InvalidOperationException("% SCP remote filename is required.");
        }

        var username = source.Username;
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new InvalidOperationException("% SCP username is required.");
        }

        // Password may come from the URL; otherwise prompt only AFTER host-key approval.
        var password = source.Password;

        var port = source.Port ?? 22;
        var config = new SshSessionConfiguration();
        config.AuthenticationMethods.Clear();
        config.AuthenticationMethods.Add(AuthenticationMethods.Password);

        var client = new SshClient(config, new TraceSource("TechConnectScp", SourceLevels.Off));
        SshClientSession? session = null;
        SshChannel? channel = null;
        try
        {
            session = await client.OpenSessionAsync(source.Host, port).ConfigureAwait(false);

            var hostKeyAccepted = false;
            session.Authenticating += (_, e) =>
            {
                if (e.AuthenticationType != SshAuthenticationType.ServerPublicKey)
                {
                    // Client-side session: only host-key verification uses this path before user auth.
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(null);
                    return;
                }

                e.AuthenticationTask = ConfirmServerHostKeyAsync(
                    e,
                    source.Host,
                    port,
                    confirmHostKeyAsync,
                    accepted => hostKeyAccepted = accepted,
                    cancellationToken);
            };

            // Authenticate the server (host key) before any user credentials are sent.
            var serverOk = await session.AuthenticateServerAsync(cancellationToken).ConfigureAwait(false);
            if (!serverOk || !hostKeyAccepted)
            {
                throw new InvalidOperationException("% SCP connection rejected by operator.");
            }

            if (string.IsNullOrEmpty(password))
            {
                password = await promptPasswordAsync("Password: ", cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrEmpty(password))
                {
                    throw new InvalidOperationException("% SCP password is required.");
                }
            }

            var credentials = new SshClientCredentials(username, password);
            password = null;
            var ok = await session.AuthenticateClientAsync(credentials, cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
            {
                throw new InvalidOperationException("% SCP authentication failed.");
            }

            channel = await session.OpenChannelAsync().ConfigureAwait(false);
            if (remotePath.IndexOfAny(['\0', '\n', '\r', '"', '`']) >= 0)
            {
                throw new InvalidOperationException("% Invalid SCP remote path.");
            }

            var exec = new CommandRequestMessage("scp -f " + remotePath);
            var accepted = await channel.RequestAsync(exec).ConfigureAwait(false);
            if (!accepted)
            {
                throw new InvalidOperationException("% Remote host rejected SCP request.");
            }

            var sshStream = new SshStream(channel);
            var (length, filename) = await ReadScpHeaderAsync(sshStream, cancellationToken)
                .ConfigureAwait(false);
            _ = filename;

            await sshStream.WriteAsync(new byte[] { 0 }, cancellationToken).ConfigureAwait(false);

            var limited = new ScpPayloadStream(sshStream, length);
            var resources = new ScpResources(client, session, channel, sshStream);
            session = null;
            channel = null;
            client = null!;

            return new ScpCopyFileSource(source.DisplayUri, length, limited, resources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not InvalidOperationException)
        {
            logger.LogWarning(ex, "SCP copy failed for {Uri}", source.DisplayUri);
            throw new InvalidOperationException("% Unable to retrieve remote file over SCP.", ex);
        }
        finally
        {
            if (channel is not null)
            {
                try { channel.Dispose(); } catch { /* ignore */ }
            }

            if (session is not null)
            {
                try
                {
                    await session.CloseAsync(SshDisconnectReason.ByApplication, "scp failed")
                        .ConfigureAwait(false);
                }
                catch
                {
                    session.Dispose();
                }
            }

            client?.Dispose();
        }
    }

    /// <summary>
    /// Confirms the presented server public key with the operator within the confirmation timeout.
    /// </summary>
    /// <param name="e">SSH authenticating event for server public key.</param>
    /// <param name="host">Expected host.</param>
    /// <param name="port">SSH port.</param>
    /// <param name="confirmHostKeyAsync">Operator confirmation callback.</param>
    /// <param name="setAccepted">Records whether confirmation succeeded.</param>
    /// <param name="cancellationToken">Linked with the confirmation timeout.</param>
    /// <returns>
    /// A principal when approved; null when rejected, timed out, or key missing.
    /// </returns>
    private static async Task<ClaimsPrincipal?> ConfirmServerHostKeyAsync(
        SshAuthenticatingEventArgs e,
        string host,
        int port,
        Func<ScpHostKeyInfo, CancellationToken, Task<bool>> confirmHostKeyAsync,
        Action<bool> setAccepted,
        CancellationToken cancellationToken)
    {
        setAccepted(false);
        if (e.PublicKey is null)
        {
            return null;
        }

        var info = new ScpHostKeyInfo
        {
            Host = host,
            Port = port,
            Algorithm = e.PublicKey.KeyAlgorithmName,
            Fingerprint = SshKeyMaterial.ComputeFingerprint(e.PublicKey)
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(HostKeyConfirmationTimeout);

        bool approved;
        try
        {
            approved = await confirmHostKeyAsync(info, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            setAccepted(false);
            return null;
        }

        if (!approved)
        {
            setAccepted(false);
            return null;
        }

        setAccepted(true);
        return new ClaimsPrincipal(new ClaimsIdentity("scp-host-confirmed"));
    }

    /// <summary>
    /// Reads the SCP file header line (<c>Cmmmm size filename</c>) or remote error.
    /// </summary>
    /// <param name="stream">SCP channel stream.</param>
    /// <param name="cancellationToken">Cancels the line read.</param>
    /// <returns>Advertised file length and filename.</returns>
    /// <exception cref="InvalidOperationException">Empty, error, or malformed header.</exception>
    private static async Task<(long Length, string Filename)> ReadScpHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (line.Length == 0)
        {
            throw new InvalidOperationException("% Empty SCP response.");
        }

        if (line[0] is (byte)1 or (byte)2)
        {
            var msg = Encoding.UTF8.GetString(line.AsSpan(1));
            var safe = new string(msg.Where(c => c is >= ' ' and <= '~').ToArray());
            if (safe.Length > 120)
            {
                safe = safe[..120];
            }

            throw new InvalidOperationException("% SCP remote error: " + safe);
        }

        var text = Encoding.ASCII.GetString(line);
        if (text.Length < 3 || text[0] != 'C')
        {
            throw new InvalidOperationException("% Unexpected SCP response.");
        }

        var parts = text.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
            size < 0)
        {
            throw new InvalidOperationException("% Invalid SCP file header.");
        }

        return (size, parts[2].TrimEnd('\r'));
    }

    /// <summary>
    /// Reads bytes until newline or 4096-byte cap (newline not included in the result).
    /// </summary>
    /// <param name="stream">Source stream.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Line bytes excluding the terminating newline.</returns>
    private static async Task<byte[]> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(128);
        var one = new byte[1];
        while (buffer.Count < 4096)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            buffer.Add(one[0]);
        }

        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _resources.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Owns the SSH client, session, channel, and stream for orderly SCP teardown.
    /// </summary>
    private sealed class ScpResources : IAsyncDisposable
    {
        /// <summary>Outbound SSH client.</summary>
        private readonly SshClient _client;

        /// <summary>Authenticated client session.</summary>
        private readonly SshClientSession _session;

        /// <summary>Exec channel running <c>scp -f</c>.</summary>
        private readonly SshChannel _channel;

        /// <summary>Channel stream used for SCP protocol bytes.</summary>
        private readonly SshStream _stream;

        /// <summary>
        /// Captures ownership of SSH resources for later dispose.
        /// </summary>
        /// <param name="client">SSH client.</param>
        /// <param name="session">Client session.</param>
        /// <param name="channel">Open channel.</param>
        /// <param name="stream">Channel stream.</param>
        public ScpResources(SshClient client, SshClientSession session, SshChannel channel, SshStream stream)
        {
            _client = client;
            _session = session;
            _channel = channel;
            _stream = stream;
        }

        /// <summary>
        /// Sends a final ACK byte when possible, then closes stream, channel, session, and client.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await _stream.WriteAsync(new byte[] { 0 }).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
            try { _channel.Dispose(); } catch { /* ignore */ }
            try
            {
                await _session.CloseAsync(SshDisconnectReason.ByApplication, "scp complete")
                    .ConfigureAwait(false);
            }
            catch
            {
                try { _session.Dispose(); } catch { /* ignore */ }
            }

            try { _client.Dispose(); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// Read-only stream that stops after the SCP-advertised byte count (binary payload only).
    /// </summary>
    private sealed class ScpPayloadStream : Stream
    {
        /// <summary>Underlying SSH channel stream.</summary>
        private readonly Stream _inner;

        /// <summary>Total payload bytes advertised by the SCP header.</summary>
        private readonly long _length;

        /// <summary>Bytes already returned to callers.</summary>
        private long _read;

        /// <summary>
        /// Limits reads from <paramref name="inner"/> to <paramref name="length"/> bytes.
        /// </summary>
        /// <param name="inner">SSH stream positioned at file payload.</param>
        /// <param name="length">Exact payload length from the SCP header.</param>
        public ScpPayloadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _length;

        /// <inheritdoc />
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var remaining = _length - _read;
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var n = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _read += n;
            return n;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
