// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    IRemoteConsoleTransport.cs
//
// Purpose:
// Defines the outbound TCP/mTLS transport contract to TechConnect.Server,
// including connect lifecycle, serial/control send, and inbound event hooks.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Models;

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Outbound transport to the central TechConnect service over native TCP with mutual TLS.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must never disable TLS certificate validation or fall back to
/// cleartext. Production DI (<c>HostingExtensions.AddTechConnectClientCore</c>) registers
/// <c>TcpMtlsRemoteConsoleTransport</c>: outbound TCP with mutual TLS and
/// TechConnect.Protocol binary framing. This is the sole supported remote transport.
/// </para>
/// <para>
/// Serial octets and control payloads travel as framed protocol messages.
/// Completion of a send means the transport accepted the write — not that a remote
/// NOC client received it. Serial payloads must never be written to application logs.
/// </para>
/// <para>
/// Connect/disconnect are serialized by implementations. Send concurrency is
/// implementation-defined (send gate and/or protocol writer). Dispose via
/// <see cref="IAsyncDisposable.DisposeAsync"/> tears down any active session.
/// </para>
/// </remarks>
public interface IRemoteConsoleTransport : IAsyncDisposable
{
    /// <summary>
    /// Whether the transport currently has an authenticated, usable remote session.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Optional awaited remote→serial handler.
    /// </summary>
    /// <remarks>
    /// When set, transports await the handler for each inbound serial payload before
    /// reading the next frame so receive-loop backpressure applies and write failures can
    /// propagate to the receive loop. When unset, transports raise
    /// <see cref="SerialDataReceived"/> instead (not in addition).
    /// </remarks>
    Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SerialDataHandler { get; set; }

    /// <summary>
    /// Raised when remote serial octets arrive and no <see cref="SerialDataHandler"/> is set.
    /// </summary>
    /// <remarks>
    /// Used only as the fallback when <see cref="SerialDataHandler"/> is null.
    /// Payload bytes must never be logged; handlers should copy data they need to retain.
    /// </remarks>
    event EventHandler<ReadOnlyMemory<byte>>? SerialDataReceived;

    /// <summary>
    /// Raised for inbound control messages expressed as UTF-8 JSON text
    /// (ControlProtocol envelopes and binary-protocol control payloads surfaced as JSON).
    /// </summary>
    event EventHandler<string>? ControlMessageReceived;

    /// <summary>
    /// Raised when the transport enters a faulted state with a human-readable reason
    /// suitable for UI status (never containing secrets or serial payloads).
    /// </summary>
    event EventHandler<string>? Faulted;

    /// <summary>
    /// Establishes the authenticated remote session via TCP connect and mutual TLS.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels connect and handshake work.</param>
    /// <returns>A task that completes when the session is ready to send and receive.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sends raw serial octets to the remote side.
    /// </summary>
    /// <param name="data">Serial payload; must not be logged.</param>
    /// <param name="cancellationToken">Token that cancels the send.</param>
    /// <returns>
    /// A task that completes when the transport has accepted the framed write
    /// (not remote-client delivery acknowledgment).
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    Task SendSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>
    /// Sends a versioned control JSON message (or maps it onto protocol control frames).
    /// </summary>
    /// <param name="json">Control JSON text; must not contain secrets intended for logs.</param>
    /// <param name="cancellationToken">Token that cancels the send.</param>
    /// <returns>A task that completes when the control message has been written.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The transport has been disposed.</exception>
    Task SendControlMessageAsync(string json, CancellationToken cancellationToken);

    /// <summary>
    /// Gracefully tears down the remote session without disposing the transport instance.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels wait for teardown gates.</param>
    /// <returns>A task that completes when the session is disconnected.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    Task DisconnectAsync(CancellationToken cancellationToken);
}
