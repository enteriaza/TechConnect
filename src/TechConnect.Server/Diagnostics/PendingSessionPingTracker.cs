// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PendingSessionPingTracker.cs
//
// Purpose:
// Correlates SessionPing requests to SessionPingResult responses with bounded
// outstanding probes, per-probe timeout CTS ownership, and connector failure fan-out.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Outstanding session ping registration awaiting a connector response.
/// </summary>
/// <remarks>
/// <see cref="TimeoutCts"/> is owned by the tracker: disposed on complete, cancel, or timeout.
/// </remarks>
internal sealed class PendingSessionPingRequest
{
    /// <summary>Correlation id embedded in the outbound SessionPing message.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Target connector session id.</summary>
    public required string ConnectorSessionId { get; init; }

    /// <summary>Completion source signaled on result, timeout, or cancel.</summary>
    public required TaskCompletionSource<PendingSessionPingResult> Completion { get; init; }

    /// <summary>Owned timeout cancellation source; disposed when the probe leaves the map.</summary>
    public required CancellationTokenSource TimeoutCts { get; init; }
}

/// <summary>
/// Result delivered to a waiting session ping registration.
/// </summary>
internal sealed class PendingSessionPingResult
{
    /// <summary>Whether the connector reported a successful ping.</summary>
    public required bool Success { get; init; }

    /// <summary>Optional error detail when unsuccessful.</summary>
    public string? Error { get; init; }

    /// <summary>Hostname reported by the connector when available.</summary>
    public string? Hostname { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the wait ended because the timeout CTS fired
    /// rather than an explicit connector result.
    /// </summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Correlates SessionPing requests to SessionPingResult responses.
/// </summary>
/// <remarks>
/// Allows multiple concurrent probes bounded by <see cref="MaxOutstanding"/>.
/// Timeout callbacks remove the entry and complete with <see cref="PendingSessionPingResult.TimedOut"/>.
/// <see cref="Cancel"/> completes the TCS as canceled after disposing the timeout CTS.
/// </remarks>
internal sealed class PendingSessionPingTracker
{
    /// <summary>Maximum concurrent outstanding session ping probes.</summary>
    public const int MaxOutstanding = 64;

    /// <summary>Outstanding probes keyed by correlation id (ordinal).</summary>
    private readonly ConcurrentDictionary<string, PendingSessionPingRequest> _byCorrelationId =
        new(StringComparer.Ordinal);

    /// <summary>Gets the current number of outstanding probes.</summary>
    public int Count => _byCorrelationId.Count;

    /// <summary>
    /// Registers a new outstanding probe with a dedicated timeout.
    /// </summary>
    /// <param name="connectorSessionId">Target connector session; must be non-whitespace.</param>
    /// <param name="timeout">Duration after which the probe completes as timed out.</param>
    /// <param name="request">Registered pending request when the method returns <see langword="true"/>.</param>
    /// <param name="error">Operator-facing error when registration fails.</param>
    /// <returns>
    /// <see langword="true"/> when registered; <see langword="false"/> when at capacity or add failed.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="connectorSessionId"/> is null or whitespace.</exception>
    public bool TryRegister(
        string connectorSessionId,
        TimeSpan timeout,
        out PendingSessionPingRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSessionId);

        if (_byCorrelationId.Count >= MaxOutstanding)
        {
            error = "% Too many outstanding session ping probes.";
            return false;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var timeoutCts = new CancellationTokenSource(timeout);
        var pending = new PendingSessionPingRequest
        {
            CorrelationId = correlationId,
            ConnectorSessionId = connectorSessionId,
            Completion = new TaskCompletionSource<PendingSessionPingResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            TimeoutCts = timeoutCts
        };

        if (!_byCorrelationId.TryAdd(correlationId, pending))
        {
            timeoutCts.Dispose();
            error = "% Unable to register session ping probe.";
            return false;
        }

        timeoutCts.Token.Register(() =>
        {
            if (_byCorrelationId.TryRemove(correlationId, out var timedOut))
            {
                timedOut.Completion.TrySetResult(new PendingSessionPingResult
                {
                    Success = false,
                    TimedOut = true,
                    Error = "Session ping timed out waiting for the connector."
                });
                timedOut.TimeoutCts.Dispose();
            }
        });

        request = pending;
        return true;
    }

    /// <summary>
    /// Completes an outstanding probe with an explicit result and disposes its timeout CTS.
    /// </summary>
    /// <param name="correlationId">Correlation id from the SessionPingResult.</param>
    /// <param name="result">Result to deliver to the waiter.</param>
    /// <returns>
    /// <see langword="true"/> when the probe was still outstanding and the TCS accepted the result.
    /// </returns>
    public bool TryComplete(string correlationId, PendingSessionPingResult result)
    {
        if (!_byCorrelationId.TryRemove(correlationId, out var pending))
        {
            return false;
        }

        try
        {
            pending.TimeoutCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        pending.TimeoutCts.Dispose();
        return pending.Completion.TrySetResult(result);
    }

    /// <summary>
    /// Fails all outstanding probes for a connector session (case-insensitive session id match).
    /// </summary>
    /// <param name="connectorSessionId">Connector session whose probes should fail.</param>
    /// <param name="error">Error text delivered to each waiter.</param>
    public void FailConnectorSession(string connectorSessionId, string error)
    {
        foreach (var pair in _byCorrelationId.ToArray())
        {
            if (!string.Equals(
                    pair.Value.ConnectorSessionId,
                    connectorSessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryComplete(
                pair.Key,
                new PendingSessionPingResult
                {
                    Success = false,
                    Error = error
                });
        }
    }

    /// <summary>
    /// Cancels an outstanding probe, disposing its timeout CTS and canceling the TCS.
    /// </summary>
    /// <param name="correlationId">Correlation id to cancel.</param>
    public void Cancel(string correlationId)
    {
        if (_byCorrelationId.TryRemove(correlationId, out var pending))
        {
            try
            {
                pending.TimeoutCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            pending.TimeoutCts.Dispose();
            pending.Completion.TrySetCanceled();
        }
    }
}
