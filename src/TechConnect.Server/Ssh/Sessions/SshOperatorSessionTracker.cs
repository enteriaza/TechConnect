// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshOperatorSessionTracker.cs
//
// Purpose:
// Tracks active SSH transport sessions and CLI presence records for limits,
// show users/terminal, and bounded shutdown close-all.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using Microsoft.DevTunnels.Ssh;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Ssh.Sessions;

/// <summary>
/// Concurrent tracker for SSH transport sessions and live CLI operator presence.
/// </summary>
/// <remarks>
/// Transport sessions gate <see cref="Configuration.SshServerOptions.MaximumConcurrentConnections"/>.
/// CLI records are keyed by operator session id and updated as privilege, attachment,
/// and terminal mode change. Shutdown uses <see cref="CloseAllAsync"/> with a timeout.
/// </remarks>
internal sealed class SshOperatorSessionTracker
{
    /// <summary>
    /// Active SSH server sessions mapped to optional remote endpoint strings.
    /// </summary>
    private readonly ConcurrentDictionary<SshServerSession, string?> _sessions = new();

    /// <summary>
    /// Live CLI presence records keyed by operator session id.
    /// </summary>
    private readonly ConcurrentDictionary<string, OperatorCliSessionRecord> _cliSessions =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Number of tracked SSH transport sessions.
    /// </summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Attempts to register a newly opened SSH session.
    /// </summary>
    /// <param name="session">SSH server session.</param>
    /// <param name="remoteEndpoint">Optional formatted remote endpoint.</param>
    /// <returns><see langword="true"/> when the session was added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public bool TryAdd(SshServerSession session, string? remoteEndpoint = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryAdd(session, remoteEndpoint);
    }

    /// <summary>
    /// Removes a closed SSH session.
    /// </summary>
    /// <param name="session">SSH server session.</param>
    /// <returns><see langword="true"/> when the session was present.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
    public bool Remove(SshServerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _sessions.TryRemove(session, out _);
    }

    /// <summary>
    /// Looks up the remote endpoint recorded for a transport session.
    /// </summary>
    /// <param name="session">SSH server session.</param>
    /// <param name="remoteEndpoint">Stored endpoint when found.</param>
    /// <returns><see langword="true"/> when the session is tracked.</returns>
    public bool TryGetRemoteEndpoint(SshServerSession session, out string? remoteEndpoint) =>
        _sessions.TryGetValue(session, out remoteEndpoint);

    /// <summary>
    /// Returns a snapshot of currently tracked SSH sessions.
    /// </summary>
    /// <returns>Array copy of session keys.</returns>
    public IReadOnlyCollection<SshServerSession> Snapshot() => _sessions.Keys.ToArray();

    /// <summary>
    /// Registers or replaces a CLI presence record for an operator session.
    /// </summary>
    /// <param name="record">Presence record.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    public void RegisterCliSession(OperatorCliSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _cliSessions[record.OperatorSessionId] = record;
    }

    /// <summary>
    /// Removes a CLI presence record by operator session id.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id; blank values are ignored.</param>
    public void UnregisterCliSession(string operatorSessionId)
    {
        if (!string.IsNullOrWhiteSpace(operatorSessionId))
        {
            _cliSessions.TryRemove(operatorSessionId, out _);
        }
    }

    /// <summary>
    /// Looks up a CLI presence record.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <param name="record">Record when found.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool TryGetCliSession(string operatorSessionId, out OperatorCliSessionRecord? record) =>
        _cliSessions.TryGetValue(operatorSessionId, out record);

    /// <summary>
    /// Updates active privilege on a tracked CLI session when present.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <param name="privilege">New active privilege.</param>
    public void UpdateActivePrivilege(string operatorSessionId, PrivilegeLevel privilege)
    {
        if (_cliSessions.TryGetValue(operatorSessionId, out var record) && record is not null)
        {
            record.ActivePrivilege = privilege;
        }
    }

    /// <summary>
    /// Updates attached connector session id on a tracked CLI session when present.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <param name="connectorSessionId">Connector session id, or <see langword="null"/> when detached.</param>
    public void UpdateAttachment(string operatorSessionId, string? connectorSessionId)
    {
        if (_cliSessions.TryGetValue(operatorSessionId, out var record) && record is not null)
        {
            record.AttachedConnectorSessionId = connectorSessionId;
        }
    }

    /// <summary>
    /// Updates whether the operator is in serial terminal mode.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <param name="active"><see langword="true"/> when terminal mode is active.</param>
    public void UpdateTerminalMode(string operatorSessionId, bool active)
    {
        if (_cliSessions.TryGetValue(operatorSessionId, out var record) && record is not null)
        {
            record.TerminalModeActive = active;
        }
    }

    /// <summary>
    /// Updates terminal dimensions on a tracked CLI session when present.
    /// </summary>
    /// <param name="operatorSessionId">Operator session id.</param>
    /// <param name="columns">Column count.</param>
    /// <param name="rows">Row count.</param>
    public void UpdateTerminalSize(string operatorSessionId, int columns, int rows)
    {
        if (_cliSessions.TryGetValue(operatorSessionId, out var record) && record is not null)
        {
            record.Columns = columns;
            record.Rows = rows;
        }
    }

    /// <summary>
    /// Lists CLI presence records ordered by connect time then username.
    /// </summary>
    /// <returns>Snapshot list for display commands.</returns>
    public IReadOnlyList<OperatorCliSessionRecord> ListCliSessions() =>
        _cliSessions.Values
            .OrderBy(r => r.ConnectedAtUtc)
            .ThenBy(r => r.Username, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Closes all tracked SSH sessions within <paramref name="timeout"/>, disposing on cancel.
    /// </summary>
    /// <param name="timeout">Maximum wait for orderly close.</param>
    /// <param name="cancellationToken">Caller cancellation linked with the timeout.</param>
    /// <returns>A task that completes when closes finish or the timeout elapses.</returns>
    public async Task CloseAllAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sessions = Snapshot();
        if (sessions.Count == 0)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        var closeTasks = sessions.Select(async session =>
        {
            try
            {
                await session.CloseAsync(SshDisconnectReason.ByApplication, "Server shutting down.")
                    .ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Best-effort cleanup during shutdown.
                }
            }
        });

        try
        {
            await Task.WhenAll(closeTasks).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            foreach (var session in sessions)
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Ignored — bounded shutdown.
                }
            }
        }
    }
}
