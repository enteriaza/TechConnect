// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteConnectFailureLoggingTests.cs
//
// Purpose:
// Verifies expected remote connect failures are logged once by ReconnectCoordinator
// without duplicate session-layer stack traces.
//
// -----------------------------------------------------------------------------

using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// End-to-end logging ownership tests for expected remote connect failures.
/// </summary>
public sealed class RemoteConnectFailureLoggingTests
{
    /// <summary>
    /// Harness exposing both session and reconnect loggers.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        public FakeSerialConnectionService Serial { get; } = new();
        public FakeRemoteTransport Transport { get; } = new();
        public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        public RemoteSessionService Session { get; }
        public CollectingLogger<RemoteSessionService> SessionLogger { get; } = new();
        public CollectingLogger<ReconnectCoordinator> ReconnectLogger { get; } = new();

        public Harness()
        {
            var options = new RemoteServiceOptions
            {
                Host = "127.0.0.1",
                Port = 9443,
                TlsHostName = "localhost",
                Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 4096 },
                Reconnect = new ReconnectOptions
                {
                    InitialDelaySeconds = 1,
                    MaxDelaySeconds = 4,
                    BackoffMultiplier = 2,
                    JitterFactor = 0
                }
            };

            var opts = new OptionsMonitorStub<RemoteServiceOptions>(options);
            var reconnect = new ReconnectCoordinator(
                opts,
                Time,
                new FixedJitterProvider(0.5),
                ReconnectLogger);

            Session = new RemoteSessionService(
                Serial,
                Transport,
                reconnect,
                opts,
                Time,
                SessionLogger);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Serial.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    /// <summary>
    /// Scenario: transport connect refused repeatedly.
    /// Expected: one concise reconnect Warning per attempt; session logger has no stack-trace connect failure.
    /// </summary>
    [Fact]
    public async Task ExpectedConnectFailure_IsLoggedOnce_WithoutSessionDuplicate()
    {
        await using var h = new Harness();
        await h.Serial.OpenAsync("COM3", null, CancellationToken.None);

        h.Transport.SetConnectException(new RemoteTransportException(
            "Unable to connect to 127.0.0.1:9443.",
            new SocketException((int)SocketError.ConnectionRefused)));

        await h.Session.StartAsync("COM3", CancellationToken.None);

        await TestHelpers.WaitUntilAsync(
            () => h.ReconnectLogger.Entries.Count(e =>
                    e.Level == LogLevel.Warning &&
                    e.Message.Contains("Connection refused", StringComparison.Ordinal)) >= 1,
            because: "reconnect coordinator should log the refused connection");

        h.Time.Advance(TimeSpan.FromSeconds(1));

        await TestHelpers.WaitUntilAsync(
            () => h.ReconnectLogger.Entries.Count(e =>
                    e.Level == LogLevel.Warning &&
                    e.Message.Contains("Connection refused", StringComparison.Ordinal)) >= 2,
            because: "second attempt should also log once");

        var refused = h.ReconnectLogger.Entries
            .Where(e => e.Level == LogLevel.Warning)
            .Where(e => e.Message.Contains("Connection refused", StringComparison.Ordinal))
            .ToList();

        refused.Should().HaveCountGreaterThanOrEqualTo(2);
        refused.Should().OnlyContain(e => e.Exception == null);
        refused[0].Message.Should().Contain("127.0.0.1:9443");
        refused[0].Message.Should().Contain("Attempt 1");
        refused[1].Message.Should().Contain("Attempt 2");

        h.SessionLogger.Entries.Should().NotContain(e =>
            e.Exception != null &&
            e.Message.Contains("connect failed", StringComparison.OrdinalIgnoreCase));

        h.SessionLogger.Entries.Should().NotContain(e =>
            e.Message.Contains("Remote transport connect failed", StringComparison.Ordinal));

        h.Serial.IsOpen.Should().BeTrue();
        await h.Session.StopAsync(CancellationToken.None);
    }
}
