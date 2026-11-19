// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorSessionRegistryTests.cs
//
// Purpose:
// Verifies Hello session registration lookup, reject-new identity policy,
// connection-exact unregister, concurrency, and activity touch behaviour.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Tests.Sessions;

/// <summary>
/// Unit tests for <see cref="ConnectorSessionRegistry"/> registration and cleanup invariants.
/// </summary>
public sealed class ConnectorSessionRegistryTests
{
    /// <summary>Fixed UTC connect time for deterministic assertions.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T10:00:00Z");

    /// <summary>
    /// Verifies register, case-insensitive lookup, list, and unregister by session ID.
    /// </summary>
    [Fact]
    public void Register_RetrievesBySessionIdAndIdentity_ListsAndRemoves()
    {
        var registry = new ConnectorSessionRegistry();

        var result = registry.TryRegister(
            connectorIdentity: "ABCD1234EF",
            connectionId: "conn-1",
            remoteEndpoint: "10.0.0.10:49152",
            hostname: "router-001",
            applicationVersion: "1.0.0",
            portName: "COM1",
            tlsProtocol: "Tls13",
            connectedAtUtc: T0);

        result.Succeeded.Should().BeTrue();
        result.Session.Should().NotBeNull();
        var session = result.Session!;
        session.SessionId.Should().MatchRegex("^[0-9a-f]{8}$");
        session.ConnectorIdentity.Should().Be("ABCD1234EF");
        session.Hostname.Should().Be("router-001");
        session.ApplicationVersion.Should().Be("1.0.0");
        session.RemoteEndpoint.Should().Be("10.0.0.10:49152");
        session.ConnectedAtUtc.Should().Be(T0);
        session.LastActivityUtc.Should().Be(T0);
        session.State.Should().Be(ConnectorSessionState.Connected);

        registry.TryGetBySessionId(session.SessionId, out var byId).Should().BeTrue();
        byId.Should().BeSameAs(session);

        registry.TryGetBySessionId(session.SessionId.ToUpperInvariant(), out var byIdCi).Should().BeTrue();
        byIdCi.Should().BeSameAs(session);

        registry.TryGetByIdentity("abcd1234ef", out var byIdentity).Should().BeTrue();
        byIdentity.Should().BeSameAs(session);

        registry.List().Should().ContainSingle().Which.Should().BeSameAs(session);

        registry.TryUnregisterBySessionId(session.SessionId, out var removed).Should().BeTrue();
        removed.Should().BeSameAs(session);
        registry.Count.Should().Be(0);
        registry.TryGetBySessionId(session.SessionId, out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies unknown session lookup returns false with null session.
    /// </summary>
    [Fact]
    public void UnknownSessionLookup_ReturnsFalse()
    {
        var registry = new ConnectorSessionRegistry();
        registry.TryGetBySessionId("deadbeef", out var session).Should().BeFalse();
        session.Should().BeNull();
    }

    /// <summary>
    /// Verifies duplicate certificate identity is rejected without replacing the original.
    /// </summary>
    [Fact]
    public void DuplicateIdentity_IsRejected()
    {
        var registry = new ConnectorSessionRegistry();
        var first = registry.TryRegister("ID1", "c1", "1.1.1.1:1", "h1", "1.0", "COM1", "Tls13", T0);
        first.Succeeded.Should().BeTrue();

        var duplicate = registry.TryRegister("ID1", "c2", "1.1.1.1:2", "h2", "1.0", "COM2", "Tls13", T0);
        duplicate.Succeeded.Should().BeFalse();
        duplicate.Status.Should().Be(ConnectorSessionRegistrationStatus.DuplicateIdentity);
        duplicate.Error.Should().Contain("already registered");
        registry.Count.Should().Be(1);
        registry.List().Single().ConnectionId.Should().Be("c1");
    }

    /// <summary>
    /// Verifies unregistering a rejected duplicate connection does not remove the original session.
    /// </summary>
    [Fact]
    public void DuplicateDisconnect_DoesNotRemoveOriginal()
    {
        var registry = new ConnectorSessionRegistry();
        var first = registry.TryRegister("ID1", "c1", null, "h1", null, "COM1", null, T0);
        first.Succeeded.Should().BeTrue();
        registry.TryRegister("ID1", "c2", null, "h2", null, "COM2", null, T0).Succeeded.Should().BeFalse();

        registry.TryUnregisterByConnectionId("c2", out _).Should().BeFalse();
        registry.Count.Should().Be(1);
        registry.TryGetByIdentity("ID1", out var remaining).Should().BeTrue();
        remaining!.ConnectionId.Should().Be("c1");
        remaining.SessionId.Should().Be(first.Session!.SessionId);
    }

    /// <summary>
    /// Verifies unregister by connection and session ID is idempotent.
    /// </summary>
    [Fact]
    public void Removal_IsIdempotent()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0).Session!;

        registry.TryUnregisterByConnectionId("c1", out _).Should().BeTrue();
        registry.TryUnregisterByConnectionId("c1", out _).Should().BeFalse();
        registry.TryUnregisterBySessionId(session.SessionId, out _).Should().BeFalse();
        registry.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies concurrent same-identity registration yields one success and the rest duplicates.
    /// </summary>
    [Fact]
    public void ConcurrentSameIdentity_OnlyOneRegisters()
    {
        var registry = new ConnectorSessionRegistry();
        var successes = 0;
        var duplicates = 0;

        Parallel.For(0, 32, i =>
        {
            var result = registry.TryRegister(
                "SAME",
                $"conn-{i}",
                null,
                $"host-{i}",
                null,
                "COM1",
                null,
                T0);
            if (result.Succeeded)
            {
                Interlocked.Increment(ref successes);
            }
            else if (result.Status == ConnectorSessionRegistrationStatus.DuplicateIdentity)
            {
                Interlocked.Increment(ref duplicates);
            }
        });

        successes.Should().Be(1);
        duplicates.Should().Be(31);
        registry.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies concurrent distinct identities all register successfully.
    /// </summary>
    [Fact]
    public void ConcurrentDifferentIdentities_AllRegister()
    {
        var registry = new ConnectorSessionRegistry();
        const int n = 40;

        Parallel.For(0, n, i =>
        {
            var result = registry.TryRegister(
                $"ID-{i}",
                $"conn-{i}",
                null,
                $"host-{i}",
                null,
                "COM1",
                null,
                T0);
            result.Succeeded.Should().BeTrue();
        });

        registry.Count.Should().Be(n);
        registry.List().Should().HaveCount(n);
    }

    /// <summary>
    /// Verifies activity touch updates only the registered connection's session.
    /// </summary>
    [Fact]
    public void TouchActivity_UpdatesOnlyRegisteredConnection()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0).Session!;
        var later = T0.AddMinutes(5);

        registry.TouchActivity("c1", later);
        registry.TouchActivity("unknown", later.AddMinutes(1));

        session.LastActivityUtc.Should().Be(later);
    }

    /// <summary>
    /// Scenario: concurrent TouchActivity and List while another connection registers.
    /// Expected: no exceptions; each session retains a progressive activity timestamp.
    /// </summary>
    [Fact]
    public void TouchActivity_ConcurrentWithRegisterAndList_IsSafe()
    {
        var registry = new ConnectorSessionRegistry();
        var sessions = new ConnectorSession[32];
        for (var i = 0; i < sessions.Length; i++)
        {
            sessions[i] = registry.TryRegister($"ID-{i}", $"conn-{i}", null, null, null, "COM1", null, T0).Session!;
        }

        Parallel.For(0, sessions.Length, i =>
        {
            for (var n = 0; n < 1_000; n++)
            {
                registry.TouchActivity(sessions[i].ConnectionId, T0.AddMilliseconds(n + 1));
                if ((n & 63) == 0)
                {
                    _ = registry.List();
                }
            }
        });

        foreach (var session in sessions)
        {
            session.LastActivityUtc.Should().NotBeNull();
            session.LastActivityUtc!.Value.Should().BeAfter(T0);
        }

        registry.Count.Should().Be(sessions.Length);
    }

    /// <summary>
    /// Scenario: LastActivityUtc updated via cached session reference without registry.TouchActivity.
    /// Expected: snapshot List observes the updated timestamp (PERF-SRV-02 hot path).
    /// </summary>
    [Fact]
    public void CachedSession_LastActivityWrite_VisibleInListSnapshot()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0).Session!;
        var later = T0.AddSeconds(30);

        // Mirrors ConnectorListenerHostedService updating connection.RegisteredSession.
        session.ObserveActivity(later);

        registry.List().Single().LastActivityUtc.Should().Be(later);
    }

    /// <summary>
    /// Scenario: concurrent ObserveActivity with increasing and decreasing candidates.
    /// Expected: final timestamp equals the maximum observed value (never moves backwards).
    /// </summary>
    [Fact]
    public void ObserveActivity_ConcurrentWriters_PreservesMaximum()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0).Session!;
        var max = T0.AddMilliseconds(5_000);

        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i <= 5_000; i++)
            {
                // Mix newer and older candidates across threads.
                var candidate = t % 2 == 0
                    ? T0.AddMilliseconds(i)
                    : T0.AddMilliseconds(5_000 - i);
                session.ObserveActivity(candidate);
            }
        });

        session.LastActivityUtc.Should().Be(max);
    }

    /// <summary>
    /// Scenario: TouchActivity after unregister.
    /// Expected: no-op; cannot resurrect or mutate a replacement session under a reused id.
    /// </summary>
    [Fact]
    public void TouchActivity_AfterUnregister_DoesNotResurrect()
    {
        var registry = new ConnectorSessionRegistry();
        var first = registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0).Session!;
        registry.TryUnregisterByConnectionId("c1", out _).Should().BeTrue();

        registry.TouchActivity("c1", T0.AddHours(1));
        first.LastActivityUtc.Should().Be(T0);

        var second = registry.TryRegister("ID1", "c2", null, null, null, "COM1", null, T0.AddMinutes(1)).Session!;
        registry.TouchActivity("c1", T0.AddHours(2)); // old connection id
        second.LastActivityUtc.Should().Be(T0.AddMinutes(1));
        registry.TouchActivity("c2", T0.AddHours(3));
        second.LastActivityUtc.Should().Be(T0.AddHours(3));
    }

    /// <summary>
    /// Verifies <see cref="ConnectorSessionRegistry.ClearAll"/> empties all indexes.
    /// </summary>
    [Fact]
    public void ClearAll_EmptiesRegistry()
    {
        var registry = new ConnectorSessionRegistry();
        registry.TryRegister("ID1", "c1", null, null, null, "COM1", null, T0);
        registry.TryRegister("ID2", "c2", null, null, null, "COM2", null, T0);
        registry.ClearAll();
        registry.Count.Should().Be(0);
        registry.List().Should().BeEmpty();
    }
}
