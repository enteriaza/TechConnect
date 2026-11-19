// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    InMemoryAuditEventStoreTests.cs
//
// Purpose:
// Regression tests and helpers covering InMemoryAuditEventStore behaviour.
// Fixture: InMemoryAuditEventStoreTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Audit;

namespace TechConnect.Server.Tests.Audit;
/// <summary>
/// xUnit test fixture for InMemoryAuditEventStore.
/// </summary>

public sealed class InMemoryAuditEventStoreTests
{
    /// <summary>
    /// Verifies Create.
    /// </summary>
    private static InMemoryAuditEventStore Create(bool enabled = true, int max = 10) =>
        new(enabled, max, TimeProvider.System, NullLogger<InMemoryAuditEventStore>.Instance);
    /// <summary>
    /// Verifies Insert Assigns Monotonic Sequences And Newest First.
    /// </summary>

    [Fact]
    public void Insert_AssignsMonotonicSequences_AndNewestFirst()
    {
        var store = Create();
        store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "a");
        store.TryWrite(AuditEventType.OperatorDetached, AuditEventOutcome.Success, "b");
        store.TryWrite(AuditEventType.TerminalModeEntered, AuditEventOutcome.Informational, "c");

        var recent = store.GetRecent(10);
        recent.Should().HaveCount(3);
        recent[0].Description.Should().Be("c");
        recent[1].Description.Should().Be("b");
        recent[2].Description.Should().Be("a");
        recent.Select(e => e.Sequence).Should().BeInDescendingOrder();
        recent[0].Sequence.Should().Be(3);
        recent[2].Sequence.Should().Be(1);
    }
    /// <summary>
    /// Verifies Capacity Evicts Oldest Retains Newest.
    /// </summary>

    [Fact]
    public void Capacity_EvictsOldest_RetainsNewest()
    {
        var store = Create(max: 3);
        for (var i = 1; i <= 5; i++)
        {
            store.TryWrite(
                AuditEventType.OperatorAttached,
                AuditEventOutcome.Success,
                $"e{i}",
                new AuditEventContext { SessionId = $"s{i}" });
        }

        store.Count.Should().Be(3);
        store.HasEvictedEvents.Should().BeTrue();
        var recent = store.GetRecent(10);
        recent.Select(e => e.Description).Should().Equal("e5", "e4", "e3");
        recent.Select(e => e.Sequence).Should().Equal(5L, 4L, 3L);
    }
    /// <summary>
    /// Verifies Empty Store Returns Empty.
    /// </summary>

    [Fact]
    public void EmptyStore_ReturnsEmpty()
    {
        var store = Create();
        store.GetRecent(50).Should().BeEmpty();
        store.GetBySessionId("abc", 50).Should().BeEmpty();
        store.GetByUsername("admin", 50).Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Filter By Session Id Exact And Newest First.
    /// </summary>

    [Fact]
    public void Filter_BySessionId_ExactAndNewestFirst()
    {
        var store = Create(max: 20);
        store.TryWrite(
            AuditEventType.ConnectorRegistered,
            AuditEventOutcome.Success,
            "reg",
            new AuditEventContext { SessionId = "aaaa1111" });
        store.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "attach",
            new AuditEventContext { SessionId = "bbbb2222", ActorUsername = "op" });
        store.TryWrite(
            AuditEventType.OperatorDetached,
            AuditEventOutcome.Success,
            "detach",
            new AuditEventContext { SessionId = "aaaa1111", ActorUsername = "op" });

        var matches = store.GetBySessionId("AAAA1111", 50);
        matches.Should().HaveCount(2);
        matches[0].Description.Should().Be("detach");
        matches[1].Description.Should().Be("reg");
        store.GetBySessionId("zzzz9999", 50).Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Filter By Username Exact Ignore Case Not Substring.
    /// </summary>

    [Fact]
    public void Filter_ByUsername_ExactIgnoreCase_NotSubstring()
    {
        var store = Create(max: 20);
        store.TryWrite(
            AuditEventType.SshPasswordAuthSuccess,
            AuditEventOutcome.Success,
            "ok",
            new AuditEventContext { ActorUsername = "admin", RequestedUsername = "admin" });
        store.TryWrite(
            AuditEventType.SshPasswordAuthFailure,
            AuditEventOutcome.Failure,
            "fail",
            new AuditEventContext { RequestedUsername = "administrator" });
        store.TryWrite(
            AuditEventType.OperatorAttached,
            AuditEventOutcome.Success,
            "attach",
            new AuditEventContext { ActorUsername = "Admin" });

        var matches = store.GetByUsername("admin", 50);
        matches.Should().HaveCount(2);
        matches.Select(e => e.Description).Should().Equal("attach", "ok");
        store.GetByUsername("ad", 50).Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Disabled Ignores Writes And Returns Empty.
    /// </summary>

    [Fact]
    public void Disabled_IgnoresWrites_AndReturnsEmpty()
    {
        var store = Create(enabled: false, max: 10);
        store.IsEnabled.Should().BeFalse();
        store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "x");
        store.Count.Should().Be(0);
        store.GetRecent(10).Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Snapshots Are Immutable Copies.
    /// </summary>

    [Fact]
    public void Snapshots_AreImmutableCopies()
    {
        var store = Create();
        store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "one");
        var snap = store.GetRecent(10);
        store.TryWrite(AuditEventType.OperatorDetached, AuditEventOutcome.Success, "two");
        snap.Should().HaveCount(1);
        snap[0].Description.Should().Be("one");
    }
    /// <summary>
    /// Verifies Concurrent Writes Are Thread Safe And Sequences Unique.
    /// </summary>

    [Fact]
    public async Task ConcurrentWrites_AreThreadSafe_AndSequencesUnique()
    {
        var store = Create(max: 5_000);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            for (var j = 0; j < 50; j++)
            {
                store.TryWrite(
                    AuditEventType.OperatorAttached,
                    AuditEventOutcome.Success,
                    $"t{i}-{j}");
            }
        }));

        await Task.WhenAll(tasks);
        store.Count.Should().Be(1000);
        var all = store.GetRecent(5000);
        all.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
        all.Should().BeInDescendingOrder(e => e.Sequence);
    }
    /// <summary>
    /// Verifies Concurrent Reads And Writes Do Not Throw.
    /// </summary>

    [Fact]
    public async Task ConcurrentReadsAndWrites_DoNotThrow()
    {
        var store = Create(max: 200);
        var writers = Enumerable.Range(0, 8).Select(__ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                store.TryWrite(AuditEventType.AuthorizationDenied, AuditEventOutcome.Denied, "deny");
            }
        }));
        var readers = Enumerable.Range(0, 8).Select(__ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                _ = store.GetRecent(20);
                _ = store.Count;
            }
        }));

        await Task.WhenAll(writers.Concat(readers));
        store.Count.Should().BeLessThanOrEqualTo(200);
        store.HasEvictedEvents.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Sanitizes Control Characters From Description.
    /// </summary>

    [Fact]
    public void Sanitizes_ControlCharacters_FromDescription()
    {
        var store = Create();
        store.TryWrite(
            AuditEventType.AuthorizationDenied,
            AuditEventOutcome.Denied,
            "bad\u001b[31m injection");
        store.GetRecent(1)[0].Description.Should().Be("bad[31m injection");
    }
}
