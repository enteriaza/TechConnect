// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorDisconnectServiceTests.cs
//
// Purpose:
// Verifies administrative connector disconnect authorization, ConnectionId safety, cleanup observation, and cancellation commit rules.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Connector;
using TechConnect.Server.Sessions;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Unit/integration tests for ConnectorDisconnectService teardown and cleanup observation.
/// </summary>
public sealed class ConnectorDisconnectServiceTests
{
    /// <summary>Fixed UTC time for seeded registry sessions.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T16:00:00Z");

    /// <summary>
    /// Verifies privilege below operator boundary is unauthorized; boundary reaches NotConnected for missing session.
    /// </summary>

    [Fact]
    public async Task Disconnect_RequiresPrivilege7()
    {
        var (service, _, _, audit) = CreateHarness();
        var below = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1));

        var denied = await service.DisconnectAsync(below, "deadbeef", "conn-1", CancellationToken.None);
        denied.Status.Should().Be(ConnectorDisconnectStatus.Unauthorized);
        audit.GetRecent(10).Should().ContainSingle(e => e.EventType == AuditEventType.AuthorizationDenied);

        var at7 = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.OperatorBoundary);
        var allowedAuth = await service.DisconnectAsync(at7, "deadbeef", "conn-1", CancellationToken.None);
        allowedAuth.Status.Should().Be(ConnectorDisconnectStatus.NotConnected);
    }

    /// <summary>
    /// Verifies unknown session returns NotConnected without teardown.
    /// </summary>

    [Fact]
    public async Task Disconnect_UnknownSession_ReturnsNotConnected()
    {
        var (service, _, _, _) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);

        var result = await service.DisconnectAsync(admin, "deadbeef", "conn-1", CancellationToken.None);
        result.Status.Should().Be(ConnectorDisconnectStatus.NotConnected);
        result.Message.Should().Contain("not connected");
    }

    /// <summary>
    /// Verifies Completed when simulated listener cleanup removes registry/tracker entries.
    /// </summary>

    [Fact]
    public async Task Disconnect_Succeeds_WhenListenerCleanupRuns()
    {
        var (service, registry, tracker, audit) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var registered = registry.TryRegister("CERT1", "conn-a", "10.0.0.1:1", "rtr", null, "COM1", null, T0);
        registered.Succeeded.Should().BeTrue();
        var session = registered.Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(session.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            connection.RegisteredSessionId = session.SessionId;
            tracker.TryAdd(connection).Should().BeTrue();

            var cleanup = Task.Run(async () =>
            {
                while (connection.State != ConnectorConnectionState.Disconnecting &&
                       connection.State != ConnectorConnectionState.Disconnected)
                {
                    await Task.Delay(5);
                }

                // Simulate listener finally: detach path, registry, tracker.
                registry.TryUnregisterByConnectionId(connection.ConnectionId, out _).Should().BeTrue();
                tracker.TryRemove(connection.ConnectionId, out _).Should().BeTrue();
            });

            var result = await service.DisconnectAsync(
                admin,
                session.SessionId,
                session.ConnectionId,
                CancellationToken.None,
                completionWait: TimeSpan.FromSeconds(5));

            await cleanup;
            result.Status.Should().Be(ConnectorDisconnectStatus.Completed);
            result.Message.Should().Contain("disconnected successfully");
            audit.GetRecent(20).Select(e => e.EventType).Should().Contain(
                AuditEventType.AdministrativeDisconnectInitiated);
            audit.GetRecent(20).Select(e => e.EventType).Should().Contain(
                AuditEventType.AdministrativeDisconnectSucceeded);
        }
    }

    /// <summary>
    /// Verifies stale ConnectionId does not dispose a replacement connection.
    /// </summary>

    [Fact]
    public async Task Disconnect_ConnectionIdMismatch_DoesNotTearDownReplacement()
    {
        var (service, registry, tracker, _) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var first = registry.TryRegister("CERT1", "conn-old", null, "h1", null, null, null, T0).Session!;
        registry.TryUnregisterByConnectionId("conn-old", out _).Should().BeTrue();
        var replacement = registry.TryRegister("CERT1", "conn-new", null, "h2", null, null, null, T0).Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(replacement.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            var result = await service.DisconnectAsync(
                admin,
                replacement.SessionId,
                expectedConnectionId: first.ConnectionId, // stale id from before confirm
                CancellationToken.None);

            result.Status.Should().Be(ConnectorDisconnectStatus.NotConnected);
            connection.State.Should().Be(ConnectorConnectionState.Authenticated);
            tracker.TryGet(replacement.ConnectionId, out _).Should().BeTrue();
            await connection.DisposeAsync();
            tracker.TryRemove(replacement.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Verifies concurrent disconnects do not double-complete teardown.
    /// </summary>

    [Fact]
    public async Task ConcurrentDisconnect_SecondIsAlreadyDisconnectingOrNotConnected()
    {
        var (service, registry, tracker, _) = CreateHarness();
        var admin1 = TestAuthenticatedOperator.Create("admin1", PrivilegeLevel.AdministratorBoundary, operatorSessionId: "a1");
        var admin2 = TestAuthenticatedOperator.Create("admin2", PrivilegeLevel.AdministratorBoundary, operatorSessionId: "a2");
        var session = registry.TryRegister("CERT1", "conn-race", null, "h", null, null, null, T0).Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(session.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            var cleanup = Task.Run(async () =>
            {
                while (connection.State == ConnectorConnectionState.Authenticated)
                {
                    await Task.Delay(5);
                }

                await Task.Delay(30);
                registry.TryUnregisterByConnectionId(connection.ConnectionId, out _);
                tracker.TryRemove(connection.ConnectionId, out _);
            });

            var t1 = service.DisconnectAsync(admin1, session.SessionId, session.ConnectionId, CancellationToken.None);
            var t2 = service.DisconnectAsync(admin2, session.SessionId, session.ConnectionId, CancellationToken.None);
            var results = await Task.WhenAll(t1, t2);
            await cleanup;

            results.Any(r =>
                    r.Status == ConnectorDisconnectStatus.Completed ||
                    r.Status == ConnectorDisconnectStatus.CleanupIncomplete)
                .Should().BeTrue();
            foreach (var status in results.Select(r => r.Status))
            {
                status.Should().BeOneOf(
                    ConnectorDisconnectStatus.AlreadyDisconnecting,
                    ConnectorDisconnectStatus.NotConnected,
                    ConnectorDisconnectStatus.Completed,
                    ConnectorDisconnectStatus.CleanupIncomplete);
            }

            results.Count(r => r.Status == ConnectorDisconnectStatus.Completed).Should().BeLessThanOrEqualTo(1);
        }
    }

    /// <summary>
    /// Verifies TryResolveLiveSession returns null when no tracked live connection exists.
    /// </summary>

    [Fact]
    public void TryResolveLiveSession_IgnoresDisconnectingConnections()
    {
        var (service, registry, tracker, _) = CreateHarness();
        var session = registry.TryRegister("CERT1", "conn-x", null, "h", null, null, null, T0).Session!;
        // No tracked connection → not live
        service.TryResolveLiveSession(session.SessionId).Should().BeNull();
        tracker.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies cleanup timeout returns CleanupIncomplete with failure audit, not success.
    /// </summary>

    [Fact]
    public async Task Disconnect_CleanupTimeout_ReturnsCleanupIncomplete_NotSuccess()
    {
        var (service, registry, tracker, audit) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var session = registry.TryRegister("CERT1", "conn-slow", null, "h", null, null, null, T0).Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(session.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            // No listener cleanup simulation — registry/tracker remain until we finish asserting.
            var result = await service.DisconnectAsync(
                admin,
                session.SessionId,
                session.ConnectionId,
                CancellationToken.None,
                completionWait: TimeSpan.FromMilliseconds(80));

            result.Status.Should().Be(ConnectorDisconnectStatus.CleanupIncomplete);
            result.TeardownCommitted.Should().BeTrue();
            result.Message.Should().Contain("cleanup has not finished");
            connection.State.Should().Be(ConnectorConnectionState.Disconnected);

            // Original session still registered under the same ConnectionId.
            registry.TryGetBySessionId(session.SessionId, out var still).Should().BeTrue();
            still!.ConnectionId.Should().Be(session.ConnectionId);

            var failed = audit.GetRecent(20)
                .Where(e => e.EventType == AuditEventType.AdministrativeDisconnectFailed)
                .ToList();
            failed.Should().ContainSingle();
            failed[0].Details.Should().NotBeNull();
            failed[0].Details!["reason"].Should().Be("cleanup-incomplete");
            audit.GetRecent(20)
                .Any(e => e.EventType == AuditEventType.AdministrativeDisconnectSucceeded)
                .Should().BeFalse();

            // Late listener cleanup after the admin stopped waiting.
            registry.TryUnregisterByConnectionId(session.ConnectionId, out _).Should().BeTrue();
            tracker.TryRemove(session.ConnectionId, out _).Should().BeTrue();
            registry.TryGetBySessionId(session.SessionId, out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// Verifies a different tracked ConnectionId does not satisfy original cleanup observation.
    /// </summary>

    [Fact]
    public async Task Disconnect_ReplacementConnection_DoesNotSatisfyOriginalCleanup()
    {
        var (service, registry, tracker, _) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var original = registry.TryRegister("CERT1", "conn-orig", null, "h1", null, null, null, T0).Session!;

        var (origConn, peer1, cert1, ca1) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(original.ConnectionId);
        using (peer1)
        using (cert1)
        using (ca1)
        {
            tracker.TryAdd(origConn).Should().BeTrue();

            var resultTask = service.DisconnectAsync(
                admin,
                original.SessionId,
                original.ConnectionId,
                CancellationToken.None,
                completionWait: TimeSpan.FromMilliseconds(200));

            // While waiting, simulate: original disposed path removes tracker entry incorrectly early
            // then a replacement registers under a NEW session — must not count as original cleanup
            // if original ConnectionId were still in the registry (we keep original registered).
            await Task.Delay(30);
            // Dispose will happen inside DisconnectAsync; do not register replacement under same sessionId.
            // Register a different session for same identity only after unregistering original connection claim.
            // Here we only add a second tracked connection with a different id while original session remains.
            var (replacement, peer2, cert2, ca2) =
                await TestConnectorConnectionFactory.CreateAuthenticatedAsync("conn-repl");
            using (peer2)
            using (cert2)
            using (ca2)
            {
                tracker.TryAdd(replacement).Should().BeTrue();

                var result = await resultTask;
                result.Status.Should().Be(ConnectorDisconnectStatus.CleanupIncomplete);
                // Original ConnectionId must still be the cleanup subject; replacement remains tracked.
                tracker.TryGet("conn-repl", out _).Should().BeTrue();
                registry.TryGetBySessionId(original.SessionId, out var still).Should().BeTrue();
                still!.ConnectionId.Should().Be(original.ConnectionId);

                await replacement.DisposeAsync();
                tracker.TryRemove("conn-repl", out _);
            }

            registry.TryUnregisterByConnectionId(original.ConnectionId, out _);
            tracker.TryRemove(original.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Verifies caller cancel after commit still disposes the connection.
    /// </summary>

    [Fact]
    public async Task Disconnect_CallerCancelAfterCommit_StillDisposes()
    {
        var (service, registry, tracker, audit) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var session = registry.TryRegister("CERT1", "conn-cancel", null, "h", null, null, null, T0).Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(session.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            using var cts = new CancellationTokenSource();
            // Cancel immediately; if it races before commit we may get OperationCanceledException.
            // After MarkDisconnecting, cancel must not prevent DisposeAsync.
            var cleanup = Task.Run(async () =>
            {
                while (connection.State == ConnectorConnectionState.Authenticated)
                {
                    await Task.Delay(2);
                }

                cts.Cancel();
                await Task.Delay(20);
                registry.TryUnregisterByConnectionId(connection.ConnectionId, out _);
                tracker.TryRemove(connection.ConnectionId, out _);
            });

            ConnectorDisconnectResult? result = null;
            Exception? thrown = null;
            try
            {
                result = await service.DisconnectAsync(
                    admin,
                    session.SessionId,
                    session.ConnectionId,
                    cts.Token,
                    completionWait: TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException ex)
            {
                thrown = ex;
            }

            await cleanup;
            connection.State.Should().Be(ConnectorConnectionState.Disconnected);
            if (thrown is null)
            {
                result.Should().NotBeNull();
                result!.TeardownCommitted.Should().BeTrue();
                result.Status.Should().BeOneOf(
                    ConnectorDisconnectStatus.Completed,
                    ConnectorDisconnectStatus.CleanupIncomplete);
                audit.GetRecent(20).Should().Contain(e =>
                    e.EventType == AuditEventType.AdministrativeDisconnectInitiated);
            }
        }
    }

    /// <summary>
    /// Verifies pre-commit cancellation throws and leaves the connection authenticated.
    /// </summary>

    [Fact]
    public async Task Disconnect_PreCommitCancellation_DoesNotDispose()
    {
        var (service, registry, tracker, _) = CreateHarness();
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);
        var session = registry.TryRegister("CERT1", "conn-precancel", null, "h", null, null, null, T0).Session!;

        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(session.ConnectionId);
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var act = async () => await service.DisconnectAsync(
                admin,
                session.SessionId,
                session.ConnectionId,
                cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            connection.State.Should().Be(ConnectorConnectionState.Authenticated);
            tracker.TryGet(session.ConnectionId, out _).Should().BeTrue();
            await connection.DisposeAsync();
            tracker.TryRemove(session.ConnectionId, out _);
        }
    }

    /// <summary>
    /// Creates disconnect service harness with in-memory audit, registry, and tracker.
    /// </summary>
    /// <returns>Service and collaborating test doubles.</returns>
    private static (
        ConnectorDisconnectService Service,
        ConnectorSessionRegistry Registry,
        ConnectorConnectionTracker Tracker,
        InMemoryAuditEventStore Audit) CreateHarness()
    {
        var audit = CliTestFactory.CreateAudit();
        var registry = new ConnectorSessionRegistry();
        var tracker = new ConnectorConnectionTracker();
        var authorization = CliTestFactory.CreateAuthorization(audit);
        var service = CliTestFactory.CreateDisconnectService(registry, tracker, authorization, audit);
        return (service, registry, tracker, audit);
    }
}
