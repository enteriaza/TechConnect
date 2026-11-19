// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ReconnectCoordinator.cs
//
// Purpose:
// Runs connect attempts with exponential backoff and symmetric jitter until cancel.
//
// -----------------------------------------------------------------------------

using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Services;

/// <summary>
/// Session-scoped reconnect loop with exponential backoff and jitter.
/// </summary>
/// <remarks>
/// <para>
/// Invokes the supplied connect attempt repeatedly until cancellation or a permanent
/// <see cref="RemoteTransportConfigurationException"/>. Successful return from the
/// attempt means the transport session ran and then ended; backoff is reset before
/// the next reconnect. Transient failures delay via <see cref="NextDelay"/>.
/// This type owns the single per-attempt connect-failure log (concise warning for
/// expected socket/transport failures; error with exception for unexpected faults).
/// </para>
/// <para>
/// Only one <see cref="RunAsync"/> may be active on an instance at a time
/// (<see cref="IsRunning"/>). Delays use <see cref="TimeProvider"/> so tests can
/// advance time. Backoff state is guarded by an internal lock; <see cref="ResetBackoff"/>
/// may be called from the connect path after a successful transport connect.
/// </para>
/// </remarks>
internal sealed class ReconnectCoordinator : IReconnectCoordinator
{
    /// <summary>Reconnect delay options (initial/max/multiplier/jitter).</summary>
    private readonly IOptions<RemoteServiceOptions> _options;

    /// <summary>Clock for cancellable backoff delays.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Source of [0,1) values for symmetric jitter.</summary>
    private readonly IJitterProvider _jitterProvider;

    /// <summary>Structured logger for attempt/backoff outcomes.</summary>
    private readonly ILogger<ReconnectCoordinator> _logger;

    /// <summary>Guards <see cref="_currentDelaySeconds"/>.</summary>
    private readonly object _gate = new();

    /// <summary>
    /// Base delay (seconds) used for the next failure backoff before multiplier advance.
    /// </summary>
    private double _currentDelaySeconds;

    /// <summary>0 = idle, 1 = <see cref="RunAsync"/> in progress.</summary>
    private int _running;

    /// <summary>1-based attempt counter for the current <see cref="RunAsync"/> loop.</summary>
    private int _attempt;

    /// <summary>
    /// Initializes coordinator state and seeds backoff from options.
    /// </summary>
    public ReconnectCoordinator(
        IOptions<RemoteServiceOptions> options,
        TimeProvider timeProvider,
        IJitterProvider jitterProvider,
        ILogger<ReconnectCoordinator> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _jitterProvider = jitterProvider ?? throw new ArgumentNullException(nameof(jitterProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ResetBackoff();
    }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <inheritdoc />
    /// <remarks>
    /// Throws if a loop is already running. Permanent configuration errors stop the
    /// loop without further retries. Cancellation ends the loop cleanly.
    /// </remarks>
    public async Task RunAsync(Func<CancellationToken, Task> connectAttempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectAttempt);

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("A reconnect loop is already running for this session.");
        }

        try
        {
            ResetBackoff();
            _attempt = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                _attempt++;
                var attempt = _attempt;
                var isReconnect = attempt > 1;

                try
                {
                    _logger.LogDebug(
                        "{AttemptKind} attempt {Attempt}",
                        isReconnect ? "Remote reconnect" : "Remote connect",
                        attempt);

                    await connectAttempt(cancellationToken).ConfigureAwait(false);

                    // Successful session ended (transport dropped). Reset backoff before retrying.
                    ResetBackoff();
                    _logger.LogInformation(
                        "Remote transport session ended after attempt {Attempt}; will reconnect",
                        attempt);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Remote reconnect loop cancelled");
                    break;
                }
                catch (RemoteTransportConfigurationException ex)
                {
                    // Expected misconfiguration (e.g. empty Host / cert): concise log, no stack trace.
                    _logger.LogWarning(
                        "Permanent remote configuration error on attempt {Attempt}; stopping reconnect loop. {Reason}",
                        attempt,
                        ex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var delay = NextDelay();
                    LogConnectAttemptFailure(ex, attempt, delay);

                    try
                    {
                        await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation("Remote reconnect loop cancelled during backoff");
                        break;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Sets the next base delay to <c>Reconnect.InitialDelaySeconds</c> (floored at 0).
    /// Called at construction, at loop start, after a successful session ends, and by
    /// callers after a successful transport connect.
    /// </remarks>
    public void ResetBackoff()
    {
        var initial = Math.Max(0, _options.Value.Reconnect.InitialDelaySeconds);
        lock (_gate)
        {
            _currentDelaySeconds = initial;
        }
    }

    /// <summary>
    /// Emits the single connect-failure log for this attempt (expected = concise warning, unexpected = error with exception).
    /// </summary>
    /// <param name="ex">Failure thrown by the connect attempt.</param>
    /// <param name="attempt">1-based attempt number.</param>
    /// <param name="delay">Backoff delay before the next retry.</param>
    private void LogConnectAttemptFailure(Exception ex, int attempt, TimeSpan delay)
    {
        var options = _options.Value;
        var host = string.IsNullOrWhiteSpace(options.Host) ? "(unconfigured)" : options.Host.Trim();
        var port = options.Port;

        if (IsExpectedConnectFailure(ex))
        {
            // Do not pass the exception object — avoids stack traces for ordinary reconnect noise.
            _logger.LogWarning(
                "Remote connection to {Host}:{Port} failed: {Reason}. Attempt {Attempt}; retrying in {DelaySeconds:0.0}s.",
                host,
                port,
                FormatConnectFailureReason(ex),
                attempt,
                delay.TotalSeconds);
            return;
        }

        _logger.LogError(
            ex,
            "Unexpected remote connect failure to {Host}:{Port} on attempt {Attempt}; retrying in {DelaySeconds:0.0}s.",
            host,
            port,
            attempt,
            delay.TotalSeconds);
    }

    /// <summary>
    /// Returns whether <paramref name="ex"/> is an ordinary operational connect failure.
    /// </summary>
    /// <remarks>
    /// Socket/TLS connect failures from the transport are wrapped as
    /// <see cref="RemoteTransportException"/> or <see cref="RemoteTransportUnavailableException"/>.
    /// Does not treat arbitrary <see cref="Exception"/> as expected.
    /// </remarks>
    internal static bool IsExpectedConnectFailure(Exception ex)
    {
        if (ex is RemoteTransportUnavailableException or RemoteTransportException or SocketException)
        {
            return true;
        }

        // Some platforms surface connect failures as IOException wrapping SocketException.
        return FindSocketException(ex) is not null && ex is IOException;
    }

    /// <summary>
    /// Builds a short technician-facing reason without stack traces or exception type names.
    /// </summary>
    internal static string FormatConnectFailureReason(Exception ex)
    {
        var socket = FindSocketException(ex);
        if (socket is not null)
        {
            return FormatSocketError(socket);
        }

        if (ex is RemoteTransportUnavailableException unavailable)
        {
            return string.IsNullOrWhiteSpace(unavailable.Message)
                ? "Remote endpoint unavailable"
                : unavailable.Message;
        }

        if (ex is RemoteTransportException remote)
        {
            if (remote.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                return "Connection timed out";
            }

            return string.IsNullOrWhiteSpace(remote.Message)
                ? "Remote connection failed"
                : remote.Message;
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
    }

    /// <summary>
    /// Maps <see cref="SocketError"/> values to concise human-readable phrases.
    /// </summary>
    internal static string FormatSocketError(SocketException ex) =>
        ex.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Connection refused",
            SocketError.TimedOut => "Connection timed out",
            SocketError.HostUnreachable => "Host unreachable",
            SocketError.NetworkUnreachable => "Network unreachable",
            SocketError.NetworkDown => "Network down",
            SocketError.AddressNotAvailable => "Address not available",
            SocketError.HostNotFound => "Host not found",
            SocketError.TryAgain => "Temporary name resolution failure",
            SocketError.ConnectionReset => "Connection reset",
            SocketError.Shutdown => "Connection shutdown",
            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? ex.SocketErrorCode.ToString()
                : ex.Message.TrimEnd('.')
        };

    /// <summary>
    /// Walks inner exceptions for a <see cref="SocketException"/>.
    /// </summary>
    private static SocketException? FindSocketException(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException socket)
            {
                return socket;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes the delay for the current failure and advances the exponential base.
    /// </summary>
    /// <returns>
    /// Delay of <c>baseSeconds + jitter</c> where jitter is symmetric in
    /// <c>± baseSeconds * JitterFactor</c> (factor clamped to [0,1]), floored at zero,
    /// then capped at <see cref="ReconnectOptions.MaxDelaySeconds"/> so jitter cannot
    /// push the effective wait above the configured ceiling.
    /// The stored base is then updated to
    /// <c>min(MaxDelaySeconds, max(InitialDelaySeconds, current * BackoffMultiplier))</c>
    /// for the next failure.
    /// </returns>
    /// <remarks>
    /// Jitter uses <see cref="IJitterProvider.NextDouble"/> mapped to [-1, 1].
    /// This delay gates reconnect attempts only; it does not imply remote service health.
    /// </remarks>
    private TimeSpan NextDelay()
    {
        var reconnect = _options.Value.Reconnect;
        var maxDelaySeconds = Math.Max(1, reconnect.MaxDelaySeconds);
        double baseSeconds;
        lock (_gate)
        {
            baseSeconds = _currentDelaySeconds;
            var next = Math.Min(
                maxDelaySeconds,
                Math.Max(reconnect.InitialDelaySeconds, _currentDelaySeconds * reconnect.BackoffMultiplier));
            _currentDelaySeconds = next;
        }

        var jitterFactor = Math.Clamp(reconnect.JitterFactor, 0.0, 1.0);
        var jitterRange = baseSeconds * jitterFactor;
        var jitter = jitterRange <= 0
            ? 0
            : (_jitterProvider.NextDouble() * 2.0 - 1.0) * jitterRange;

        // Cap after jitter so positive jitter cannot exceed MaxDelaySeconds.
        var delayed = Math.Min(maxDelaySeconds, Math.Max(0, baseSeconds + jitter));
        return TimeSpan.FromSeconds(delayed);
    }
}
