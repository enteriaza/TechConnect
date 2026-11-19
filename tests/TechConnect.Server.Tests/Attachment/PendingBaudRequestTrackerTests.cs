// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PendingBaudRequestTrackerTests.cs
//
// Purpose:
// Verifies SetBaud correlation ownership, single-outstanding-per-operator policy,
// timeout completion, and connector-session failure fan-out.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;

namespace TechConnect.Server.Tests.Attachment;

/// <summary>
/// Unit tests for <see cref="PendingBaudRequestTracker"/> correlation and timeout behaviour.
/// </summary>
public sealed class PendingBaudRequestTrackerTests
{
    /// <summary>
    /// Verifies register/complete delivers success to the owning operator and clears the tracker.
    /// </summary>
    [Fact]
    public async Task RegisterAndComplete_DeliversToCorrectOperator()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister("op-1", "sess-1", 9600, out var pending, out _).Should().BeTrue();
        pending.Should().NotBeNull();

        tracker.TryComplete(
                pending!.CorrelationId,
                "op-1",
                new PendingBaudResult { Success = true, EffectiveBaudRate = 9600 })
            .Should().BeTrue();

        var result = await pending.Completion.Task;
        result.Success.Should().BeTrue();
        result.EffectiveBaudRate.Should().Be(9600);
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies a different operator cannot complete another operator's correlation ID.
    /// </summary>
    [Fact]
    public void WrongOperator_CannotComplete()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister("op-1", "sess-1", 9600, out var pending, out _).Should().BeTrue();

        tracker.TryComplete(
                pending!.CorrelationId,
                "op-other",
                new PendingBaudResult { Success = true, EffectiveBaudRate = 9600 })
            .Should().BeFalse();

        tracker.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies a second outstanding baud request for the same operator is rejected.
    /// </summary>
    [Fact]
    public void SecondRequest_SameOperator_Rejected()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister("op-1", "sess-1", 9600, out _, out _).Should().BeTrue();
        tracker.TryRegister("op-1", "sess-1", 115200, out _, out var error).Should().BeFalse();
        error.Should().Contain("already in progress");
    }

    /// <summary>
    /// Verifies timeout completes with failure and clears the outstanding entry.
    /// </summary>
    [Fact]
    public async Task Timeout_FailsRequest()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister(
                "op-1",
                "sess-1",
                9600,
                out var pending,
                out _,
                timeout: TimeSpan.FromMilliseconds(50))
            .Should().BeTrue();

        var result = await pending!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("timed out");
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies a late complete after timeout is ignored.
    /// </summary>
    [Fact]
    public async Task LateComplete_AfterTimeout_IsIgnored()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister(
                "op-1",
                "sess-1",
                9600,
                out var pending,
                out _,
                timeout: TimeSpan.FromMilliseconds(30))
            .Should().BeTrue();

        await pending!.Completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        tracker.TryComplete(
                pending.CorrelationId,
                "op-1",
                new PendingBaudResult { Success = true, EffectiveBaudRate = 9600 })
            .Should().BeFalse();
    }

    /// <summary>
    /// Verifies failing by connector session unblocks the outstanding operator request.
    /// </summary>
    [Fact]
    public async Task FailConnectorSession_FailsOutstanding()
    {
        var tracker = new PendingBaudRequestTracker();
        tracker.TryRegister("op-1", "sess-1", 9600, out var pending, out _).Should().BeTrue();
        tracker.FailConnectorSession("sess-1", "Connector disconnected.");
        var result = await pending!.Completion.Task;
        result.Success.Should().BeFalse();
        result.Error.Should().Contain("disconnected");
    }
}
