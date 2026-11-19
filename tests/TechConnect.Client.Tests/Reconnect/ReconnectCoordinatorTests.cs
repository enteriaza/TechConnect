// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    ReconnectCoordinatorTests.cs
//
// Purpose:
// Verifies reconnect backoff, jitter, cancellation, and scheduling against fake time.
//
// -----------------------------------------------------------------------------

using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Reconnect;

/// <summary>
/// Unit tests for ReconnectCoordinator backoff, jitter, cancellation, and concurrency.
/// </summary>
public sealed class ReconnectCoordinatorTests
{
    /// <summary>
    /// Builds RemoteServiceOptions with the given reconnect timing parameters.
    /// </summary>
    private static RemoteServiceOptions CreateOptions(
        int initialDelaySeconds = 2,
        int maxDelaySeconds = 30,
        double backoffMultiplier = 2.0,
        double jitterFactor = 0.2) =>
        new()
        {
            Host = "127.0.0.1",
            Port = 9443,
            Reconnect = new ReconnectOptions
            {
                InitialDelaySeconds = initialDelaySeconds,
                MaxDelaySeconds = maxDelaySeconds,
                BackoffMultiplier = backoffMultiplier,
                JitterFactor = jitterFactor
            }
        };

    /// <summary>
    /// Parses retry delays logged by <see cref="ReconnectCoordinator"/>.
    /// </summary>
    private static IReadOnlyList<double> LoggedRetryDelays(CollectingLogger<ReconnectCoordinator> logger) =>
        logger.Entries
            .Select(e => System.Text.RegularExpressions.Regex.Match(
                e.Message,
                @"retrying in (?<delay>\d+(?:\.\d+)?)s",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Where(m => m.Success)
            .Select(m => double.Parse(m.Groups["delay"].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>
    /// Creates a ReconnectCoordinator over fake time, options, jitter, and logger.
    /// </summary>
    private static ReconnectCoordinator CreateCoordinator(
        FakeTimeProvider time,
        RemoteServiceOptions options,
        TechConnect.Client.Abstractions.IJitterProvider? jitter = null,
        CollectingLogger<ReconnectCoordinator>? logger = null)
    {
        return new ReconnectCoordinator(
            new OptionsMonitorStub<RemoteServiceOptions>(options),
            time,
            jitter ?? new FixedJitterProvider(0.5),
            logger ?? new CollectingLogger<ReconnectCoordinator>());
    }

    /// <summary>
    /// Scenario: connect succeeds then the operation reports a drop.
    /// Expected: coordinator retries after the configured delay.
    /// </summary>
    [Fact]
    public async Task SuccessThenDrop_Retries()
    {
        var time = new FakeTimeProvider();
        var coordinator = CreateCoordinator(time, CreateOptions(initialDelaySeconds: 1, jitterFactor: 0));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n == 1)
            {
                // Connected session then "drops" by returning.
                await Task.CompletedTask;
                return;
            }

            cts.Cancel();
            ct.ThrowIfCancellationRequested();
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 2, TimeSpan.FromSeconds(5));
        await run;
        attempts.Should().BeGreaterThanOrEqualTo(2);
        coordinator.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: repeated failures with jitter factor 0.
    /// Expected: delays follow exponential backoff without jitter noise.
    /// </summary>
    [Fact]
    public async Task Failure_UsesExponentialBackoff_WithZeroJitter()
    {
        var time = new FakeTimeProvider();
        // FixedJitterProvider(0.5) => jitter contribution is zero.
        var options = CreateOptions(initialDelaySeconds: 2, maxDelaySeconds: 60, backoffMultiplier: 2.0, jitterFactor: 0.2);
        var coordinator = CreateCoordinator(time, options, new FixedJitterProvider(0.5));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n > 4)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            throw new InvalidOperationException($"fail-{n}");
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1, TimeSpan.FromSeconds(2));

        var before2 = time.GetUtcNow();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 2,
            step: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromSeconds(5));
        (time.GetUtcNow() - before2).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));

        var before3 = time.GetUtcNow();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 3,
            step: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromSeconds(5));
        (time.GetUtcNow() - before3).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4));

        var before4 = time.GetUtcNow();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 4,
            step: TimeSpan.FromMilliseconds(200),
            timeout: TimeSpan.FromSeconds(5));
        (time.GetUtcNow() - before4).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));

        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;

        coordinator.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: backoff would exceed MaxDelaySeconds.
    /// Expected: delay is capped at the configured maximum.
    /// </summary>
    [Fact]
    public async Task Delay_IsCappedAtMaxDelay()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 2, maxDelaySeconds: 8, backoffMultiplier: 2.0, jitterFactor: 0);
        var coordinator = CreateCoordinator(time, options, new FixedJitterProvider(0.5), logger);
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new InvalidOperationException("fail"));
        }, cts.Token);

        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 5,
            step: TimeSpan.FromMilliseconds(500),
            timeout: TimeSpan.FromSeconds(10));

        cts.Cancel();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;

        attempts.Should().BeGreaterThanOrEqualTo(5);
        coordinator.IsRunning.Should().BeFalse();
        LoggedRetryDelays(logger).Should().OnlyContain(d => d <= 8.0);
        LoggedRetryDelays(logger).Should().Contain(d => d == 8.0);
    }

    /// <summary>
    /// Scenario: production defaults for reconnect timing.
    /// Expected: initial delay 2s, max delay 30s.
    /// </summary>
    [Fact]
    public void DefaultReconnectOptions_InitialTwoSeconds_MaxThirtySeconds()
    {
        var reconnect = new ReconnectOptions();
        reconnect.InitialDelaySeconds.Should().Be(2);
        reconnect.MaxDelaySeconds.Should().Be(30);
        reconnect.BackoffMultiplier.Should().Be(2.0);
        reconnect.JitterFactor.Should().Be(0.2);
    }

    /// <summary>
    /// Scenario: exponential growth under the 30s ceiling with zero jitter.
    /// Expected: 2 → 4 → 8 → 16 → 30 → 30 …
    /// </summary>
    [Fact]
    public async Task ExponentialBackoff_ReachesThirtySecondCeiling()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 2, maxDelaySeconds: 30, backoffMultiplier: 2.0, jitterFactor: 0);
        var coordinator = CreateCoordinator(time, options, new FixedJitterProvider(0.5), logger);
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new RemoteTransportUnavailableException("down"));
        }, cts.Token);

        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 7,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(30));

        cts.Cancel();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;

        var delays = LoggedRetryDelays(logger);
        delays.Should().HaveCountGreaterThanOrEqualTo(6);
        delays[0].Should().Be(2.0);
        delays[1].Should().Be(4.0);
        delays[2].Should().Be(8.0);
        delays[3].Should().Be(16.0);
        delays[4].Should().Be(30.0);
        delays.Skip(4).Should().OnlyContain(d => d == 30.0);
        delays.Should().OnlyContain(d => d <= 30.0);
        attempts.Should().BeGreaterThanOrEqualTo(7);
    }

    /// <summary>
    /// Scenario: maximum positive jitter at the 30s base.
    /// Expected: effective delay stays ≤ 30s (post-jitter cap).
    /// </summary>
    [Fact]
    public async Task PositiveJitter_CannotExceedThirtySecondCeiling()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        // NextDouble=1 => +20% jitter; without a post-jitter cap, 30 * 1.2 = 36s.
        var options = CreateOptions(initialDelaySeconds: 30, maxDelaySeconds: 30, backoffMultiplier: 2.0, jitterFactor: 0.2);
        var coordinator = CreateCoordinator(time, options, new FixedJitterProvider(1.0), logger);
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) >= 3)
            {
                cts.Cancel();
            }

            return Task.FromException(new RemoteTransportUnavailableException("down"));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1);
        time.Advance(TimeSpan.FromSeconds(30));
        await TestHelpers.WaitUntilAsync(() => attempts >= 2 || cts.IsCancellationRequested);
        time.Advance(TimeSpan.FromSeconds(30));
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;

        LoggedRetryDelays(logger).Should().NotBeEmpty()
            .And.OnlyContain(d => d <= 30.0);
        LoggedRetryDelays(logger).Should().Contain(d => d == 30.0);
    }

    /// <summary>
    /// Scenario: jitter provider returns 0.
    /// Expected: delay is reduced by the full jitter range.
    /// </summary>
    [Fact]
    public async Task Jitter_AtZeroBoundary_SubtractsFullRange()
    {
        var time = new FakeTimeProvider();
        var options = CreateOptions(initialDelaySeconds: 10, maxDelaySeconds: 60, jitterFactor: 0.2);
        // NextDouble=0 => jitter = -jitterRange = -2s => delay = 8s
        var coordinator = CreateCoordinator(time, options, new SequenceJitterProvider(0.0));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            if (attempts >= 2)
            {
                cts.Cancel();
            }

            return Task.FromException(new InvalidOperationException("fail"));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1, TimeSpan.FromSeconds(2));
        // Allow Task.Delay(..., timeProvider) to register before probing boundaries.
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(7.9));
        await Task.Delay(30);
        attempts.Should().Be(1, because: "7.9s is short of the 8s jittered delay");

        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 2 || cts.IsCancellationRequested,
            step: TimeSpan.FromMilliseconds(100),
            timeout: TimeSpan.FromSeconds(5));
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;
        attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Scenario: jitter provider returns 1.
    /// Expected: delay is increased by the full jitter range.
    /// </summary>
    [Fact]
    public async Task Jitter_AtOneBoundary_AddsFullRange()
    {
        var time = new FakeTimeProvider();
        var options = CreateOptions(initialDelaySeconds: 10, maxDelaySeconds: 60, jitterFactor: 0.2);
        // NextDouble=1 => jitter = +2s => delay = 12s
        var coordinator = CreateCoordinator(time, options, new SequenceJitterProvider(1.0));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            if (attempts >= 2)
            {
                cts.Cancel();
            }

            return Task.FromException(new InvalidOperationException("fail"));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(11.9));
        await Task.Delay(30);
        attempts.Should().Be(1, because: "11.9s is short of the 12s jittered delay");

        await TestHelpers.AdvanceUntilAsync(
            time,
            () => attempts >= 2 || cts.IsCancellationRequested,
            step: TimeSpan.FromMilliseconds(100),
            timeout: TimeSpan.FromSeconds(5));
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;
        attempts.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Scenario: cancel while waiting between attempts.
    /// Expected: loop stops without further connects.
    /// </summary>
    [Fact]
    public async Task CancelDuringDelay_StopsLoop()
    {
        var time = new FakeTimeProvider();
        var coordinator = CreateCoordinator(time, CreateOptions(initialDelaySeconds: 30, jitterFactor: 0));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new InvalidOperationException("fail"));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts == 1, TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        cts.Cancel();
        await TestHelpers.AdvanceUntilAsync(
            time,
            () => run.IsCompleted,
            step: TimeSpan.FromSeconds(1),
            timeout: TimeSpan.FromSeconds(5));
        await run;

        attempts.Should().Be(1);
        coordinator.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: cancel while connect is in flight.
    /// Expected: loop stops cleanly.
    /// </summary>
    [Fact]
    public async Task CancelDuringConnect_StopsLoop()
    {
        var time = new FakeTimeProvider();
        var coordinator = CreateCoordinator(time, CreateOptions());
        using var cts = new CancellationTokenSource();
        var connectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = coordinator.RunAsync(async ct =>
        {
            connectStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cts.Token);

        await connectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await run;
        coordinator.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: second RunAsync while one is active.
    /// Expected: throws rather than nesting loops.
    /// </summary>
    [Fact]
    public async Task ConcurrentRunAsync_Throws()
    {
        var time = new FakeTimeProvider();
        var coordinator = CreateCoordinator(time, CreateOptions());
        using var cts = new CancellationTokenSource();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunAsync(async ct =>
        {
            hold.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cts.Token);

        await hold.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.IsRunning.Should().BeTrue();

        var act = async () => await coordinator.RunAsync(_ => Task.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already running*");

        cts.Cancel();
        await first;
    }

    /// <summary>
    /// Scenario: permanent configuration exception from connect.
    /// Expected: loop stops without retry storm.
    /// </summary>
    [Fact]
    public async Task PermanentConfigurationException_StopsLoop_WithoutStorm()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var coordinator = CreateCoordinator(time, CreateOptions(initialDelaySeconds: 1, jitterFactor: 0), logger: logger);
        var attempts = 0;

        await coordinator.RunAsync(_ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new RemoteTransportConfigurationException("bad config"));
        }, CancellationToken.None);

        attempts.Should().Be(1);
        coordinator.IsRunning.Should().BeFalse();

        // Advancing time must not spawn more attempts.
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50);
        attempts.Should().Be(1);

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("configuration error", StringComparison.OrdinalIgnoreCase));
        logger.Entries.Where(e => e.Message.Contains("configuration error", StringComparison.OrdinalIgnoreCase))
            .Should().OnlyContain(e => e.Exception == null,
                because: "expected configuration errors must not include a stack-trace exception object");
    }

    /// <summary>
    /// Scenario: unexpected connect exception (not a transport/socket failure).
    /// Expected: logger records the exception object at Error.
    /// </summary>
    [Fact]
    public async Task UnexpectedException_LogsExceptionObjectAtError()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 1, jitterFactor: 0);
        options.Host = "127.0.0.1";
        options.Port = 9443;
        var coordinator = CreateCoordinator(time, options, logger: logger);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var run = coordinator.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                cts.Cancel();
            }

            return Task.FromException(new InvalidOperationException("transient-fail"));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await run;

        logger.Entries.Should().Contain(e =>
            e.Level == Microsoft.Extensions.Logging.LogLevel.Error &&
            e.Exception != null &&
            e.Exception.GetType() == typeof(InvalidOperationException) &&
            e.Message.Contains("Unexpected remote connect failure", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: connection refused wrapped as RemoteTransportException.
    /// Expected: single concise Warning without exception object; attempt and delay present.
    /// </summary>
    [Fact]
    public async Task ConnectionRefused_LogsConciseWarning_WithoutException()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 2, jitterFactor: 0);
        options.Host = "127.0.0.1";
        options.Port = 9443;
        var coordinator = CreateCoordinator(time, options, logger: logger);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var run = coordinator.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                cts.Cancel();
            }

            return Task.FromException(new RemoteTransportException(
                "Unable to connect to 127.0.0.1:9443.",
                new SocketException((int)SocketError.ConnectionRefused)));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1);
        time.Advance(TimeSpan.FromSeconds(2));
        await run;

        var warnings = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Where(e => e.Message.Contains("Connection refused", StringComparison.Ordinal))
            .ToList();

        warnings.Should().ContainSingle();
        warnings[0].Exception.Should().BeNull();
        warnings[0].Message.Should().Contain("127.0.0.1:9443");
        warnings[0].Message.Should().Contain("Attempt 1");
        warnings[0].Message.Should().MatchRegex(@"retrying in 2\.0s");
        logger.Entries.Count(e => e.Exception != null).Should().Be(0);
    }

    /// <summary>
    /// Scenario: connect timeout as RemoteTransportException without SocketException.
    /// Expected: concise Warning with Connection timed out; no exception object.
    /// </summary>
    [Fact]
    public async Task ConnectionTimeout_LogsConciseWarning_WithoutException()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 1, jitterFactor: 0);
        options.Host = "10.0.0.1";
        options.Port = 9443;
        var coordinator = CreateCoordinator(time, options, logger: logger);
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        var run = coordinator.RunAsync(_ =>
        {
            if (Interlocked.Increment(ref attempts) >= 2)
            {
                cts.Cancel();
            }

            return Task.FromException(new RemoteTransportException("TCP/mTLS connect timed out after 30s."));
        }, cts.Token);

        await TestHelpers.WaitUntilAsync(() => attempts >= 1);
        time.Advance(TimeSpan.FromSeconds(1));
        await run;

        var warnings = logger.Entries
            .Where(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .Where(e => e.Message.Contains("Connection timed out", StringComparison.Ordinal))
            .ToList();

        warnings.Should().ContainSingle();
        warnings[0].Exception.Should().BeNull();
        warnings[0].Message.Should().Contain("10.0.0.1:9443");
        warnings[0].Message.Should().Contain("Attempt 1");
    }

    /// <summary>
    /// Scenario: cancel during backoff after an expected failure.
    /// Expected: cancellation log only; no additional failure warning for cancel.
    /// </summary>
    [Fact]
    public async Task Cancellation_DoesNotLogConnectionFailureWarning()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger<ReconnectCoordinator>();
        var options = CreateOptions(initialDelaySeconds: 30, jitterFactor: 0);
        options.Host = "127.0.0.1";
        options.Port = 9443;
        var coordinator = CreateCoordinator(time, options, logger: logger);

        using var cts = new CancellationTokenSource();
        var run = coordinator.RunAsync(
            _ => Task.FromException(new RemoteTransportUnavailableException("down")),
            cts.Token);

        await TestHelpers.WaitUntilAsync(() =>
            logger.Entries.Any(e => e.Message.Contains("failed", StringComparison.OrdinalIgnoreCase)));

        cts.Cancel();
        time.Advance(TimeSpan.FromSeconds(1));
        await run;

        logger.Entries.Should().Contain(e =>
            e.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
        logger.Entries.Count(e =>
                e.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
                e.Message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, because: "only the connect failure is warned; cancel is informational");
    }

    /// <summary>
    /// Scenario: reason formatting for common socket errors.
    /// Expected: stable human-readable phrases without exception type names.
    /// </summary>
    [Theory]
    [InlineData(SocketError.ConnectionRefused, "Connection refused")]
    [InlineData(SocketError.TimedOut, "Connection timed out")]
    [InlineData(SocketError.HostUnreachable, "Host unreachable")]
    [InlineData(SocketError.NetworkUnreachable, "Network unreachable")]
    public void FormatConnectFailureReason_MapsSocketErrors(SocketError code, string expected)
    {
        var ex = new RemoteTransportException(
            "Unable to connect.",
            new SocketException((int)code));
        ReconnectCoordinator.FormatConnectFailureReason(ex).Should().Be(expected);
        ReconnectCoordinator.IsExpectedConnectFailure(ex).Should().BeTrue();
    }

    /// <summary>
    /// Scenario: many fail/succeed cycles under fake time.
    /// Expected: coordinator ends cleanly when cancelled.
    /// </summary>
    [Fact]
    public async Task Stress_ManyFailSucceedCycles_EndsCleanly()
    {
        var time = new FakeTimeProvider();
        var coordinator = CreateCoordinator(time, CreateOptions(initialDelaySeconds: 1, maxDelaySeconds: 4, jitterFactor: 0));
        var attempts = 0;
        using var cts = new CancellationTokenSource();

        var run = coordinator.RunAsync(async ct =>
        {
            var n = Interlocked.Increment(ref attempts);
            if (n >= 40)
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }

            if (n % 3 == 0)
            {
                // Simulate brief connected session then drop.
                await Task.CompletedTask;
                return;
            }

            throw new InvalidOperationException($"transient-{n}");
        }, cts.Token);

        // Pump fake time until cancelled.
        for (var i = 0; i < 80 && !run.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(4));
            await Task.Delay(5);
        }

        await run;
        coordinator.IsRunning.Should().BeFalse();
        attempts.Should().BeGreaterThanOrEqualTo(40);
        // No unobserved exceptions — awaiting run is the assertion.
    }
}
