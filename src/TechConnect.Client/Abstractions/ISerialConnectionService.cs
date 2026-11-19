// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ISerialConnectionService.cs
//
// Purpose:
// Owns the physical serial port lifecycle, RX publication, TX writes, live
// configuration apply, and explicit loopback probe diagnostics.
//
// -----------------------------------------------------------------------------

using System.Threading.Channels;
using TechConnect.Client.Models;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Owns the physical serial port. Lifecycle is independent of remote connectivity.
/// </summary>
/// <remarks>
/// <para>
/// Open/close/write/apply operations are serialized. An unbounded
/// <see cref="Incoming"/> channel republishes RX chunks for remote bridging only
/// while <see cref="SetRemoteIngressEnabled"/> is true; the channel completes when
/// the port closes. Payloads must never be written to application logs.
/// </para>
/// <para>
/// <see cref="DataTransferred"/> and <see cref="Faulted"/> may be raised on
/// background threads; UI subscribers must marshal as needed.
/// </para>
/// </remarks>
public interface ISerialConnectionService : IAsyncDisposable
{
    /// <summary>
    /// Whether a serial port is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Name of the open COM port, or <see langword="null"/> when closed.
    /// </summary>
    string? PortName { get; }

    /// <summary>
    /// Last requested/applied <see cref="SerialPortConfiguration"/> (defaults when never opened).
    /// </summary>
    /// <remarks>
    /// For live driver settings after open, prefer <see cref="GetEffectiveSettings"/>.
    /// </remarks>
    SerialPortConfiguration CurrentConfiguration { get; }

    /// <summary>
    /// RX stream for remote session consumers. Completes when the port closes.
    /// </summary>
    /// <remarks>
    /// Multiple readers are allowed. Chunks are written only while remote ingress is
    /// enabled (see <see cref="SetRemoteIngressEnabled"/>). Payloads must never be
    /// logged. Before the first open, the channel may already be completed so idle
    /// consumers do not hang.
    /// </remarks>
    ChannelReader<SerialDataChunk> Incoming { get; }

    /// <summary>
    /// Whether RX octets are currently published onto <see cref="Incoming"/> for remote bridging.
    /// </summary>
    /// <remarks>
    /// Independent of whether the serial port is open. Defaults to <see langword="false"/>
    /// so local-console-only operation cannot accumulate unbounded remote-queue memory.
    /// </remarks>
    bool IsRemoteIngressEnabled { get; }

    /// <summary>
    /// Enables or disables publication of RX octets onto <see cref="Incoming"/>.
    /// </summary>
    /// <param name="enabled">
    /// When <see langword="true"/>, subsequent RX chunks are written to <see cref="Incoming"/>.
    /// When <see langword="false"/>, RX continues for the local console via
    /// <see cref="DataTransferred"/> but is not enqueued for remote delivery.
    /// </param>
    /// <remarks>
    /// Remote session code must enable ingress only while a bridge consumer is (or will
    /// immediately be) running, and disable it after that consumer has stopped. Disabling
    /// waits for any in-flight publish that already observed the enabled state, so a following
    /// <see cref="DrainPendingRemoteIngress"/> cannot miss a late enqueue. Disabling does not
    /// close the serial port. Payloads are never logged.
    /// </remarks>
    void SetRemoteIngressEnabled(bool enabled);

    /// <summary>
    /// Drains any chunks already queued on <see cref="Incoming"/> without forwarding them.
    /// </summary>
    /// <returns>Total payload bytes discarded from the channel.</returns>
    /// <remarks>
    /// Intended for use after the remote bridge consumer has stopped so leftover items
    /// cannot retain memory. Counts toward skipped-remote-ingress diagnostics. Does not
    /// log payloads and does not affect the serial port.
    /// </remarks>
    int DrainPendingRemoteIngress();

    /// <summary>
    /// Returns cumulative serial traffic counters for the current open cycle.
    /// </summary>
    /// <returns>Point-in-time <see cref="SerialTrafficStats"/> copy.</returns>
    /// <remarks>
    /// <c>BytesReceived</c> is driver RX. <c>BytesTransmitted</c> is counted after
    /// write + flush + driver output-queue drain — not remote-device acknowledgment.
    /// </remarks>
    SerialTrafficStats GetStats();

    /// <summary>
    /// Effective settings from the live port instance, or <see langword="null"/> when closed.
    /// </summary>
    /// <returns>Driver settings after open, not merely the requested configuration.</returns>
    SerialPortEffectiveSettings? GetEffectiveSettings();

    /// <summary>
    /// RX reader observability counters and last error text (no payloads).
    /// </summary>
    /// <returns>Point-in-time <see cref="SerialReceiveDiagnostics"/> copy.</returns>
    SerialReceiveDiagnostics GetReceiveDiagnostics();

    /// <summary>
    /// Raised for each RX or TX chunk published for UI display.
    /// </summary>
    /// <remarks>
    /// <para>
    /// TX chunks are raised after a successful local write path with a durable payload copy;
    /// they are not published onto <see cref="Incoming"/>. Payloads must never be logged.
    /// </para>
    /// <para>
    /// <b>RX lifetime (PERF-CLI-01):</b> When <see cref="IsRemoteIngressEnabled"/> was false at
    /// the reader’s ownership decision for that read, <see cref="SerialDataChunk.Data"/> may be
    /// an ephemeral view over the reusable read buffer — valid only until the handler returns.
    /// The production UI copies into <c>SerialConsoleBuffer</c> synchronously. Do not retain,
    /// queue, or await on that memory. When ingress was enabled for that read, the payload is
    /// durable and may also appear on <see cref="Incoming"/>. Ephemeral payloads are never
    /// written to <see cref="Incoming"/>. The event API cannot enforce this; subscribers must
    /// honour it.
    /// </para>
    /// </remarks>
    event EventHandler<SerialDataChunk>? DataTransferred;

    /// <summary>
    /// Raised when the serial path faults with a human-readable reason
    /// (never secrets or serial payloads).
    /// </summary>
    event EventHandler<string>? Faulted;

    /// <summary>
    /// Opens the named COM port and starts the RX reader.
    /// </summary>
    /// <param name="portName">COM port name (for example <c>COM3</c>).</param>
    /// <param name="initialConfiguration">
    /// Optional settings; when <see langword="null"/>,
    /// <see cref="SerialPortConfiguration.CreateDefault"/> is used.
    /// </param>
    /// <param name="cancellationToken">Token that cancels waiting for the lifecycle gate.</param>
    /// <returns>A task that completes when the port is open and RX is running.</returns>
    /// <exception cref="ArgumentException"><paramref name="portName"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">A port is already open.</exception>
    /// <exception cref="ObjectDisposedException">The service has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task OpenAsync(string portName, SerialPortConfiguration? initialConfiguration, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the open port, stops the RX reader, and completes <see cref="Incoming"/>.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels waiting for the lifecycle gate.</param>
    /// <returns>A task that completes when teardown finishes; no-op when already closed/disposed.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task CloseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes octets to the open serial port and waits for the driver output queue to drain.
    /// </summary>
    /// <param name="data">Payload to transmit; must not be logged.</param>
    /// <param name="cancellationToken">Token that cancels the write/flush/drain.</param>
    /// <returns>A task that completes after driver drain for this write.</returns>
    /// <remarks>
    /// Empty payloads are ignored. Successful writes update TX stats and raise
    /// <see cref="DataTransferred"/>; they are not written to <see cref="Incoming"/>.
    /// Driver drain means the Windows serial driver reported an empty output queue for
    /// this write — not that a remote serial device acknowledged the bytes.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The port is not open.</exception>
    /// <exception cref="ObjectDisposedException">The service has been disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Applies serial communication parameters to the open port.
    /// </summary>
    /// <param name="configuration">Validated parameters from an authorized remote session.</param>
    /// <param name="cancellationToken">Token that cancels waiting for the lifecycle gate.</param>
    /// <returns>A task that completes when settings have been applied.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task ApplyConfigurationAsync(SerialPortConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>
    /// Explicit diagnostic: write <paramref name="probe"/> and wait for the same bytes on RX.
    /// </summary>
    /// <param name="probe">Bytes to write; must not be logged.</param>
    /// <param name="timeout">Maximum time to wait for a matching RX sequence.</param>
    /// <param name="cancellationToken">Token that cancels the probe.</param>
    /// <returns>
    /// Structured result including match/timeout flags. Does not claim hardware RX
    /// validation unless <see cref="SerialLoopbackProbeResult.ProbeMatched"/> is true.
    /// </returns>
    /// <remarks>
    /// Requires a physical TX↔RX loopback (or a test double that echoes). Never called automatically.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task<SerialLoopbackProbeResult> RunLoopbackProbeAsync(
        ReadOnlyMemory<byte> probe,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
