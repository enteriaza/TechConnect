// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteConfigurationTests.cs
//
// Purpose:
// Verifies remote configuration application and logging harness behaviour.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Session;

/// <summary>
/// Tests remote session configuration error paths and reconnect logger behaviour.
/// </summary>
public sealed class RemoteConfigurationTests
{
    /// <summary>
    /// Scenario: RemoteService Host empty so session cannot connect.
    /// Expected: Error state without transport connect attempts; logs avoid stack-trace dumps for config errors.
    /// </summary>
    [Fact]
    public async Task EmptyHost_SetsError_WithoutConnectAttempts_OrStackTrace()
    {
        await using var h = new RemoteSessionServiceTestsHarness(new RemoteServiceOptions
        {
            Host = string.Empty,
            Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 4096 }
        });

        await h.Serial.OpenAsync("COM4", null, CancellationToken.None);
        await h.Session.StartAsync("COM4", CancellationToken.None);

        h.Session.State.Should().Be(RemoteSessionState.Error);
        h.Serial.IsOpen.Should().BeTrue();
        h.Transport.ConnectCallCount.Should().Be(0, because: "empty Host must fail before connect attempts");

        var snap = h.Session.GetSnapshot();
        snap.RemoteStatus.Should().Be(RemoteSessionState.Error);
        snap.NocStatus.Should().Be(NocAttachmentState.Waiting);
        snap.LastRemoteError.Should().Contain("not configured");
        snap.LastRemoteError.Should().Contain("Local console remains available");

        var configLogs = h.Logger.Entries
            .Where(e => e.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        configLogs.Should().NotBeEmpty();
        configLogs.Should().OnlyContain(e => e.Exception == null, because: "expected config condition must not log a stack trace");
        h.Logger.Entries.Any(e => e.Exception is RemoteTransportConfigurationException)
            .Should().BeFalse();

        // No reconnect storm: still a single Error, still no connects.
        await Task.Delay(50);
        h.Transport.ConnectCallCount.Should().Be(0);
        h.Session.State.Should().Be(RemoteSessionState.Error);

        // Start can be retried after Stop (or directly since no session CTS was retained).
        await h.Session.StopAsync(CancellationToken.None);
        h.Session.State.Should().Be(RemoteSessionState.Disconnected);
        h.Serial.IsOpen.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: transport throws an unexpected exception during connect.
    /// Expected: logger still receives the exception object for diagnostics.
    /// </summary>
    [Fact]
    public async Task UnexpectedTransportException_StillLogsExceptionObject()
    {
        await using var h = new RemoteSessionServiceTestsHarness();
        await h.Serial.OpenAsync("COM4", null, CancellationToken.None);
        h.Transport.SetConnectException(new InvalidOperationException("boom-unexpected"));

        await h.Session.StartAsync("COM4", CancellationToken.None);
        await TestHelpers.WaitUntilAsync(() => h.Transport.ConnectCallCount >= 1);

        h.Time.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(30);

        h.ReconnectLogger.Entries.Should().Contain(e =>
            e.Exception != null &&
            e.Message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Local harness exposing reconnect logger for configuration/logging tests.</summary>
/// <summary>
/// Local harness exposing serial/transport/time/loggers for configuration and logging tests.
/// Limitation: uses fakes only; not a real WSS endpoint.
/// </summary>
internal sealed class RemoteSessionServiceTestsHarness : IAsyncDisposable
{
    /// <summary>
    /// Fake serial connection used by the harness.
    /// </summary>
    public FakeSerialConnectionService Serial { get; } = new();
    /// <summary>
    /// Fake remote transport used by the harness.
    /// </summary>
    public FakeRemoteTransport Transport { get; } = new();
    /// <summary>
    /// Fake time provider for reconnect scheduling.
    /// </summary>
    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; } = new();
    /// <summary>
    /// RemoteSessionService under test.
    /// </summary>
    public TechConnect.Client.Services.RemoteSessionService Session { get; }
    /// <summary>
    /// Collecting logger for RemoteSessionService.
    /// </summary>
    public CollectingLogger<TechConnect.Client.Services.RemoteSessionService> Logger { get; } = new();
    /// <summary>
    /// Collecting logger for ReconnectCoordinator.
    /// </summary>
    public CollectingLogger<TechConnect.Client.Services.ReconnectCoordinator> ReconnectLogger { get; } = new();

    /// <summary>
    /// Creates the harness with optional RemoteServiceOptions overrides.
    /// </summary>
    public RemoteSessionServiceTestsHarness(RemoteServiceOptions? options = null)
    {
        options ??= new RemoteServiceOptions
        {
            Host = "remote.contoso.test",
            Port = 9443,
            TlsHostName = "remote.contoso.test",
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
        var reconnect = new TechConnect.Client.Services.ReconnectCoordinator(
            opts,
            Time,
            new FixedJitterProvider(0.5),
            ReconnectLogger);
        Session = new TechConnect.Client.Services.RemoteSessionService(
            Serial, Transport, reconnect, opts, Time, Logger);
    }

    /// <summary>
    /// Disposes the session, serial service, and transport.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        await Serial.DisposeAsync();
        await Transport.DisposeAsync();
    }
}
