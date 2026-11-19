// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    TestHelpers.cs
//
// Purpose:
// Shared test helpers: serial chunk factories, accounting invariants, wait/poll
// utilities, collecting loggers, jitter stubs, and IOptions stubs.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using FluentAssertions;
using TechConnect.Client.Models;

namespace TechConnect.Client.Tests.Support;

/// <summary>
/// Common factories and wait helpers shared across TechConnect.Client unit and integration tests.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Builds a <see cref="SerialDataChunk"/> from UTF-8 text with the given direction and timestamp.
    /// </summary>
    public static SerialDataChunk Chunk(string text, SerialDataDirection direction = SerialDataDirection.Rx, DateTimeOffset? timestamp = null) =>
        new()
        {
            TimestampUtc = timestamp ?? DateTimeOffset.UtcNow,
            Direction = direction,
            Data = System.Text.Encoding.UTF8.GetBytes(text)
        };

    /// <summary>
    /// Builds a <see cref="SerialDataChunk"/> from raw bytes with the given direction and timestamp.
    /// </summary>
    public static SerialDataChunk Chunk(byte[] data, SerialDataDirection direction = SerialDataDirection.Rx, DateTimeOffset? timestamp = null) =>
        new()
        {
            TimestampUtc = timestamp ?? DateTimeOffset.UtcNow,
            Direction = direction,
            Data = data
        };

    /// <summary>
    /// Asserts the remote delivery accounting invariant:
    /// BytesAccepted = BytesSent + BytesDropped + BytesQueued + BytesInFlight.
    /// </summary>
    public static void AssertAccountingInvariant(RemoteDeliveryStats stats)
    {
        (stats.BytesSent + stats.BytesDropped + stats.BytesQueued + stats.BytesInFlight)
            .Should().Be(stats.BytesAccepted,
                because: "BytesAccepted = BytesSent + BytesDropped + BytesQueued + BytesInFlight");
    }

    /// <summary>
    /// Polls a synchronous condition until true or the timeout elapses (default 5s).
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when the condition is still false after the limit.</exception>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, string? because = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > limit)
            {
                throw new TimeoutException(because ?? "Condition was not met within the allotted time.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls an asynchronous condition until true or the timeout elapses (default 5s).
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when the condition is still false after the limit.</exception>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null, string? because = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow - start > limit)
            {
                throw new TimeoutException(because ?? "Condition was not met within the allotted time.");
            }

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Advances <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/> in steps until
    /// <paramref name="condition"/> is true. Avoids races where a single Advance runs before
    /// <c>Task.Delay(..., timeProvider)</c> has registered its timer.
    /// </summary>
    public static async Task AdvanceUntilAsync(
        Microsoft.Extensions.Time.Testing.FakeTimeProvider time,
        Func<bool> condition,
        TimeSpan step,
        TimeSpan? timeout = null,
        string? because = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > limit)
            {
                throw new TimeoutException(because ?? "Condition was not met while advancing fake time.");
            }

            time.Advance(step);
            await Task.Delay(10).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// <see cref="Microsoft.Extensions.Logging.ILogger{T}"/> that records every log entry in memory for assertions.
/// </summary>
/// <remarks>
/// Limitation: scopes are no-ops; all log levels are enabled; not thread-order deterministic beyond ConcurrentQueue enqueue order.
/// </remarks>
internal sealed class CollectingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    /// <summary>
    /// Recorded log entries.
    /// </summary>
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>
    /// Snapshot of all recorded log entries in enqueue order.
    /// </summary>
    public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel,
        Microsoft.Extensions.Logging.EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    /// <summary>
    /// One captured log line with level, formatted message, and optional exception.
    /// </summary>
    public sealed record LogEntry(
        Microsoft.Extensions.Logging.LogLevel Level,
        string Message,
        Exception? Exception);

    /// <summary>
    /// No-op logging scope singleton.
    /// </summary>
    private sealed class NullScope : IDisposable
    {
        /// <summary>
        /// Shared no-op scope instance.
        /// </summary>
        public static readonly NullScope Instance = new();

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// <see cref="TechConnect.Client.Abstractions.IJitterProvider"/> that always returns a fixed double.
/// </summary>
internal sealed class FixedJitterProvider : TechConnect.Client.Abstractions.IJitterProvider
{
    /// <summary>
    /// Fixed jitter value.
    /// </summary>
    private readonly double _value;

    /// <summary>
    /// Creates a provider that always returns <paramref name="value"/>.
    /// </summary>
    public FixedJitterProvider(double value) => _value = value;

    /// <inheritdoc />
    public double NextDouble() => _value;
}

/// <summary>
/// <see cref="TechConnect.Client.Abstractions.IJitterProvider"/> that returns values from a queue, then 0.5 when empty.
/// </summary>
/// <remarks>
/// Limitation: after the sequence is exhausted, every subsequent call returns 0.5.
/// </remarks>
internal sealed class SequenceJitterProvider : TechConnect.Client.Abstractions.IJitterProvider
{
    /// <summary>
    /// Queued jitter sequence.
    /// </summary>
    private readonly Queue<double> _values;

    /// <summary>
    /// Creates a provider that dequeues the given sequence.
    /// </summary>
    public SequenceJitterProvider(params double[] values) =>
        _values = new Queue<double>(values);

    /// <inheritdoc />
    public double NextDouble() => _values.Count > 0 ? _values.Dequeue() : 0.5;
}

/// <summary>
/// Minimal <see cref="Microsoft.Extensions.Options.IOptions{T}"/> stub returning a fixed value.
/// </summary>
/// <remarks>
/// Limitation: does not implement <c>IOptionsMonitor</c> / change notifications.
/// </remarks>
internal sealed class OptionsMonitorStub<T> : Microsoft.Extensions.Options.IOptions<T>
    where T : class
{
    /// <summary>
    /// Creates a stub that always returns <paramref name="value"/>.
    /// </summary>
    public OptionsMonitorStub(T value) => Value = value;

    /// <inheritdoc />
    public T Value { get; }
}
