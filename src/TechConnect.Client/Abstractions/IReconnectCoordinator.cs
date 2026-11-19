// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    IReconnectCoordinator.cs
//
// Purpose:
// Single-owner reconnect loop contract with exponential backoff and jitter until
// cancellation (technician Disconnect or app shutdown).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Single owner of remote reconnect attempts.
/// </summary>
/// <remarks>
/// Guarantees at most one in-flight connect/reconnect loop per instance.
/// Uses exponential backoff with jitter until cancelled (technician Disconnect /
/// app shutdown). Permanent configuration errors from the connect attempt should
/// stop the loop rather than retry forever.
/// </remarks>
public interface IReconnectCoordinator
{
    /// <summary>
    /// Whether a reconnect loop is currently executing on this instance.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Runs <paramref name="connectAttempt"/> repeatedly until cancelled.
    /// </summary>
    /// <param name="connectAttempt">
    /// Attempt delegate. A successful attempt should return only after the connection
    /// is lost (so the loop reconnects), or throw when connect fails.
    /// </param>
    /// <param name="cancellationToken">
    /// Token that stops the loop (including during backoff delay).
    /// </param>
    /// <returns>
    /// A task that completes when the loop exits due to cancellation or a permanent
    /// configuration failure handled by the implementation.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="connectAttempt"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A reconnect loop is already running.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task RunAsync(Func<CancellationToken, Task> connectAttempt, CancellationToken cancellationToken);

    /// <summary>
    /// Resets the backoff delay to the configured initial delay.
    /// </summary>
    /// <remarks>
    /// Called at loop start and after a successful session ends so the next reconnect
    /// does not inherit a large delay.
    /// </remarks>
    void ResetBackoff();
}
