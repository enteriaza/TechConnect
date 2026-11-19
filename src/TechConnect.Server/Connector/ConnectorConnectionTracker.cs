// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorConnectionTracker.cs
//
// Purpose:
// Tracks live authenticated ConnectorConnection instances by connection ID and
// supports bounded best-effort close of all connections during shutdown.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace TechConnect.Server.Connector;

/// <summary>
/// Concurrent map of authenticated connector connections keyed by <see cref="ConnectorConnection.ConnectionId"/>.
/// </summary>
/// <remarks>
/// Does not own registry or attachment cleanup; those remain in the listener finally /
/// shutdown paths. <see cref="CloseAllAsync"/> marks disconnecting, disposes connections,
/// and removes them from the map even if the wait times out.
/// </remarks>
internal sealed class ConnectorConnectionTracker
{
    /// <summary>Live connections keyed by ordinal connection ID.</summary>
    private readonly ConcurrentDictionary<string, ConnectorConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>Number of currently tracked connections.</summary>
    public int Count => _connections.Count;

    /// <summary>
    /// Attempts to add a connection under its <see cref="ConnectorConnection.ConnectionId"/>.
    /// </summary>
    /// <param name="connection">Connection to track.</param>
    /// <returns><see langword="true"/> when the ID was not already present.</returns>
    public bool TryAdd(ConnectorConnection connection) =>
        _connections.TryAdd(connection.ConnectionId, connection);

    /// <summary>
    /// Removes a connection by ID.
    /// </summary>
    /// <param name="connectionId">Connection ID to remove.</param>
    /// <param name="connection">Removed instance when present.</param>
    /// <returns><see langword="true"/> when removed.</returns>
    public bool TryRemove(string connectionId, out ConnectorConnection? connection) =>
        _connections.TryRemove(connectionId, out connection);

    /// <summary>
    /// Looks up a tracked connection by ID.
    /// </summary>
    /// <param name="connectionId">Connection ID.</param>
    /// <param name="connection">Matching connection when found.</param>
    /// <returns><see langword="true"/> when present.</returns>
    public bool TryGet(string connectionId, out ConnectorConnection? connection) =>
        _connections.TryGetValue(connectionId, out connection);

    /// <summary>
    /// Returns a point-in-time array of tracked connections.
    /// </summary>
    /// <returns>Snapshot copy of current values.</returns>
    public IReadOnlyCollection<ConnectorConnection> Snapshot() =>
        _connections.Values.ToArray();

    /// <summary>
    /// Marks all tracked connections disconnecting, disposes them, and removes them from the map.
    /// </summary>
    /// <param name="timeout">Maximum time to wait for all dispose tasks.</param>
    /// <param name="cancellationToken">Linked into the wait; on cancel, remaining dispose continues best-effort.</param>
    /// <returns>A task that completes when the wait ends (success, timeout, or cancel).</returns>
    /// <remarks>
    /// After the wait (including timeout/cancel), each snapshotted connection ID is removed
    /// from the tracker in a finally block. Does not clear the session registry or attachments.
    /// </remarks>
    public async Task CloseAllAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connections = Snapshot();
        if (connections.Count == 0)
        {
            return;
        }

        foreach (var connection in connections)
        {
            connection.MarkDisconnecting();
        }

        var disposeTasks = connections.Select(static c => c.DisposeAsync().AsTask()).ToArray();
        var all = Task.WhenAll(disposeTasks);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await all.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Bounded shutdown — remaining dispose tasks continue best-effort.
        }
        finally
        {
            foreach (var connection in connections)
            {
                _connections.TryRemove(connection.ConnectionId, out _);
            }
        }
    }
}
