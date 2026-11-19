// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionRegistry.cs
//
// Purpose:
// Maintains the in-memory index of Hello-registered connector sessions with
// reject-new identity policy and connection-ID-exact unregister for cleanup.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// In-memory registry of successfully Hello-registered connector sessions.
/// </summary>
/// <remarks>
/// <para>
/// One active connection per certificate identity (reject-new policy).
/// Session IDs are 8 lowercase hex characters from a cryptographically random Guid
/// (collision checked); case-insensitive for CLI lookup.
/// </para>
/// <para>
/// All public mutations synchronize on a private gate. Unregister by connection ID
/// never removes a different connection's session even when identities match.
/// Clearing the registry does not dispose connector connections or attachments.
/// </para>
/// </remarks>
internal sealed class ConnectorSessionRegistry
{
    /// <summary>Serializes all registry mutations and lookups that touch shared maps.</summary>
    private readonly object _gate = new();

    /// <summary>Sessions keyed by session ID (case-insensitive).</summary>
    private readonly Dictionary<string, ConnectorSession> _bySessionId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps certificate identity thumbprint to session ID (case-insensitive).</summary>
    private readonly Dictionary<string, string> _identityToSessionId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps TCP connection ID to session ID (ordinal).</summary>
    private readonly Dictionary<string, string> _connectionIdToSessionId =
        new(StringComparer.Ordinal);

    /// <summary>Number of currently registered sessions.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _bySessionId.Count;
            }
        }
    }

    /// <summary>
    /// Registers a new session for a validated connector identity, or returns an existing/duplicate result.
    /// </summary>
    /// <param name="connectorIdentity">mTLS client certificate thumbprint (identity key).</param>
    /// <param name="connectionId">TCP connection ID owning this registration.</param>
    /// <param name="remoteEndpoint">Optional remote endpoint string.</param>
    /// <param name="hostname">Optional Hello hostname.</param>
    /// <param name="applicationVersion">Optional Hello application version.</param>
    /// <param name="portName">Optional Hello serial port name.</param>
    /// <param name="tlsProtocol">Optional negotiated TLS protocol name.</param>
    /// <param name="connectedAtUtc">Registration timestamp; also seeds <see cref="ConnectorSession.LastActivityUtc"/>.</param>
    /// <returns>
    /// <see cref="ConnectorSessionRegistrationResult"/> indicating registered, already registered
    /// on this connection, or duplicate identity.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="connectorIdentity"/> or <paramref name="connectionId"/> is null/whitespace.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Unable to allocate a unique session ID after repeated Guid attempts (pathological).
    /// </exception>
    public ConnectorSessionRegistrationResult TryRegister(
        string connectorIdentity,
        string connectionId,
        string? remoteEndpoint,
        string? hostname,
        string? applicationVersion,
        string? portName,
        string? tlsProtocol,
        DateTimeOffset connectedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (_connectionIdToSessionId.TryGetValue(connectionId, out var existingOnConnection))
            {
                return ConnectorSessionRegistrationResult.AlreadyRegistered(_bySessionId[existingOnConnection]);
            }

            if (_identityToSessionId.ContainsKey(connectorIdentity))
            {
                return ConnectorSessionRegistrationResult.DuplicateIdentity(
                    "A connector with this certificate identity is already registered.");
            }

            var sessionId = AllocateSessionId_NoLock();
            var session = new ConnectorSession
            {
                SessionId = sessionId,
                ConnectorIdentity = connectorIdentity,
                ConnectionId = connectionId,
                RemoteEndpoint = remoteEndpoint,
                Hostname = NullIfWhiteSpace(hostname),
                ApplicationVersion = NullIfWhiteSpace(applicationVersion),
                PortName = NullIfWhiteSpace(portName),
                ConnectedAtUtc = connectedAtUtc,
                LastActivityUtc = connectedAtUtc,
                TlsProtocol = NullIfWhiteSpace(tlsProtocol),
                State = ConnectorSessionState.Connected
            };

            _bySessionId[sessionId] = session;
            _identityToSessionId[connectorIdentity] = sessionId;
            _connectionIdToSessionId[connectionId] = sessionId;
            return ConnectorSessionRegistrationResult.Registered(session);
        }
    }

    /// <summary>
    /// Looks up a session by session ID (case-insensitive).
    /// </summary>
    /// <param name="sessionId">Session ID to find.</param>
    /// <param name="session">Matching session when found.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool TryGetBySessionId(string sessionId, out ConnectorSession? session)
    {
        lock (_gate)
        {
            return _bySessionId.TryGetValue(sessionId, out session);
        }
    }

    /// <summary>
    /// Looks up a session by certificate identity thumbprint (case-insensitive).
    /// </summary>
    /// <param name="connectorIdentity">mTLS thumbprint identity.</param>
    /// <param name="session">Matching session when found.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool TryGetByIdentity(string connectorIdentity, out ConnectorSession? session)
    {
        session = null;
        lock (_gate)
        {
            if (!_identityToSessionId.TryGetValue(connectorIdentity, out var sessionId))
            {
                return false;
            }

            return _bySessionId.TryGetValue(sessionId, out session);
        }
    }

    /// <summary>
    /// Returns a snapshot of all sessions ordered by connect time then session ID.
    /// </summary>
    /// <returns>A new array copy; safe to enumerate without holding the registry lock.</returns>
    public IReadOnlyList<ConnectorSession> List()
    {
        lock (_gate)
        {
            return _bySessionId.Values
                .OrderBy(s => s.ConnectedAtUtc)
                .ThenBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>
    /// Removes the session owned by the given connection. Idempotent.
    /// </summary>
    /// <param name="connectionId">TCP connection ID that must own the session.</param>
    /// <param name="session">Removed session when successful.</param>
    /// <returns><see langword="true"/> when a session was removed for that connection.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionId"/> is null/whitespace.</exception>
    /// <remarks>
    /// Does not remove a different connection's session even if identities match.
    /// </remarks>
    public bool TryUnregisterByConnectionId(string connectionId, out ConnectorSession? session)
    {
        session = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_gate)
        {
            if (!_connectionIdToSessionId.TryGetValue(connectionId, out var sessionId))
            {
                return false;
            }

            return RemoveCore_NoLock(sessionId, expectedConnectionId: connectionId, out session);
        }
    }

    /// <summary>
    /// Removes a session by session ID without requiring a connection-ID match.
    /// </summary>
    /// <param name="sessionId">Session ID to remove.</param>
    /// <param name="session">Removed session when successful.</param>
    /// <returns><see langword="true"/> when removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="sessionId"/> is null/whitespace.</exception>
    public bool TryUnregisterBySessionId(string sessionId, out ConnectorSession? session)
    {
        session = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_gate)
        {
            return RemoveCore_NoLock(sessionId, expectedConnectionId: null, out session);
        }
    }

    /// <summary>
    /// Updates <see cref="ConnectorSession.LastActivityUtc"/> for the session owned by a connection.
    /// </summary>
    /// <param name="connectionId">TCP connection ID.</param>
    /// <param name="utcNow">Activity timestamp to record.</param>
    /// <remarks>
    /// Looks up the session under the registry gate, then writes the timestamp outside the lock
    /// (PERF-SRV-02). The connector RX hot path prefers a cached
    /// <see cref="TechConnect.Server.Connector.ConnectorConnection.RegisteredSession"/> and does
    /// not call this method. No-op when the connection is not registered.
    /// </remarks>
    public void TouchActivity(string connectionId, DateTimeOffset utcNow)
    {
        ConnectorSession? session;
        lock (_gate)
        {
            if (!_connectionIdToSessionId.TryGetValue(connectionId, out var sessionId) ||
                !_bySessionId.TryGetValue(sessionId, out session))
            {
                return;
            }
        }

        session.ObserveActivity(utcNow);
    }

    /// <summary>Removes all sessions (for example during listener shutdown).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _bySessionId.Clear();
            _identityToSessionId.Clear();
            _connectionIdToSessionId.Clear();
        }
    }

    /// <summary>
    /// Removes a session from all indexes when present and connection ownership matches.
    /// </summary>
    /// <param name="sessionId">Session ID to remove.</param>
    /// <param name="expectedConnectionId">
    /// When non-null, removal fails unless the session's connection ID matches exactly.
    /// </param>
    /// <param name="session">Removed session, or null when not removed.</param>
    /// <returns><see langword="true"/> when removed.</returns>
    /// <remarks>Caller must hold the registry gate.</remarks>
    private bool RemoveCore_NoLock(
        string sessionId,
        string? expectedConnectionId,
        out ConnectorSession? session)
    {
        if (!_bySessionId.TryGetValue(sessionId, out session))
        {
            session = null;
            return false;
        }

        if (expectedConnectionId is not null &&
            !string.Equals(session.ConnectionId, expectedConnectionId, StringComparison.Ordinal))
        {
            session = null;
            return false;
        }

        _bySessionId.Remove(sessionId);
        _identityToSessionId.Remove(session.ConnectorIdentity);
        _connectionIdToSessionId.Remove(session.ConnectionId);
        return true;
    }

    /// <summary>
    /// Allocates a unique 8-character lowercase hex session ID.
    /// </summary>
    /// <returns>Unused session ID.</returns>
    /// <exception cref="InvalidOperationException">All attempts collided (pathological).</exception>
    /// <remarks>Caller must hold the registry gate. Uses Guid entropy; compact for SSH CLI typing.</remarks>
    private string AllocateSessionId_NoLock()
    {
        // 8 lowercase hex chars (32 bits of Guid entropy) — compact for SSH CLI typing.
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = Guid.NewGuid().ToString("N")[..8];
            if (!_bySessionId.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to allocate a unique connector session ID.");
    }

    /// <summary>
    /// Returns trimmed text, or null when null/whitespace.
    /// </summary>
    /// <param name="value">Raw Hello field.</param>
    /// <returns>Trimmed value or null.</returns>
    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
