// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    InMemoryAuditEventStoreRuntimeUpdateTests.cs
//
// Purpose:
// Verifies InMemoryAuditEventStore.ApplyOptions (enable/disable, retention
// shrink, ordering, concurrency) and configuration audit events.
//
// -----------------------------------------------------------------------------

using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Audit;
using TechConnect.Server.Configuration;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Tests.Configuration.Management;
using TechConnect.Server.Tests.Support;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Tests.Audit;

/// <summary>
/// Runtime audit settings tests.
/// </summary>
public sealed class InMemoryAuditEventStoreRuntimeUpdateTests
{
    /// <summary>Creates a store.</summary>
    private static InMemoryAuditEventStore Create(int max = 100) =>
        new(true, max, TimeProvider.System, NullLogger<InMemoryAuditEventStore>.Instance);

    /// <summary>Writes numbered events.</summary>
    private static void WriteNumbered(InMemoryAuditEventStore store, int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "e" + i);
        }
    }

    /// <summary>Disabling stops further appends.</summary>
    [Fact]
    public void Disable_PreventsSubsequentAppends()
    {
        var store = Create();
        WriteNumbered(store, 1, 3);

        store.ApplyOptions(new AuditOptions { Enabled = false, MaximumEvents = 100 });
        WriteNumbered(store, 4, 6);

        store.IsEnabled.Should().BeFalse();
        store.Count.Should().Be(3);
        store.ApplyOptions(new AuditOptions { Enabled = true, MaximumEvents = 100 });
        store.GetRecent(10).Select(e => e.Description).Should().Equal("e3", "e2", "e1");
    }

    /// <summary>
    /// A write that passed the unlocked enabled check is paused (test hook) before taking the lock;
    /// the store is then disabled; when the write resumes it must not append (the under-lock re-check).
    /// Earlier events keep their order and contents, the dropped write consumes no sequence number,
    /// and re-enabling records new events.
    /// </summary>
    [Fact]
    public async Task WritePausedBetweenCheckAndLock_IsDroppedWhenDisabledMeanwhile()
    {
        var pauseNext = 0;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryAuditEventStore(
            true,
            100,
            TimeProvider.System,
            NullLogger<InMemoryAuditEventStore>.Instance,
            beforeWriteLock: () =>
            {
                if (Interlocked.Exchange(ref pauseNext, 0) == 1)
                {
                    paused.SetResult();
                    resume.Task.Wait();
                }
            });
        WriteNumbered(store, 1, 3);

        Interlocked.Exchange(ref pauseNext, 1);
        var racing = Task.Run(() => store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "racing"));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        store.ApplyOptions(new AuditOptions { Enabled = false, MaximumEvents = 100 });
        resume.SetResult();
        await racing.WaitAsync(TimeSpan.FromSeconds(10));
        store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "after-disable");

        store.Count.Should().Be(3);
        store.ApplyOptions(new AuditOptions { Enabled = true, MaximumEvents = 100 });
        var retained = store.GetRecent(10);
        retained.Select(e => e.Description).Should().Equal("e3", "e2", "e1");
        retained.Select(e => e.Sequence).Should().Equal(3, 2, 1);

        store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "e4");
        var after = store.GetRecent(10);
        after.Select(e => e.Description).Should().Equal("e4", "e3", "e2", "e1");
        after[0].Sequence.Should().Be(4);
    }

    /// <summary>
    /// The mirror ordering: a write paused before the lock while the store is enabled completes
    /// normally when nothing disables it in between (the hook itself does not drop writes).
    /// </summary>
    [Fact]
    public async Task WritePausedBetweenCheckAndLock_IsRecordedWhenStillEnabled()
    {
        var pauseNext = 0;
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryAuditEventStore(
            true,
            100,
            TimeProvider.System,
            NullLogger<InMemoryAuditEventStore>.Instance,
            beforeWriteLock: () =>
            {
                if (Interlocked.Exchange(ref pauseNext, 0) == 1)
                {
                    paused.SetResult();
                    resume.Task.Wait();
                }
            });
        WriteNumbered(store, 1, 1);

        Interlocked.Exchange(ref pauseNext, 1);
        var racing = Task.Run(() => store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "racing"));
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        resume.SetResult();
        await racing.WaitAsync(TimeSpan.FromSeconds(10));

        store.GetRecent(10).Select(e => e.Description).Should().Equal("racing", "e1");
    }

    /// <summary>Shrinking the limit keeps the newest events in order and marks eviction.</summary>
    [Fact]
    public void ShrinkMaximumEvents_KeepsNewest_PreservesOrder()
    {
        var store = Create(max: 10);
        WriteNumbered(store, 1, 8);
        store.HasEvictedEvents.Should().BeFalse();

        store.ApplyOptions(new AuditOptions { Enabled = true, MaximumEvents = 3 });

        store.MaximumEvents.Should().Be(3);
        store.Count.Should().Be(3);
        store.HasEvictedEvents.Should().BeTrue();
        store.GetRecent(10).Select(e => e.Description).Should().Equal("e8", "e7", "e6");
        store.GetRecent(10).Select(e => e.Sequence).Should().BeInDescendingOrder();

        WriteNumbered(store, 9, 9);
        store.GetRecent(10).Select(e => e.Description).Should().Equal("e9", "e8", "e7");
    }

    /// <summary>Growing the limit keeps all events and allows more retention.</summary>
    [Fact]
    public void GrowMaximumEvents_RetainsMore()
    {
        var store = Create(max: 2);
        WriteNumbered(store, 1, 3);
        store.ApplyOptions(new AuditOptions { Enabled = true, MaximumEvents = 5 });
        WriteNumbered(store, 4, 6);

        store.GetRecent(10).Select(e => e.Description).Should().Equal("e6", "e5", "e4", "e3", "e2");
    }

    /// <summary>Invalid limits are rejected without changing state.</summary>
    [Fact]
    public void ApplyOptions_InvalidMaximum_Throws_StateUnchanged()
    {
        var store = Create(max: 5);
        WriteNumbered(store, 1, 2);

        var act = () => store.ApplyOptions(new AuditOptions { Enabled = false, MaximumEvents = 0 });

        act.Should().Throw<ArgumentOutOfRangeException>();
        store.IsEnabled.Should().BeTrue();
        store.MaximumEvents.Should().Be(5);
        store.Count.Should().Be(2);
    }

    /// <summary>Concurrent reads, writes, and updates never exceed the active limit or throw.</summary>
    [Fact]
    public async Task ConcurrentReadsWritesAndUpdates_AreSafe()
    {
        var store = Create(max: 50);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var writers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                store.TryWrite(AuditEventType.OperatorAttached, AuditEventOutcome.Success, "w");
            }
        }));
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var recent = store.GetRecent(100);
                recent.Select(e => e.Sequence).Should().BeInDescendingOrder();
                recent.Count.Should().BeLessThanOrEqualTo(50);
            }
        }));
        var updater = Task.Run(() =>
        {
            var flip = false;
            while (!stop.IsCancellationRequested)
            {
                store.ApplyOptions(new AuditOptions { Enabled = true, MaximumEvents = flip ? 10 : 50 });
                flip = !flip;
            }
        });

        await Task.WhenAll(writers.Concat(readers).Append(updater));

        store.Count.Should().BeLessThanOrEqualTo(store.MaximumEvents);
    }

    /// <summary>Configuration save/discard/enter events carry operator and session context and no secret values.</summary>
    [Fact]
    public async Task ConfigurationEvents_HaveOperatorContext_AndNoSecrets()
    {
        using var fixture = new ConfigurationFixture();
        var store = Create(max: 1000);
        var service = fixture.CreateService(audit: store);
        var admin = TestAuthenticatedOperator.Create("cfgadmin", PrivilegeLevel.FromInt(15), operatorSessionId: "sess-42");
        var edit = service.BeginEdit(admin, out _);
        ConfigPaths.Set(edit, "authentication radius primary-server", "shared-secret", JsonValue.Create("Audit-Leak-Canary"));
        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);
        var second = service.BeginEdit(admin, out _);
        ConfigPaths.Set(second, "authentication radius primary-server", "shared-secret", JsonValue.Create("Discard-Leak-Canary"));
        service.Discard(second, "end");

        var events = store.GetRecent(100);
        events.Select(e => e.EventType).Should().Contain(
            [AuditEventType.ConfigurationModeEntered, AuditEventType.ConfigurationSaved, AuditEventType.ConfigurationDiscarded]);
        foreach (var e in events.Where(e => e.EventType is AuditEventType.ConfigurationModeEntered or AuditEventType.ConfigurationSaved or AuditEventType.ConfigurationDiscarded))
        {
            e.ActorUsername.Should().Be("cfgadmin");
            e.SessionId.Should().Be("sess-42");
        }

        var all = string.Join("\n", events.Select(e => e.Description + " " + string.Join(",", e.Details?.Select(d => d.Key + "=" + d.Value) ?? [])));
        all.Should().NotContain("Audit-Leak-Canary").And.NotContain("Discard-Leak-Canary");
    }
}
