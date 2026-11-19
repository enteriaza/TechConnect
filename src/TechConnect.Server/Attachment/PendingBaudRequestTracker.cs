// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PendingBaudRequestTracker.cs
//
// Purpose:
// Correlates operator-initiated SetBaud requests to connector SetBaudResult
// responses with timeouts and bounded outstanding work per operator session.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;

namespace TechConnect.Server.Attachment;

/// <summary>
/// In-flight SetBaud request awaiting a matching connector result.
/// </summary>
internal sealed class PendingBaudRequest
{
    /// <summary>Correlation ID sent on the wire and used as the tracker key.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>Operator SSH session that owns this outstanding request.</summary>
    public required string OperatorSessionId { get; init; }

    /// <summary>Connector session targeted by the baud change.</summary>
    public required string ConnectorSessionId { get; init; }

    /// <summary>Baud rate requested by the operator.</summary>
    public required int RequestedBaudRate { get; init; }

    /// <summary>
    /// Completes when a result, timeout, or failure is recorded.
    /// Created with <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>.
    /// </summary>
    public required TaskCompletionSource<PendingBaudResult> Completion { get; init; }

    /// <summary>Cancels when the request times out; disposed when the request is removed.</summary>
    public required CancellationTokenSource TimeoutCts { get; init; }
}

/// <summary>
/// Outcome delivered to the waiting operator for a SetBaud attempt.
/// </summary>
internal sealed class PendingBaudResult
{
    /// <summary>True when the connector reported a successful baud change.</summary>
    public required bool Success { get; init; }

    /// <summary>Human-readable failure text when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>Effective baud rate from the connector when available.</summary>
    public int? EffectiveBaudRate { get; init; }

    /// <summary>Full serial snapshot from a successful result payload, if present.</summary>
    public CachedSerialSnapshot? Config { get; init; }

    /// <summary>True when the tracker completed the request due to timeout.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// Connector-reported serial settings carried on a successful SetBaudResult.
/// </summary>
internal sealed class CachedSerialSnapshot
{
    /// <summary>Baud rate reported by the connector.</summary>
    public required int BaudRate { get; init; }

    /// <summary>Data bits reported by the connector.</summary>
    public required int DataBits { get; init; }

    /// <summary>Parity name reported by the connector.</summary>
    public required string Parity { get; init; }

    /// <summary>Stop bits name reported by the connector.</summary>
    public required string StopBits { get; init; }

    /// <summary>Handshake name reported by the connector.</summary>
    public required string Handshake { get; init; }
}

/// <summary>
/// Correlates SetBaud requests to SetBaudResult responses for operators.
/// </summary>
/// <remarks>
/// <para>
/// Bounded: at most one outstanding baud request per operator session.
/// Maps are concurrent; removal validates that the completing operator matches
/// the registered owner (wrong operator cannot complete another operator's request).
/// </para>
/// <para>
/// Timeouts complete with <see cref="PendingBaudResult.TimedOut"/> set.
/// Late completes after removal return false. Clearing/failing paths cancel and
/// dispose each request's timeout CTS. This tracker does not tear down attachments
/// or connector connections.
/// </para>
/// </remarks>
internal sealed class PendingBaudRequestTracker
{
    /// <summary>Default wait for a connector SetBaudResult before marking timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Outstanding requests keyed by correlation ID (ordinal).</summary>
    private readonly ConcurrentDictionary<string, PendingBaudRequest> _byCorrelationId =
        new(StringComparer.Ordinal);

    /// <summary>Maps operator session ID to its single outstanding correlation ID (ordinal).</summary>
    private readonly ConcurrentDictionary<string, string> _correlationByOperator =
        new(StringComparer.Ordinal);

    /// <summary>Number of currently tracked outstanding requests.</summary>
    public int Count => _byCorrelationId.Count;

    /// <summary>
    /// Registers a new outstanding baud request for an operator, or rejects if one is already in progress.
    /// </summary>
    /// <param name="operatorSessionId">Operator SSH session owning the request.</param>
    /// <param name="connectorSessionId">Target connector session ID.</param>
    /// <param name="requestedBaudRate">Baud rate to request on the wire.</param>
    /// <param name="request">Created pending request when registration succeeds; otherwise null.</param>
    /// <param name="error">Operator-facing rejection text when registration fails.</param>
    /// <param name="timeout">
    /// Optional timeout; when null, <see cref="DefaultTimeout"/> is used.
    /// </param>
    /// <returns><see langword="true"/> when registered; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="operatorSessionId"/> or <paramref name="connectorSessionId"/> is null/whitespace.
    /// </exception>
    public bool TryRegister(
        string operatorSessionId,
        string connectorSessionId,
        int requestedBaudRate,
        out PendingBaudRequest? request,
        out string? error,
        TimeSpan? timeout = null)
    {
        request = null;
        error = null;
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSessionId);

        if (_correlationByOperator.ContainsKey(operatorSessionId))
        {
            error = "% A baud-rate change is already in progress.";
            return false;
        }

        var correlationId = Guid.NewGuid().ToString("N");
        var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        var pending = new PendingBaudRequest
        {
            CorrelationId = correlationId,
            OperatorSessionId = operatorSessionId,
            ConnectorSessionId = connectorSessionId,
            RequestedBaudRate = requestedBaudRate,
            Completion = new TaskCompletionSource<PendingBaudResult>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            TimeoutCts = timeoutCts
        };

        if (!_correlationByOperator.TryAdd(operatorSessionId, correlationId) ||
            !_byCorrelationId.TryAdd(correlationId, pending))
        {
            timeoutCts.Dispose();
            _correlationByOperator.TryRemove(operatorSessionId, out _);
            _byCorrelationId.TryRemove(correlationId, out _);
            error = "% A baud-rate change is already in progress.";
            return false;
        }

        timeoutCts.Token.Register(() =>
        {
            if (TryRemove(correlationId, operatorSessionId, out var timedOut) && timedOut is not null)
            {
                timedOut.Completion.TrySetResult(new PendingBaudResult
                {
                    Success = false,
                    TimedOut = true,
                    Error = "Baud-rate change timed out waiting for the connector."
                });
                timedOut.TimeoutCts.Dispose();
            }
        });

        request = pending;
        return true;
    }

    /// <summary>
    /// Completes a pending request with a connector result when correlation and operator match.
    /// </summary>
    /// <param name="correlationId">Correlation ID from the SetBaudResult.</param>
    /// <param name="operatorSessionId">Expected owning operator session.</param>
    /// <param name="result">Result delivered to <see cref="PendingBaudRequest.Completion"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the pending request was removed and the completion set;
    /// otherwise <see langword="false"/> (unknown, already completed, or wrong operator).
    /// </returns>
    public bool TryComplete(
        string correlationId,
        string operatorSessionId,
        PendingBaudResult result)
    {
        if (!TryRemove(correlationId, operatorSessionId, out var pending) || pending is null)
        {
            return false;
        }

        pending.TimeoutCts.Cancel();
        pending.TimeoutCts.Dispose();
        return pending.Completion.TrySetResult(result);
    }

    /// <summary>
    /// Lookup for CLI inspection. Returns the pending request for a connector session, if any.
    /// </summary>
    /// <param name="connectorSessionId">Connector session to search for (case-insensitive match).</param>
    /// <param name="request">Matching pending request, or null when not found.</param>
    /// <returns><see langword="true"/> when a matching outstanding request exists.</returns>
    /// <remarks>
    /// Scans outstanding entries; not keyed by connector session. Whitespace IDs return false.
    /// </remarks>
    public bool TryGetByConnectorSessionId(string connectorSessionId, out PendingBaudRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(connectorSessionId))
        {
            return false;
        }

        foreach (var pair in _byCorrelationId)
        {
            if (string.Equals(
                    pair.Value.ConnectorSessionId,
                    connectorSessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                request = pair.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Fails and removes the outstanding request for an operator, if any.
    /// </summary>
    /// <param name="operatorSessionId">Operator whose pending request should fail.</param>
    /// <param name="error">Failure message delivered on <see cref="PendingBaudResult.Error"/>.</param>
    public void FailOperator(string operatorSessionId, string error)
    {
        if (!_correlationByOperator.TryGetValue(operatorSessionId, out var correlationId))
        {
            return;
        }

        if (TryRemove(correlationId, operatorSessionId, out var pending) && pending is not null)
        {
            pending.TimeoutCts.Cancel();
            pending.TimeoutCts.Dispose();
            pending.Completion.TrySetResult(new PendingBaudResult
            {
                Success = false,
                Error = error
            });
        }
    }

    /// <summary>
    /// Fails all outstanding requests targeting the given connector session.
    /// </summary>
    /// <param name="connectorSessionId">Connector session ID (case-insensitive match).</param>
    /// <param name="error">Failure message delivered to each matching request.</param>
    /// <remarks>Used when a connector disconnects so waiting operators unblock.</remarks>
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

            if (TryRemove(pair.Key, pair.Value.OperatorSessionId, out var pending) && pending is not null)
            {
                pending.TimeoutCts.Cancel();
                pending.TimeoutCts.Dispose();
                pending.Completion.TrySetResult(new PendingBaudResult
                {
                    Success = false,
                    Error = error
                });
            }
        }
    }

    /// <summary>
    /// Fails and removes every outstanding request (for example during server shutdown).
    /// </summary>
    /// <param name="error">Failure message delivered to all pending completions.</param>
    public void ClearAll(string error)
    {
        foreach (var pair in _byCorrelationId.ToArray())
        {
            if (TryRemove(pair.Key, pair.Value.OperatorSessionId, out var pending) && pending is not null)
            {
                pending.TimeoutCts.Cancel();
                pending.TimeoutCts.Dispose();
                pending.Completion.TrySetResult(new PendingBaudResult
                {
                    Success = false,
                    Error = error
                });
            }
        }
    }

    /// <summary>
    /// Removes a pending request when correlation and operator ownership match.
    /// </summary>
    /// <param name="correlationId">Correlation key.</param>
    /// <param name="operatorSessionId">Expected operator owner (ordinal match).</param>
    /// <param name="request">Removed request, or null when rejected/not found.</param>
    /// <returns><see langword="true"/> when removed for the expected operator.</returns>
    /// <remarks>
    /// If the correlation exists but the operator does not match, the entry is put back
    /// and the call fails — preventing cross-operator completion.
    /// </remarks>
    private bool TryRemove(
        string correlationId,
        string operatorSessionId,
        out PendingBaudRequest? request)
    {
        if (!_byCorrelationId.TryRemove(correlationId, out request))
        {
            return false;
        }

        if (!string.Equals(request.OperatorSessionId, operatorSessionId, StringComparison.Ordinal))
        {
            // Wrong operator — put it back and reject.
            _byCorrelationId.TryAdd(correlationId, request);
            request = null;
            return false;
        }

        _correlationByOperator.TryRemove(operatorSessionId, out _);
        return true;
    }
}
