// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    InternalRadiusClient.cs
//
// Purpose:
// Per-server RADIUS UDP client with concurrent request correlation by
// packet identifier, response validation, and idempotent disposal.
//
// -----------------------------------------------------------------------------
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Authentication.Radius.Transport;

namespace TechConnect.Server.Authentication.Radius.Client;

/// <summary>
/// Per-server RADIUS UDP client with concurrent request correlation by packet identifier.
/// One instance owns one remote endpoint; use separate instances for primary and secondary servers.
/// </summary>
internal sealed class InternalRadiusClient : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Max Packet Size backing member.
    /// </summary>
    private const int MaxPacketSize = RadiusPacket.MaximumPacketLength;
    /// <summary>
    /// Identifier Offset backing member.
    /// </summary>
    private const int IdentifierOffset = 1;

    /// <summary>
    /// remote End Point backing member.
    /// </summary>
    private readonly IPEndPoint _remoteEndPoint;
    /// <summary>
    /// logger backing member.
    /// </summary>
    private readonly ILogger _logger;
    /// <summary>
    /// time Provider backing member.
    /// </summary>
    private readonly TimeProvider _timeProvider;
    /// <summary>
    /// identifiers backing member.
    /// </summary>
    private readonly RadiusIdentifierAllocator _identifiers;
    /// <summary>
    /// new.
    /// </summary>
    private readonly ConcurrentDictionary<byte, PendingExchange> _pending = new();
    /// <summary>
    /// socket backing member.
    /// </summary>
    private readonly Socket _socket;
    /// <summary>
    /// new.
    /// </summary>
    private readonly CancellationTokenSource _receiveCts = new();
    /// <summary>
    /// receive Loop backing member.
    /// </summary>
    private readonly Task _receiveLoop;
    /// <summary>
    /// disposed backing member.
    /// </summary>
    private int _disposed;

    /// <summary>
    /// Internal Radius Client.
    /// </summary>
    public InternalRadiusClient(
        IPEndPoint remoteEndPoint,
        ILogger? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        _remoteEndPoint = remoteEndPoint;
        _logger = logger ?? NullLogger.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _identifiers = new RadiusIdentifierAllocator(_timeProvider);

        _socket = new Socket(remoteEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Suppress ICMP Port Unreachable → ConnectionReset on UDP (Windows-specific).
                const int SioUdpConnreset = -1744830452;
                _socket.IOControl(SioUdpConnreset, [0], null);
            }

            _socket.Bind(new IPEndPoint(
                remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Any
                    : IPAddress.Any,
                0));
            _socket.Connect(remoteEndPoint);
        }
        catch
        {
            _socket.Dispose();
            _receiveCts.Dispose();
            throw;
        }

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
        _logger.LogInformation(
            "RADIUS UDP client started for {Endpoint}",
            _remoteEndPoint);
    }

    /// <summary>
    /// Creates Async.
    /// </summary>
    public static async Task<InternalRadiusClient> CreateAsync(
        string hostOrAddress,
        int port = RadiusEndpointResolver.DefaultAuthenticationPort,
        ILogger? logger = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = await RadiusEndpointResolver
            .ResolveAsync(hostOrAddress, port, cancellationToken)
            .ConfigureAwait(false);
        return new InternalRadiusClient(endpoint, logger, timeProvider);
    }

    /// <summary>
    /// Remote End Point backing member.
    /// </summary>
    public IPEndPoint RemoteEndPoint => _remoteEndPoint;

    /// <summary>
    /// Outstanding Requests backing member.
    /// </summary>
    public int OutstandingRequests => _identifiers.InUseCount;

    /// <summary>
    /// Sends a constructed RADIUS request and awaits a validated response.
    /// Allocates a unique packet identifier, overwriting the Identifier field of a request copy.
    /// </summary>
    public async Task<RadiusExchangeResult> ExchangeAsync(
        ReadOnlyMemory<byte> requestPacket,
        ReadOnlyMemory<byte> requestAuthenticator,
        ReadOnlyMemory<byte> sharedSecret,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (requestPacket.Length < RadiusPacket.MinimumPacketLength ||
            requestPacket.Length > MaxPacketSize)
        {
            return RadiusExchangeResult.InvalidResponse("Request packet length is outside RADIUS limits.");
        }

        if (requestAuthenticator.Length != RadiusAuthenticators.AuthenticatorLength)
        {
            return RadiusExchangeResult.InvalidResponse("Request Authenticator must be 16 bytes.");
        }

        if (sharedSecret.IsEmpty)
        {
            return RadiusExchangeResult.InvalidResponse("Shared secret must not be empty.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RadiusExchangeResult.Cancelled("Cancelled before send.");
        }

        if (!_identifiers.TryAllocate(out var identifier))
        {
            _logger.LogWarning(
                "RADIUS identifier space exhausted for {Endpoint} ({Capacity} outstanding/quarantined)",
                _remoteEndPoint,
                RadiusIdentifierAllocator.Capacity);
            throw new InvalidOperationException(
                $"RADIUS identifier space exhausted for {_remoteEndPoint}. " +
                $"At most {RadiusIdentifierAllocator.Capacity} concurrent outstanding requests are supported.");
        }

        var requestCopy = requestPacket.ToArray();
        requestCopy[IdentifierOffset] = identifier;
        // Ensure Request Authenticator bytes in the packet match the provided authenticator.
        requestAuthenticator.Span.CopyTo(requestCopy.AsSpan(RadiusAuthenticators.AuthenticatorOffset, 16));

        var secretCopy = sharedSecret.ToArray();
        var requestAuthCopy = requestAuthenticator.ToArray();
        var tcs = new TaskCompletionSource<RadiusExchangeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = new PendingExchange(
            identifier,
            requestAuthCopy,
            secretCopy,
            tcs,
            timeout);

        if (!_pending.TryAdd(identifier, pending))
        {
            CryptographicOperations.ZeroMemory(secretCopy);
            CryptographicOperations.ZeroMemory(requestAuthCopy);
            _identifiers.Release(identifier, TimeSpan.Zero);
            throw new InvalidOperationException(
                $"Identifier {identifier} is already pending; allocator invariant violated.");
        }

        pending.CallerToken = cancellationToken;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var registration = timeoutCts.Token.Register(
            static state =>
            {
                var exchange = (PendingExchange)state!;
                var cancelledByCaller = exchange.CallerToken.IsCancellationRequested;
                exchange.TryComplete(
                    cancelledByCaller
                        ? RadiusExchangeResult.Cancelled("Cancelled while awaiting response.")
                        : RadiusExchangeResult.Unavailable("Request timed out."));
            },
            pending);
        pending.TimeoutRegistration = registration;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _logger.LogDebug(
                "RADIUS request sent to {Endpoint} id={Identifier}",
                _remoteEndPoint,
                identifier);

            try
            {
                await _socket.SendAsync(requestCopy, SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                pending.TryComplete(RadiusExchangeResult.Cancelled("Cancelled during send."));
            }
            catch (ObjectDisposedException)
            {
                pending.TryComplete(RadiusExchangeResult.Cancelled("RADIUS client disposed during send."));
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "RADIUS send failed to {Endpoint} id={Identifier}", _remoteEndPoint, identifier);
                pending.TryComplete(RadiusExchangeResult.Unavailable($"Transport send failure: {ex.SocketErrorCode}."));
            }

            var result = await tcs.Task.ConfigureAwait(false);
            stopwatch.Stop();
            _logger.LogDebug(
                "RADIUS exchange completed for {Endpoint} id={Identifier} outcome={Outcome} in {ElapsedMs}ms",
                _remoteEndPoint,
                identifier,
                result.Outcome,
                stopwatch.ElapsedMilliseconds);
            return result;
        }
        finally
        {
            registration.Dispose();
            if (_pending.TryRemove(identifier, out var removed))
            {
                removed.DisposeSensitive();
            }

            _identifiers.Release(identifier, timeout);
        }
    }

    /// <summary>
    /// Runs the Receive Loop Async loop until cancellation or disposal.
    /// </summary>
    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxPacketSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            int received;
            try
            {
                // Socket is connected to the single remote endpoint — ReceiveAsync is sufficient
                // and avoids cross-endpoint delivery on this instance.
                received = await _socket
                    .ReceiveAsync(buffer.AsMemory(0, buffer.Length), SocketFlags.None, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "RADIUS receive loop ending after cancellation");
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "RADIUS receive error from socket bound for {Endpoint}", _remoteEndPoint);
                continue;
            }

            if (received <= 0)
            {
                continue;
            }

            ProcessDatagram(buffer.AsSpan(0, received));
        }
    }

    /// <summary>
    /// Processes Datagram.
    /// </summary>
    private void ProcessDatagram(ReadOnlySpan<byte> datagram)
    {
        // Identifier is octet 2 (offset 1). If present, associate with a pending request so a
        // malformed datagram becomes InvalidResponse rather than a later timeout/Unavailable.
        if (datagram.Length > IdentifierOffset &&
            _pending.TryGetValue(datagram[IdentifierOffset], out var shortPending) &&
            datagram.Length < RadiusPacket.MinimumPacketLength)
        {
            shortPending.TryComplete(
                RadiusExchangeResult.InvalidResponse(
                    $"Truncated RADIUS datagram ({datagram.Length} bytes)."));
            return;
        }

        if (datagram.Length < RadiusPacket.MinimumPacketLength)
        {
            _logger.LogDebug(
                "Ignoring truncated RADIUS datagram ({Length} bytes) from {Endpoint}",
                datagram.Length,
                _remoteEndPoint);
            return;
        }

        var identifier = datagram[IdentifierOffset];
        if (!_pending.TryGetValue(identifier, out var pending))
        {
            _logger.LogDebug(
                "Ignoring unmatched RADIUS datagram id={Identifier} from {Endpoint}",
                identifier,
                _remoteEndPoint);
            return;
        }

        // Copy before completion paths return pooled/stack buffers.
        var copy = datagram.ToArray();
        var outcome = ValidateAndClassify(copy, pending);
        pending.TryComplete(outcome);
    }

    /// <summary>
    /// Validates And Classify.
    /// </summary>
    private RadiusExchangeResult ValidateAndClassify(byte[] datagram, PendingExchange pending)
    {
        // 1–3. Structural parse (length checks inside RadiusPacket).
        var packet = new RadiusPacket(datagram);
        if (!packet.Valid)
        {
            return RadiusExchangeResult.InvalidResponse("Malformed RADIUS response packet.");
        }

        if (packet.Identifier != pending.Identifier)
        {
            return RadiusExchangeResult.InvalidResponse("Response identifier mismatch.");
        }

        // 4–5. Response Authenticator before trusting code/attributes.
        if (!RadiusAuthenticators.VerifyResponseAuthenticator(
                packet.RawData,
                pending.RequestAuthenticator,
                pending.SharedSecret))
        {
            // Failed crypto may also indicate a late response for a previously quarantined
            // use of this identifier; do not complete the current request as InvalidResponse
            // unless we are confident it is for this request. Because identifiers are
            // quarantined after release, an active pending entry should only see responses
            // intended for this outstanding request — treat failed verification as InvalidResponse.
            return RadiusExchangeResult.InvalidResponse("Response Authenticator verification failed.");
        }

        // 6. Message-Authenticator when present.
        if (packet.TryGetAttribute(RadiusAttributeType.MessageAuthenticator, out _))
        {
            if (!RadiusMessageAuthenticator.TryVerify(packet.RawData, pending.SharedSecret))
            {
                return RadiusExchangeResult.InvalidResponse("Message-Authenticator verification failed.");
            }
        }

        // 7. Only now expose code.
        return packet.PacketType switch
        {
            RadiusPacketCode.AccessAccept => RadiusExchangeResult.Accept(packet),
            RadiusPacketCode.AccessReject => RadiusExchangeResult.Reject(packet),
            _ => RadiusExchangeResult.InvalidResponse(
                $"Unexpected RADIUS response code '{packet.PacketType}'.")
        };
    }

    /// <summary>
    /// Releases owned resources; subsequent calls are no-ops when guarded.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Releases owned resources; subsequent calls are no-ops when guarded.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _receiveCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        foreach (var pair in _pending)
        {
            pair.Value.TryComplete(RadiusExchangeResult.Cancelled("RADIUS client disposed."));
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing RADIUS socket for {Endpoint}", _remoteEndPoint);
        }

        try
        {
            await _receiveLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RADIUS receive loop ended with error for {Endpoint}", _remoteEndPoint);
        }

        _receiveCts.Dispose();
        _logger.LogInformation("RADIUS UDP client disposed for {Endpoint}", _remoteEndPoint);
    }

    /// <summary>
    /// Defines PendingExchange for InternalRadiusClient.cs.
    /// </summary>
    private sealed class PendingExchange
    {
        /// <summary>
        /// completed backing member.
        /// </summary>
        private int _completed;

        /// <summary>
        /// Pending Exchange.
        /// </summary>
        public PendingExchange(
            byte identifier,
            byte[] requestAuthenticator,
            byte[] sharedSecret,
            TaskCompletionSource<RadiusExchangeResult> completion,
            TimeSpan timeout)
        {
            Identifier = identifier;
            RequestAuthenticator = requestAuthenticator;
            SharedSecret = sharedSecret;
            Completion = completion;
            Timeout = timeout;
        }

        /// <summary>
        /// Identifier backing member.
        /// </summary>
        public byte Identifier { get; }

        /// <summary>
        /// Request Authenticator backing member.
        /// </summary>
        public byte[] RequestAuthenticator { get; }

        /// <summary>
        /// Shared Secret backing member.
        /// </summary>
        public byte[] SharedSecret { get; }

        /// <summary>
        /// Completion backing member.
        /// </summary>
        public TaskCompletionSource<RadiusExchangeResult> Completion { get; }

        /// <summary>
        /// Timeout backing member.
        /// </summary>
        public TimeSpan Timeout { get; }

        /// <summary>
        /// Caller Token backing member.
        /// </summary>
        public CancellationToken CallerToken { get; set; }

        /// <summary>
        /// Timeout Registration backing member.
        /// </summary>
        public CancellationTokenRegistration TimeoutRegistration { get; set; }

        /// <summary>
        /// Attempts to Complete without throwing for ordinary failures.
        /// </summary>
        public bool TryComplete(RadiusExchangeResult result)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return false;
            }

            return Completion.TrySetResult(result);
        }

        /// <summary>
        /// Releases owned resources; subsequent calls are no-ops when guarded.
        /// </summary>
        public void DisposeSensitive()
        {
            CryptographicOperations.ZeroMemory(RequestAuthenticator);
            CryptographicOperations.ZeroMemory(SharedSecret);
            TimeoutRegistration.Dispose();
        }
    }
}
