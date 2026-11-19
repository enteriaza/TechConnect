// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorDisconnectService.cs
//
// Purpose:
// Performs administrator-initiated connector teardown via the existing connection
// lifetime path, with authorization, audit, bounded Disconnect frame write, and
// cleanup observation distinct from attachment release.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Connector;

/// <summary>
/// Outcome of an administrative connector disconnect attempt.
/// </summary>
internal enum ConnectorDisconnectStatus
{
    /// <summary>Disposal completed and original ConnectionId cleanup was observed in time.</summary>
    Completed = 1,

    /// <summary>Session/connection is not live (missing, mismatched, or already gone).</summary>
    NotConnected = 2,

    /// <summary>Another teardown is already in flight or the connection is already disconnecting.</summary>
    AlreadyDisconnecting = 3,

    /// <summary>Teardown threw after starting; may or may not have committed disposal.</summary>
    Failed = 4,

    /// <summary>Operator lacked privilege for <see cref="CliCommandId.Disconnect"/>.</summary>
    Unauthorized = 5,

    /// <summary>
    /// Connection disposal was requested (and typically completed), but listener registry/tracker
    /// cleanup for the original ConnectionId was not observed within the wait window.
    /// </summary>
    CleanupIncomplete = 6
}

/// <summary>
/// Result payload returned to CLI/callers after <see cref="ConnectorDisconnectService.DisconnectAsync"/>.
/// </summary>
internal sealed class ConnectorDisconnectResult
{
    /// <summary>Disconnect outcome status.</summary>
    public required ConnectorDisconnectStatus Status { get; init; }

    /// <summary>Target session ID when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>Expected/actual connection ID involved in the attempt.</summary>
    public string? ConnectionId { get; init; }

    /// <summary>Abbreviated connector identity for display, when available.</summary>
    public string? ConnectorIdentity { get; init; }

    /// <summary>Remote endpoint for display, when available.</summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>Operator-facing status message.</summary>
    public string? Message { get; init; }

    /// <summary>True once MarkDisconnecting / disposal has been committed for this attempt.</summary>
    public bool TeardownCommitted { get; init; }
}

/// <summary>
/// Administrator-initiated connector teardown using the existing connection lifetime path.
/// </summary>
/// <remarks>
/// <para>
/// Does not own registry/attachment cleanup — that remains in the listener finally block.
/// This service marks disconnecting, best-effort writes a Disconnect control frame, disposes
/// the <see cref="ConnectorConnection"/>, then observes tracker/registry cleanup for the
/// original ConnectionId only (replacement connections do not satisfy original cleanup).
/// </para>
/// <para>
/// Caller <see cref="CancellationToken"/> applies only until teardown is committed.
/// After commit, disposal and cleanup observation ignore caller cancellation so SSH
/// disconnect cannot abandon connection cleanup. Attachment release still happens in the
/// listener path when the connection ends — distinct from this administrative trigger.
/// </para>
/// </remarks>
internal sealed class ConnectorDisconnectService
{
    /// <summary>Default time to wait for original ConnectionId cleanup after dispose.</summary>
    public static readonly TimeSpan DefaultCompletionWait = TimeSpan.FromSeconds(10);

    /// <summary>Bound for the best-effort Disconnect frame; teardown proceeds if the write stalls.</summary>
    public static readonly TimeSpan DisconnectFrameWriteTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Registry used to resolve sessions and observe unregister.</summary>
    private readonly ConnectorSessionRegistry _sessionRegistry;

    /// <summary>Tracker used to resolve live connections and observe removal.</summary>
    private readonly ConnectorConnectionTracker _connectionTracker;

    /// <summary>Authorizes <see cref="CliCommandId.Disconnect"/>.</summary>
    private readonly OperatorAuthorizationService _authorization;

    /// <summary>Audit sink for initiate/success/failure events.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Clock for cleanup wait loops.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Structured logger.</summary>
    private readonly ILogger<ConnectorDisconnectService> _logger;

    /// <summary>In-flight guard keyed by connection ID to prevent concurrent admin disconnects.</summary>
    private readonly ConcurrentDictionary<string, byte> _inFlightByConnectionId = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates the disconnect service.
    /// </summary>
    /// <param name="sessionRegistry">Session registry.</param>
    /// <param name="connectionTracker">Connection tracker.</param>
    /// <param name="authorization">Operator authorization service.</param>
    /// <param name="audit">Audit writer.</param>
    /// <param name="timeProvider">Clock for cleanup observation.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ConnectorDisconnectService(
        ConnectorSessionRegistry sessionRegistry,
        ConnectorConnectionTracker connectionTracker,
        OperatorAuthorizationService authorization,
        IAuditEventWriter audit,
        TimeProvider timeProvider,
        ILogger<ConnectorDisconnectService> logger)
    {
        _sessionRegistry = sessionRegistry ?? throw new ArgumentNullException(nameof(sessionRegistry));
        _connectionTracker = connectionTracker ?? throw new ArgumentNullException(nameof(connectionTracker));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Snapshot a live session (registry + authenticated connection). Null when not connected.
    /// </summary>
    /// <param name="sessionId">Session ID to resolve; whitespace returns null.</param>
    /// <returns>
    /// The registry session when tracked and not already disconnecting/disconnected; otherwise null.
    /// </returns>
    public ConnectorSession? TryResolveLiveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        if (!_sessionRegistry.TryGetBySessionId(sessionId.Trim(), out var session) || session is null)
        {
            return null;
        }

        if (!_connectionTracker.TryGet(session.ConnectionId, out var connection) || connection is null)
        {
            return null;
        }

        if (connection.State is ConnectorConnectionState.Disconnecting or ConnectorConnectionState.Disconnected)
        {
            return null;
        }

        return session;
    }

    /// <summary>
    /// Administratively disconnects the connector for the given session/connection pair.
    /// </summary>
    /// <param name="identity">Authenticated operator performing the disconnect.</param>
    /// <param name="sessionId">Target connector session ID.</param>
    /// <param name="expectedConnectionId">
    /// Connection ID that must still own the session; mismatches abort as not connected
    /// (never tears down a replacement connection).
    /// </param>
    /// <param name="cancellationToken">
    /// Applies only until teardown is committed. After commit, disposal and cleanup
    /// observation ignore caller cancellation so SSH disconnect cannot abandon cleanup.
    /// </param>
    /// <param name="completionWait">Optional cleanup observation window; defaults to <see cref="DefaultCompletionWait"/>.</param>
    /// <param name="disconnectFrameWriteTimeout">
    /// Optional Disconnect frame write timeout; defaults to <see cref="DisconnectFrameWriteTimeout"/>.
    /// </param>
    /// <returns>Structured result including status, abbreviated identity, and operator message.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is null.</exception>
    /// <exception cref="ArgumentException">Session or connection ID is null/whitespace.</exception>
    /// <exception cref="OperationCanceledException">
    /// Canceled before teardown commit when <paramref name="cancellationToken"/> requests cancel.
    /// </exception>
    public async Task<ConnectorDisconnectResult> DisconnectAsync(
        AuthenticatedOperator identity,
        string sessionId,
        string expectedConnectionId,
        CancellationToken cancellationToken,
        TimeSpan? completionWait = null,
        TimeSpan? disconnectFrameWriteTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConnectionId);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_authorization.TryAuthorize(identity, CliCommandId.Disconnect, out _))
        {
            return new ConnectorDisconnectResult
            {
                Status = ConnectorDisconnectStatus.Unauthorized,
                SessionId = sessionId,
                Message = OperatorAuthorizationService.PermissionDeniedMessage
            };
        }

        sessionId = sessionId.Trim();
        expectedConnectionId = expectedConnectionId.Trim();

        if (!_sessionRegistry.TryGetBySessionId(sessionId, out var session) || session is null)
        {
            return NotConnected(sessionId, expectedConnectionId);
        }

        // Never disconnect a replacement connection that reused an identity under a new session.
        if (!string.Equals(session.ConnectionId, expectedConnectionId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Administrative disconnect aborted: session {SessionId} connection changed (expected {Expected}, actual {Actual})",
                sessionId,
                expectedConnectionId,
                session.ConnectionId);
            return NotConnected(sessionId, expectedConnectionId);
        }

        if (!_connectionTracker.TryGet(expectedConnectionId, out var connection) || connection is null)
        {
            return NotConnected(sessionId, expectedConnectionId, session.ConnectorIdentity, session.RemoteEndpoint);
        }

        if (connection.State is ConnectorConnectionState.Disconnecting or ConnectorConnectionState.Disconnected)
        {
            return new ConnectorDisconnectResult
            {
                Status = ConnectorDisconnectStatus.AlreadyDisconnecting,
                SessionId = sessionId,
                ConnectionId = expectedConnectionId,
                ConnectorIdentity = AbbreviateIdentity(session.ConnectorIdentity),
                RemoteEndpoint = session.RemoteEndpoint ?? connection.RemoteEndPoint,
                Message = $"Session {sessionId} is already disconnecting."
            };
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!_inFlightByConnectionId.TryAdd(expectedConnectionId, 0))
        {
            return new ConnectorDisconnectResult
            {
                Status = ConnectorDisconnectStatus.AlreadyDisconnecting,
                SessionId = sessionId,
                ConnectionId = expectedConnectionId,
                ConnectorIdentity = AbbreviateIdentity(session.ConnectorIdentity),
                RemoteEndpoint = session.RemoteEndpoint ?? connection.RemoteEndPoint,
                Message = $"Session {sessionId} is already disconnecting."
            };
        }

        // COMMITTED: caller cancellation must not skip disposal from this point.
        var identityPrefix = AbbreviateIdentity(session.ConnectorIdentity);
        var remote = session.RemoteEndpoint ?? connection.RemoteEndPoint;
        var teardownCommitted = false;

        try
        {
            // Re-check ownership under the in-flight guard before mutating state.
            if (!_connectionTracker.TryGet(expectedConnectionId, out connection) || connection is null ||
                !_sessionRegistry.TryGetBySessionId(sessionId, out session) || session is null ||
                !string.Equals(session.ConnectionId, expectedConnectionId, StringComparison.Ordinal))
            {
                return NotConnected(sessionId, expectedConnectionId);
            }

            if (connection.State is ConnectorConnectionState.Disconnecting or ConnectorConnectionState.Disconnected)
            {
                return new ConnectorDisconnectResult
                {
                    Status = ConnectorDisconnectStatus.AlreadyDisconnecting,
                    SessionId = sessionId,
                    ConnectionId = expectedConnectionId,
                    ConnectorIdentity = identityPrefix,
                    RemoteEndpoint = remote,
                    Message = $"Session {sessionId} is already disconnecting."
                };
            }

            connection.MarkDisconnecting();
            teardownCommitted = true;

            _logger.LogInformation(
                "Administrative connector disconnect initiated. Operator={Operator} SessionId={SessionId} ConnectionId={ConnectionId}",
                identity.Username,
                sessionId,
                expectedConnectionId);

            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectInitiated,
                AuditEventOutcome.Informational,
                "Administrative connector disconnect initiated",
                new AuditEventContext
                {
                    ActorUsername = identity.Username,
                    SessionId = sessionId,
                    ConnectorIdentity = identityPrefix,
                    RemoteEndpoint = remote,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = expectedConnectionId
                    }
                });

            await TryWriteDisconnectFrameAsync(
                    connection,
                    expectedConnectionId,
                    disconnectFrameWriteTimeout ?? DisconnectFrameWriteTimeout)
                .ConfigureAwait(false);

            // Always dispose after commit — independent of SSH session lifetime.
            await connection.DisposeAsync().ConfigureAwait(false);

            var wait = completionWait ?? DefaultCompletionWait;
            // Observe listener cleanup without caller cancellation (timeout-only).
            var cleanupObserved = await WaitForOriginalConnectionCleanupAsync(
                    sessionId,
                    expectedConnectionId,
                    wait)
                .ConfigureAwait(false);

            if (!cleanupObserved)
            {
                _logger.LogWarning(
                    "Administrative disconnect: disposal requested but original ConnectionId cleanup not observed. SessionId={SessionId} ConnectionId={ConnectionId}",
                    sessionId,
                    expectedConnectionId);
                _audit.TryWrite(
                    AuditEventType.AdministrativeDisconnectFailed,
                    AuditEventOutcome.Failure,
                    "Administrative connector disconnect cleanup incomplete",
                    new AuditEventContext
                    {
                        ActorUsername = identity.Username,
                        SessionId = sessionId,
                        ConnectorIdentity = identityPrefix,
                        RemoteEndpoint = remote,
                        Details = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["connectionId"] = expectedConnectionId,
                            ["reason"] = "cleanup-incomplete"
                        }
                    });

                return new ConnectorDisconnectResult
                {
                    Status = ConnectorDisconnectStatus.CleanupIncomplete,
                    SessionId = sessionId,
                    ConnectionId = expectedConnectionId,
                    ConnectorIdentity = identityPrefix,
                    RemoteEndpoint = remote,
                    TeardownCommitted = true,
                    Message =
                        $"Disconnect of connector {sessionId} was requested; cleanup has not finished yet."
                };
            }

            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectSucceeded,
                AuditEventOutcome.Success,
                "Administrative connector disconnect succeeded",
                new AuditEventContext
                {
                    ActorUsername = identity.Username,
                    SessionId = sessionId,
                    ConnectorIdentity = identityPrefix,
                    RemoteEndpoint = remote,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = expectedConnectionId
                    }
                });

            return new ConnectorDisconnectResult
            {
                Status = ConnectorDisconnectStatus.Completed,
                SessionId = sessionId,
                ConnectionId = expectedConnectionId,
                ConnectorIdentity = identityPrefix,
                RemoteEndpoint = remote,
                TeardownCommitted = true,
                Message = $"Connector {sessionId} disconnected successfully."
            };
        }
        catch (OperationCanceledException) when (!teardownCommitted && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Administrative disconnect failed. SessionId={SessionId} ConnectionId={ConnectionId} Committed={Committed}",
                sessionId,
                expectedConnectionId,
                teardownCommitted);

            // If committed but Dispose was not reached, attempt disposal once more.
            if (teardownCommitted &&
                _connectionTracker.TryGet(expectedConnectionId, out var remaining) &&
                remaining is not null &&
                remaining.State != ConnectorConnectionState.Disconnected)
            {
                try
                {
                    await remaining.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeEx)
                {
                    _logger.LogDebug(
                        disposeEx,
                        "Best-effort disposal after administrative disconnect failure. ConnectionId={ConnectionId}",
                        expectedConnectionId);
                }
            }

            _audit.TryWrite(
                AuditEventType.AdministrativeDisconnectFailed,
                AuditEventOutcome.Failure,
                "Administrative connector disconnect failed",
                new AuditEventContext
                {
                    ActorUsername = identity.Username,
                    SessionId = sessionId,
                    ConnectorIdentity = identityPrefix,
                    RemoteEndpoint = remote,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["connectionId"] = expectedConnectionId
                    }
                });

            return new ConnectorDisconnectResult
            {
                Status = ConnectorDisconnectStatus.Failed,
                SessionId = sessionId,
                ConnectionId = expectedConnectionId,
                ConnectorIdentity = identityPrefix,
                RemoteEndpoint = remote,
                TeardownCommitted = teardownCommitted,
                Message = $"Disconnect of connector {sessionId} failed."
            };
        }
        finally
        {
            _inFlightByConnectionId.TryRemove(expectedConnectionId, out _);
        }
    }

    /// <summary>
    /// Best-effort write of a Disconnect control frame before local disposal.
    /// </summary>
    /// <param name="connection">Target connection.</param>
    /// <param name="connectionId">Connection ID for log correlation.</param>
    /// <param name="writeTimeout">Bounded write timeout.</param>
    /// <remarks>
    /// Failures and timeouts are logged at debug and do not prevent teardown.
    /// FrameWriter serializes concurrent writes.
    /// </remarks>
    private async Task TryWriteDisconnectFrameAsync(
        ConnectorConnection connection,
        string connectionId,
        TimeSpan writeTimeout)
    {
        // Best-effort only; FrameWriter serializes concurrent writes. Bounded so a stalled peer
        // cannot delay local disposal indefinitely.
        try
        {
            using var writeCts = new CancellationTokenSource(writeTimeout);
            await connection.Protocol.WriteControlAsync(
                    MessageType.Disconnect,
                    new DisconnectMessage { Reason = "Administrative disconnect" },
                    writeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Best-effort Disconnect frame timed out or cancelled. ConnectionId={ConnectionId}",
                connectionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Best-effort Disconnect frame failed before teardown. ConnectionId={ConnectionId}",
                connectionId);
        }
    }

    /// <summary>
    /// Completes only when the original ConnectionId is gone from the tracker and the
    /// session registry no longer maps sessionId to that ConnectionId.
    /// </summary>
    /// <param name="sessionId">Session under observation.</param>
    /// <param name="expectedConnectionId">Original connection ID that must leave the tracker.</param>
    /// <param name="wait">Maximum poll duration (timeout-only; no caller cancellation).</param>
    /// <returns><see langword="true"/> when cleanup is observed within the wait.</returns>
    /// <remarks>
    /// A replacement session (different ConnectionId) does not satisfy tracker removal for the original id.
    /// </remarks>
    private async Task<bool> WaitForOriginalConnectionCleanupAsync(
        string sessionId,
        string expectedConnectionId,
        TimeSpan wait)
    {
        var start = _timeProvider.GetUtcNow();
        while (_timeProvider.GetUtcNow() - start < wait)
        {
            if (IsOriginalConnectionFullyCleanedUp(sessionId, expectedConnectionId))
            {
                return true;
            }

            try
            {
                await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }

        return IsOriginalConnectionFullyCleanedUp(sessionId, expectedConnectionId);
    }

    /// <summary>
    /// True when the original connection ID is absent from the tracker and the registry
    /// no longer claims that connection ID for the session.
    /// </summary>
    /// <param name="sessionId">Session ID.</param>
    /// <param name="expectedConnectionId">Original connection ID.</param>
    /// <returns>Whether cleanup criteria are met.</returns>
    private bool IsOriginalConnectionFullyCleanedUp(string sessionId, string expectedConnectionId)
    {
        // Original connection must leave the tracker (keyed by ConnectionId — never by identity).
        if (_connectionTracker.TryGet(expectedConnectionId, out _))
        {
            return false;
        }

        // Registry must not still claim this ConnectionId for the session.
        if (_sessionRegistry.TryGetBySessionId(sessionId, out var remaining) &&
            remaining is not null &&
            string.Equals(remaining.ConnectionId, expectedConnectionId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Builds a <see cref="ConnectorDisconnectStatus.NotConnected"/> result.
    /// </summary>
    /// <param name="sessionId">Session ID.</param>
    /// <param name="connectionId">Optional connection ID.</param>
    /// <param name="connectorIdentity">Optional full thumbprint to abbreviate for display.</param>
    /// <param name="remoteEndpoint">Optional remote endpoint.</param>
    /// <returns>Not-connected result with operator message.</returns>
    private static ConnectorDisconnectResult NotConnected(
        string sessionId,
        string? connectionId = null,
        string? connectorIdentity = null,
        string? remoteEndpoint = null) =>
        new()
        {
            Status = ConnectorDisconnectStatus.NotConnected,
            SessionId = sessionId,
            ConnectionId = connectionId,
            ConnectorIdentity = connectorIdentity is null ? null : AbbreviateIdentity(connectorIdentity),
            RemoteEndpoint = remoteEndpoint,
            Message = $"Session {sessionId} is not connected."
        };

    /// <summary>
    /// Abbreviates a thumbprint to at most 12 characters for logs/CLI/audit display.
    /// </summary>
    /// <param name="thumbprint">Full or empty thumbprint.</param>
    /// <returns>Unchanged when length ≤ 12; otherwise the first 12 characters.</returns>
    private static string AbbreviateIdentity(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint))
        {
            return thumbprint;
        }

        return thumbprint.Length <= 12 ? thumbprint : thumbprint[..12];
    }
}
