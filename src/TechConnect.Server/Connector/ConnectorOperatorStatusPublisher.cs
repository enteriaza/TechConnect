// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorOperatorStatusPublisher.cs
//
// Purpose:
// Publishes ordered, on-change OperatorStatus frames for a single connector
// connection generation without owning attachment lifecycle.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging;
using TechConnect.Protocol.Messages;

namespace TechConnect.Server.Connector;

/// <summary>
/// Serialized on-change publisher for <see cref="TechConnect.Protocol.MessageType.OperatorStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors attachment/terminal state; does not own attachment lifecycle.
/// Version allocation and wire transmission share one gate so messages cannot reorder.
/// Never holds <c>OperatorAttachmentManager</c> locks.
/// </para>
/// <para>
/// After dispose, <see cref="PublishAsync"/> is a no-op. Write failures are logged and do
/// not roll back an already-allocated version so later publishes stay ordered.
/// Teardown uses <see cref="TryPublishWaitingAsync"/> as a best-effort Waiting notification;
/// connector disposal continues regardless.
/// </para>
/// </remarks>
internal sealed class ConnectorOperatorStatusPublisher : IAsyncDisposable
{
    /// <summary>Connection whose protocol writer carries OperatorStatus frames.</summary>
    private readonly ConnectorConnection _connection;

    /// <summary>Logger for publish failures and version overflow.</summary>
    private readonly ILogger _logger;

    /// <summary>Serializes version allocation and wire writes.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Last allocated state version (0 before first successful allocation).</summary>
    private uint _version;

    /// <summary>Last published Attached flag.</summary>
    private bool _attached;

    /// <summary>Last published TerminalActive flag.</summary>
    private bool _terminalActive;

    /// <summary>True after at least one version has been allocated.</summary>
    private bool _hasPublished;

    /// <summary>Dispose gate; 0 = live, 1 = disposed.</summary>
    private int _disposed;

    /// <summary>
    /// Creates a publisher bound to a connection.
    /// </summary>
    /// <param name="connection">Owning connector connection.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ConnectorOperatorStatusPublisher(ConnectorConnection connection, ILogger logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Last allocated version (0 before the first successful allocation).</summary>
    public uint CurrentVersion => Volatile.Read(ref _version);

    /// <summary>
    /// Publish a status transition when it differs from the last published state.
    /// </summary>
    /// <param name="attached">Whether an operator is attached.</param>
    /// <param name="terminalActive">
    /// Whether terminal mode is active; requires <paramref name="attached"/> to be true.
    /// </param>
    /// <param name="cancellationToken">Cancels waiting for the gate or the protocol write.</param>
    /// <returns>A task that completes when the publish attempt finishes or is skipped.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="terminalActive"/> is true while <paramref name="attached"/> is false.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> canceled while waiting or writing (propagated).
    /// </exception>
    /// <remarks>
    /// Safe to call after dispose (no-op). Failures are logged and do not roll back version.
    /// Duplicate (attached, terminalActive) pairs after the first publish are skipped.
    /// </remarks>
    public async Task PublishAsync(
        bool attached,
        bool terminalActive,
        CancellationToken cancellationToken = default)
    {
        if (terminalActive && !attached)
        {
            throw new ArgumentException(
                "TerminalActive requires Attached to be true.",
                nameof(terminalActive));
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_hasPublished &&
                _attached == attached &&
                _terminalActive == terminalActive)
            {
                return;
            }

            var nextVersion = _version + 1;
            if (nextVersion == 0)
            {
                // Wrap is pathological; treat as dispose.
                _logger.LogError(
                    "OperatorStatus version overflow. ConnectionId={ConnectionId}",
                    _connection.ConnectionId);
                return;
            }

            _version = nextVersion;
            _attached = attached;
            _terminalActive = terminalActive;
            _hasPublished = true;

            var message = new OperatorStatusMessage
            {
                StateVersion = nextVersion,
                Attached = attached,
                TerminalActive = terminalActive
            };

            try
            {
                await _connection.Protocol.WriteOperatorStatusAsync(message, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug(
                    "Published OperatorStatus v{Version} Attached={Attached} TerminalActive={TerminalActive} ConnectionId={ConnectionId}",
                    nextVersion,
                    attached,
                    terminalActive,
                    _connection.ConnectionId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Version remains allocated so later publishes stay ordered; cleanup must continue.
                _logger.LogDebug(
                    ex,
                    "OperatorStatus publish failed. Version={Version}, ConnectionId={ConnectionId}",
                    nextVersion,
                    _connection.ConnectionId);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Best-effort Waiting notification used during teardown.</summary>
    /// <param name="cancellationToken">Passed through to <see cref="PublishAsync"/>.</param>
    /// <returns>Task from publishing attached=false, terminalActive=false.</returns>
    public Task TryPublishWaitingAsync(CancellationToken cancellationToken = default) =>
        PublishAsync(attached: false, terminalActive: false, cancellationToken);

    /// <summary>
    /// Marks the publisher disposed and releases the serialization gate.
    /// </summary>
    /// <remarks>Idempotent. Does not send a final wire message.</remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Gate held only to observe dispose; no I/O.
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
