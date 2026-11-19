// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusIdentifierAllocatorTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusIdentifierAllocator behaviour.
// Fixture: RadiusIdentifierAllocatorTests.
//
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Transport;

namespace TechConnect.Server.Tests.Authentication.Radius.Transport;
/// <summary>
/// xUnit test fixture for RadiusIdentifierAllocator.
/// </summary>

public sealed class RadiusIdentifierAllocatorTests
{
    /// <summary>
    /// Verifies Try Allocate All256 Then Fails Until Release.
    /// </summary>
    [Fact]
    public void TryAllocate_All256_ThenFailsUntilRelease()
    {
        var allocator = new RadiusIdentifierAllocator();
        var allocated = new HashSet<byte>();

        for (var i = 0; i < RadiusIdentifierAllocator.Capacity; i++)
        {
            allocator.TryAllocate(out var id).Should().BeTrue();
            allocated.Add(id).Should().BeTrue();
        }

        allocated.Should().HaveCount(256);
        allocator.TryAllocate(out _).Should().BeFalse();
        allocator.InUseCount.Should().Be(256);

        allocator.Release(7, TimeSpan.Zero);
        // Quarantine with zero duration — purge on next allocate should free it.
        allocator.TryAllocate(out var reused).Should().BeTrue();
        reused.Should().Be(7);
    }
    /// <summary>
    /// Verifies Release Is Idempotent.
    /// </summary>

    [Fact]
    public void Release_IsIdempotent()
    {
        var allocator = new RadiusIdentifierAllocator();
        allocator.TryAllocate(out var id).Should().BeTrue();
        allocator.Release(id, TimeSpan.Zero);
        allocator.Release(id, TimeSpan.Zero);
        allocator.InUseCount.Should().Be(0);
    }
    /// <summary>
    /// Verifies Cannot Allocate Same Identifier Twice Concurrently.
    /// </summary>

    [Fact]
    public void CannotAllocateSameIdentifierTwiceConcurrently()
    {
        var allocator = new RadiusIdentifierAllocator();
        var ids = new ConcurrentDictionary<byte, byte>();
        Parallel.For(0, 256, _ =>
        {
            if (allocator.TryAllocate(out var id))
            {
                ids.TryAdd(id, 0).Should().BeTrue();
            }
        });

        ids.Should().HaveCount(256);
        allocator.TryAllocate(out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Quarantine Prevents Immediate Reuse.
    /// </summary>

    [Fact]
    public void Quarantine_PreventsImmediateReuse()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-18T12:00:00Z"));
        var allocator = new RadiusIdentifierAllocator(time);
        allocator.TryAllocate(out var id).Should().BeTrue();
        allocator.Release(id, TimeSpan.FromSeconds(5));

        // Exhaust every non-quarantined identifier.
        for (var i = 0; i < 255; i++)
        {
            allocator.TryAllocate(out _).Should().BeTrue();
        }

        allocator.TryAllocate(out _).Should().BeFalse("quarantined id must not be reused yet");
        allocator.QuarantinedCount.Should().Be(1);

        time.Advance(TimeSpan.FromSeconds(5));
        allocator.TryAllocate(out var reused).Should().BeTrue();
        reused.Should().Be(id);
    }
}
