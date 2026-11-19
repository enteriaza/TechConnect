// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    IRemoteSessionService.cs
//
// Purpose:
// Coordinates remote session state and bridges an already-open serial port to
// the outbound transport without owning the physical serial lifecycle.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Models;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Coordinates remote session state and bridges serial traffic to the transport.
/// </summary>
/// <remarks>
/// <para>
/// Reconnection is automatic while the technician session remains active.
/// Physical serial lifecycle is independent of remote connectivity: stopping the
/// remote session does not close the serial port, and a remote outage does not
/// imply serial teardown.
/// </para>
/// <para>
/// Local serial RX is always drained. While the transport is down, remote-bound
/// bytes are discarded and never replayed after reconnect. Delivery counters such as
/// bytes-sent mean the transport accepted a write — not NOC-client receipt.
/// </para>
/// <para>
/// State and snapshot properties are safe to read from UI threads; change events
/// may be raised on background threads — UI subscribers must marshal as needed.
/// </para>
/// </remarks>
public interface IRemoteSessionService : IAsyncDisposable
{
    /// <summary>
    /// Current remote transport / session lifecycle state.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="ISerialConnectionService.IsOpen"/>.
    /// </remarks>
    RemoteSessionState State { get; }

    /// <summary>
    /// Locally generated field-side correlation id for this Connect cycle.
    /// </summary>
    /// <remarks>
    /// Not the server-assigned registration session id exposed on
    /// <see cref="RemoteSessionSnapshot.ServerSessionId"/>. Cleared after stop.
    /// </remarks>
    string? SessionId { get; }

    /// <summary>
    /// Raised when <see cref="State"/> changes.
    /// </summary>
    event EventHandler<RemoteSessionState>? StateChanged;

    /// <summary>
    /// Raised when a new <see cref="RemoteSessionSnapshot"/> is published for UI/status.
    /// </summary>
    /// <remarks>
    /// Implementations may coalesce high-frequency stats updates; do not assume
    /// one event per byte transferred.
    /// </remarks>
    event EventHandler<RemoteSessionSnapshot>? SnapshotChanged;

    /// <summary>
    /// Raised when the remote session faults with a human-readable reason
    /// (never secrets or serial payloads).
    /// </summary>
    event EventHandler<string>? Faulted;

    /// <summary>
    /// Returns a point-in-time snapshot of serial, remote, NOC, and delivery state.
    /// </summary>
    /// <returns>Immutable snapshot suitable for UI binding.</returns>
    RemoteSessionSnapshot GetSnapshot();

    /// <summary>
    /// Starts remote session coordination for an already-open serial port.
    /// </summary>
    /// <param name="portName">
    /// COM port name used for correlation and logging; the physical port must already
    /// be owned/open by <see cref="ISerialConnectionService"/>.
    /// </param>
    /// <param name="cancellationToken">Token that cancels startup of the reconnect loop.</param>
    /// <returns>A task that completes when session workers have been started.</returns>
    /// <remarks>
    /// Does not close the serial port when the remote service is unavailable; reconnect
    /// continues until <see cref="StopAsync"/> or disposal.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task StartAsync(string portName, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels reconnect attempts and disconnects the transport.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels wait for worker join / disconnect gates.</param>
    /// <returns>A task that completes when remote workers have stopped.</returns>
    /// <remarks>
    /// Does not close the physical serial port; the caller owns that lifecycle.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task StopAsync(CancellationToken cancellationToken);
}
