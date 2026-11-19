// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorConnection.cs
//
// Purpose:
// Owns an authenticated connector TcpClient/SslStream pair, protocol session,
// lifetime cancellation, and OperatorStatus publisher for one connection generation.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Connector;

/// <summary>
/// Authenticated connector TLS connection with a binary protocol session over the <see cref="SslStream"/>.
/// </summary>
/// <remarks>
/// <para>
/// The protocol layer does not own or dispose the stream; <see cref="DisposeAsync"/> does.
/// Construction sets <see cref="State"/> to <see cref="ConnectorConnectionState.Authenticated"/>
/// after mTLS has already succeeded in the listener.
/// </para>
/// <para>
/// Identity is the validated client certificate thumbprint/subject captured at construction.
/// Disposing cancels <see cref="LifetimeToken"/>, disposes OperatorStatus and Protocol,
/// then the SslStream and TcpClient. Attachment/registry cleanup is performed by the
/// listener finally path, not by this type.
/// </para>
/// <para>
/// Not safe for concurrent dispose beyond the interlocked gate; protocol read/write
/// concurrency follows <see cref="ProtocolSession"/> rules.
/// </para>
/// </remarks>
internal sealed class ConnectorConnection : IAsyncDisposable
{
    /// <summary>Owned TCP client closed during <see cref="DisposeAsync"/>.</summary>
    private readonly TcpClient _tcpClient;

    /// <summary>Linked lifetime CTS canceled on dispose; exposed via <see cref="LifetimeToken"/>.</summary>
    private readonly CancellationTokenSource _lifetimeCts;

    /// <summary>Dispose gate; 0 = live, 1 = disposed.</summary>
    private int _disposed;

    /// <summary>
    /// Creates a connection that owns the TCP client, SSL stream, and lifetime CTS.
    /// </summary>
    /// <param name="connectionId">Opaque connection ID used by the tracker and registry.</param>
    /// <param name="certificateThumbprint">Validated mTLS client certificate thumbprint.</param>
    /// <param name="certificateSubject">Validated client certificate subject.</param>
    /// <param name="remoteEndPoint">Remote endpoint; formatted as <c>unknown</c> when null.</param>
    /// <param name="tcpClient">Owned TCP client.</param>
    /// <param name="sslStream">Owned mutually authenticated SSL stream.</param>
    /// <param name="negotiatedProtocol">Negotiated TLS protocol.</param>
    /// <param name="establishedAtUtc">UTC time when the authenticated connection was established.</param>
    /// <param name="lifetimeCts">Lifetime CTS linked to host stopping; canceled on dispose.</param>
    /// <param name="protocolLimits">Optional protocol limits; defaults to <see cref="ProtocolLimits.Default"/>.</param>
    /// <param name="logger">Optional logger for <see cref="OperatorStatus"/>; defaults to null logger.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="tcpClient"/>, <paramref name="sslStream"/>, or <paramref name="lifetimeCts"/> is null.
    /// </exception>
    public ConnectorConnection(
        string connectionId,
        string certificateThumbprint,
        string certificateSubject,
        EndPoint? remoteEndPoint,
        TcpClient tcpClient,
        SslStream sslStream,
        SslProtocols negotiatedProtocol,
        DateTimeOffset establishedAtUtc,
        CancellationTokenSource lifetimeCts,
        ProtocolLimits? protocolLimits = null,
        ILogger? logger = null)
    {
        ConnectionId = connectionId;
        CertificateThumbprint = certificateThumbprint;
        CertificateSubject = certificateSubject;
        RemoteEndPoint = remoteEndPoint?.ToString() ?? "unknown";
        _tcpClient = tcpClient ?? throw new ArgumentNullException(nameof(tcpClient));
        SslStream = sslStream ?? throw new ArgumentNullException(nameof(sslStream));
        NegotiatedProtocol = negotiatedProtocol;
        EstablishedAtUtc = establishedAtUtc;
        _lifetimeCts = lifetimeCts ?? throw new ArgumentNullException(nameof(lifetimeCts));
        Protocol = new ProtocolSession(sslStream, protocolLimits ?? ProtocolLimits.Default);
        OperatorStatus = new ConnectorOperatorStatusPublisher(
            this,
            logger ?? NullLogger.Instance);
        State = ConnectorConnectionState.Authenticated;
    }

    /// <summary>Opaque connection ID (tracker / registry key).</summary>
    public string ConnectionId { get; }

    /// <summary>Validated mTLS client certificate thumbprint (identity).</summary>
    public string CertificateThumbprint { get; }

    /// <summary>Validated client certificate subject string.</summary>
    public string CertificateSubject { get; }

    /// <summary>Formatted remote endpoint, or <c>unknown</c>.</summary>
    public string RemoteEndPoint { get; }

    /// <summary>Owned SSL stream carrying the binary protocol.</summary>
    public SslStream SslStream { get; }

    /// <summary>Protocol session over <see cref="SslStream"/>; disposed before the stream.</summary>
    public ProtocolSession Protocol { get; }

    /// <summary>On-change OperatorStatus publisher for this connection generation.</summary>
    public ConnectorOperatorStatusPublisher OperatorStatus { get; }

    /// <summary>Negotiated TLS protocol for this connection.</summary>
    public SslProtocols NegotiatedProtocol { get; }

    /// <summary>UTC time when the authenticated connection object was created.</summary>
    public DateTimeOffset EstablishedAtUtc { get; }

    /// <summary>Server session ID after successful Hello registration; null until registered.</summary>
    public string? RegisteredSessionId { get; set; }

    /// <summary>
    /// Live registry session after successful Hello registration; null until registered.
    /// </summary>
    /// <remarks>
    /// Cached so protocol activity can update <see cref="ConnectorSession.LastActivityUtc"/>
    /// without taking the registry's global lock on every frame (PERF-SRV-02). Assigned only after
    /// successful Hello registration; cleared on connector teardown after unregister. The registry
    /// remains the source of truth for membership.
    /// </remarks>
    public ConnectorSession? RegisteredSession { get; set; }

    /// <summary>Current connection lifecycle state.</summary>
    public ConnectorConnectionState State { get; private set; }

    /// <summary>Token canceled when the connection lifetime ends or dispose begins.</summary>
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

    /// <summary>Transitions <see cref="State"/> to <see cref="ConnectorConnectionState.Disconnecting"/>.</summary>
    public void MarkDisconnecting() => State = ConnectorConnectionState.Disconnecting;

    /// <summary>
    /// Cancels lifetime, disposes OperatorStatus and Protocol, then closes SSL and TCP resources.
    /// </summary>
    /// <remarks>
    /// Idempotent. Sets <see cref="State"/> to Disconnecting then Disconnected.
    /// Failures during individual resource closes are swallowed (best-effort).
    /// Does not unregister sessions or detach operators.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        State = ConnectorConnectionState.Disconnecting;

        try
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await OperatorStatus.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await Protocol.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await SslStream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort close.
        }

        try
        {
            _tcpClient.Dispose();
        }
        catch
        {
        }

        _lifetimeCts.Dispose();
        State = ConnectorConnectionState.Disconnected;
    }
}
