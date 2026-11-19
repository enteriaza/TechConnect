// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ActiveCopyRegistry.cs
//
// Purpose:
// Tracks at-most-one active copy transfer per serial attachment session id with
// operator-owned claim and release semantics.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace TechConnect.Server.Copy;

/// <summary>
/// Ensures at most one active <c>copy</c> transfer per connector attachment (session id).
/// </summary>
/// <remarks>
/// Ownership is keyed by attachment <c>sessionId</c> and stored as the claiming
/// operator session id. <see cref="End"/> only releases when the same operator
/// that claimed the slot calls it (compare-and-remove).
/// </remarks>
internal sealed class ActiveCopyRegistry
{
    /// <summary>Active transfers: attachment session id → claiming operator session id.</summary>
    private readonly ConcurrentDictionary<string, string> _activeBySessionId =
        new(StringComparer.Ordinal);

    /// <summary>Attempts to claim exclusive copy ownership for <paramref name="sessionId"/>.</summary>
    /// <param name="sessionId">Connector attachment / serial session id.</param>
    /// <param name="operatorSessionId">Operator that will own the transfer.</param>
    /// <returns>True if the claim succeeded; false if another copy is already active.</returns>
    /// <exception cref="ArgumentException">Either id is null or whitespace.</exception>
    public bool TryBegin(string sessionId, string operatorSessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorSessionId);
        return _activeBySessionId.TryAdd(sessionId, operatorSessionId);
    }

    /// <summary>
    /// Releases the active copy slot for <paramref name="sessionId"/> only if
    /// <paramref name="operatorSessionId"/> still owns it.
    /// </summary>
    /// <param name="sessionId">Attachment session id.</param>
    /// <param name="operatorSessionId">Operator that previously claimed the slot.</param>
    /// <remarks>
    /// Null/whitespace inputs are ignored. A mismatched operator does not clear the slot.
    /// </remarks>
    public void End(string sessionId, string operatorSessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(operatorSessionId))
        {
            return;
        }

        // Atomic compare-and-remove: only the owning operator may release the slot.
        _activeBySessionId.TryRemove(
            new KeyValuePair<string, string>(sessionId, operatorSessionId));
    }

    /// <summary>
    /// Returns whether any operator currently holds an active copy for the session.
    /// </summary>
    /// <param name="sessionId">Attachment session id.</param>
    /// <returns>True when a claim is present.</returns>
    public bool IsActive(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _activeBySessionId.ContainsKey(sessionId);
}
