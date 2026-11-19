// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PendingSessionPingTrackerTests.cs
//
// Purpose:
// Verifies session ping correlation, timeout completion, cancel, and connector failure fan-out.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Unit tests for PendingSessionPingTracker outstanding-probe tracking.
/// </summary>
public sealed class PendingSessionPingTrackerTests
{
    /// <summary>
    /// Complete Succeeds.
    /// </summary>
    [Fact]
    public async Task Complete_Succeeds()
    {
        var tracker = new PendingSessionPingTracker();
        tracker.TryRegister("sess1", TimeSpan.FromSeconds(2), out var pending, out var error)
            .Should().BeTrue(error);
        pending.Should().NotBeNull();

        tracker.TryComplete(pending!.CorrelationId, new PendingSessionPingResult
        {
            Success = true,
            Hostname = "host"
        }).Should().BeTrue();

        var result = await pending.Completion.Task;
        result.Success.Should().BeTrue();
        result.Hostname.Should().Be("host");
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// Timeout Completes As Timed Out.
    /// </summary>
    [Fact]
    public async Task Timeout_CompletesAsTimedOut()
    {
        var tracker = new PendingSessionPingTracker();
        tracker.TryRegister("sess1", TimeSpan.FromMilliseconds(50), out var pending, out _)
            .Should().BeTrue();
        var result = await pending!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.TimedOut.Should().BeTrue();
        result.Success.Should().BeFalse();
    }

    /// <summary>
    /// Fail Connector Session Completes Outstanding.
    /// </summary>
    [Fact]
    public async Task FailConnectorSession_CompletesOutstanding()
    {
        var tracker = new PendingSessionPingTracker();
        tracker.TryRegister("sess1", TimeSpan.FromSeconds(30), out var a, out _).Should().BeTrue();
        tracker.TryRegister("sess1", TimeSpan.FromSeconds(30), out var b, out _).Should().BeTrue();
        tracker.FailConnectorSession("sess1", "gone");
        (await a!.Completion.Task).Error.Should().Be("gone");
        (await b!.Completion.Task).Error.Should().Be("gone");
        tracker.Count.Should().Be(0);
    }
}
