// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PendingBaudTestExtensions.cs
//
// Purpose:
// Completes pending SetBaud / serial-configuration requests in tests so connect
// can proceed into terminal mode without a real connector.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Attachment;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Test extensions for <see cref="PendingBaudRequestTracker"/>.
/// </summary>
internal static class PendingBaudTestExtensions
{
    /// <summary>
    /// Completes the operator's outstanding serial-configuration (SetBaud) request as the connector would.
    /// </summary>
    /// <remarks>
    /// <c>connect</c> waits for this result before entering terminal mode.
    /// Polls until a pending request appears or <paramref name="timeout"/> elapses.
    /// </remarks>
    /// <param name="tracker">Pending baud tracker under test.</param>
    /// <param name="connectorSessionId">Connector session id.</param>
    /// <param name="operatorSessionId">Operator session id that owns the pending request.</param>
    /// <param name="success">Whether the completion reports success.</param>
    /// <param name="error">Optional error text on failure.</param>
    /// <param name="baudRate">Effective baud rate on success.</param>
    /// <param name="timeout">Poll timeout (default 8 seconds).</param>
    /// <returns>A task that completes when the pending request is completed.</returns>
    /// <exception cref="InvalidOperationException">Completion failed for an existing pending request.</exception>
    /// <exception cref="TimeoutException">No pending request appeared before the deadline.</exception>
    public static async Task CompletePendingSerialConfigAsync(
        this PendingBaudRequestTracker tracker,
        string connectorSessionId,
        string operatorSessionId,
        bool success = true,
        string? error = null,
        int baudRate = 9600,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        while (DateTime.UtcNow < deadline)
        {
            if (tracker.TryGetByConnectorSessionId(connectorSessionId, out var pending) && pending is not null)
            {
                var completed = tracker.TryComplete(
                    pending.CorrelationId,
                    operatorSessionId,
                    new PendingBaudResult
                    {
                        Success = success,
                        Error = error,
                        EffectiveBaudRate = success ? baudRate : null
                    });
                if (!completed)
                {
                    throw new InvalidOperationException(
                        "Pending serial configuration request could not be completed.");
                }

                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No pending serial configuration request was registered in time.");
    }
}
