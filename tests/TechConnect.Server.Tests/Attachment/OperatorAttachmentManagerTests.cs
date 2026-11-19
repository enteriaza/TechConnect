// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    OperatorAttachmentManagerTests.cs
//
// Purpose:
// Verifies attach/detach ownership invariants, reject-new concurrency, device-RX
// enqueue outcomes, and cleanup isolation across connector connections.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Tests.Attachment;

/// <summary>
/// Unit tests for <see cref="OperatorAttachmentManager"/> ownership and enqueue policy.
/// </summary>
public sealed class OperatorAttachmentManagerTests
{
    /// <summary>Fixed UTC time used when seeding registry sessions.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Seeds a registry with one registered session and a manager bound to it.
    /// </summary>
    /// <param name="identity">Certificate identity thumbprint for registration.</param>
    /// <param name="connectionId">Connection ID owning the session.</param>
    /// <returns>Manager, registry, and allocated session ID.</returns>
    private static (OperatorAttachmentManager Manager, ConnectorSessionRegistry Registry, string SessionId) Seed(
        string identity = "ID1",
        string connectionId = "conn-1")
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister(
            identity,
            connectionId,
            "10.0.0.1:1",
            "router-001",
            "1.0",
            "COM1",
            "Tls13",
            T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        return (manager, registry, session.SessionId);
    }

    /// <summary>
    /// Verifies a successful attach indexes the attachment and marks the session Attached.
    /// </summary>
    [Fact]
    public void Attach_AvailableConnector_Succeeds()
    {
        var (manager, registry, sessionId) = Seed();
        var result = manager.TryAttach("op-1", sessionId, "conn-1");
        result.Succeeded.Should().BeTrue();
        result.Attachment!.SessionId.Should().Be(sessionId);
        registry.TryGetBySessionId(sessionId, out var session).Should().BeTrue();
        session!.State.Should().Be(ConnectorSessionState.Attached);
        manager.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies a second operator cannot attach to an already-attached connector (reject-new).
    /// </summary>
    [Fact]
    public void Reject_SecondOperator_OnSameConnector()
    {
        var (manager, _, sessionId) = Seed();
        manager.TryAttach("op-1", sessionId, "conn-1").Succeeded.Should().BeTrue();
        var second = manager.TryAttach("op-2", sessionId, "conn-1");
        second.Succeeded.Should().BeFalse();
        second.Status.Should().Be(AttachmentStatus.ConnectorAlreadyAttached);
        manager.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies one operator SSH session cannot attach to two connectors at once.
    /// </summary>
    [Fact]
    public void Reject_Operator_AttachingToMultipleConnectors()
    {
        var registry = new ConnectorSessionRegistry();
        var a = registry.TryRegister("A", "c1", null, "h1", null, "COM1", null, T0).Session!;
        var b = registry.TryRegister("B", "c2", null, "h2", null, "COM2", null, T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);

        manager.TryAttach("op-1", a.SessionId, "c1").Succeeded.Should().BeTrue();
        var second = manager.TryAttach("op-1", b.SessionId, "c2");
        second.Succeeded.Should().BeFalse();
        second.Status.Should().Be(AttachmentStatus.OperatorAlreadyAttached);
    }

    /// <summary>
    /// Verifies concurrent attach races against one connector yield exactly one success.
    /// </summary>
    [Fact]
    public void ConcurrentAttach_SameConnector_OnlyOneSucceeds()
    {
        var (manager, _, sessionId) = Seed();
        var successes = 0;
        Parallel.For(0, 32, i =>
        {
            if (manager.TryAttach($"op-{i}", sessionId, "conn-1").Succeeded)
            {
                Interlocked.Increment(ref successes);
            }
        });
        successes.Should().Be(1);
        manager.Count.Should().Be(1);
    }

    /// <summary>
    /// Verifies detach releases both maps, restores Connected, and is idempotent.
    /// </summary>
    [Fact]
    public void Detach_ReleasesBothMappings_Idempotent()
    {
        var (manager, registry, sessionId) = Seed();
        manager.TryAttach("op-1", sessionId, "conn-1").Succeeded.Should().BeTrue();
        manager.TryDetachByOperatorSessionId("op-1", AttachmentDetachReason.OperatorRequest, out var a)
            .Should().BeTrue();
        a!.DetachReason.Should().Be(AttachmentDetachReason.OperatorRequest);
        registry.TryGetBySessionId(sessionId, out var session).Should().BeTrue();
        session!.State.Should().Be(ConnectorSessionState.Connected);

        manager.TryDetachByOperatorSessionId("op-1", AttachmentDetachReason.OperatorRequest, out _)
            .Should().BeFalse();
        manager.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies detach by connection ID releases the matching operator attachment.
    /// </summary>
    [Fact]
    public void ConnectorRemoval_DetachesOperator()
    {
        var (manager, _, sessionId) = Seed();
        manager.TryAttach("op-1", sessionId, "conn-1");
        manager.TryDetachByConnectionId("conn-1", AttachmentDetachReason.ConnectorDisconnect, out var a)
            .Should().BeTrue();
        a!.DetachReason.Should().Be(AttachmentDetachReason.ConnectorDisconnect);
        manager.TryGetByOperatorSessionId("op-1", out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies cleaning one connection's attachment does not remove another's.
    /// </summary>
    [Fact]
    public void StaleConnectionCleanup_DoesNotRemoveOtherAttachment()
    {
        var registry = new ConnectorSessionRegistry();
        var a = registry.TryRegister("A", "c1", null, "h1", null, "COM1", null, T0).Session!;
        var b = registry.TryRegister("B", "c2", null, "h2", null, "COM2", null, T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        manager.TryAttach("op-a", a.SessionId, "c1");
        manager.TryAttach("op-b", b.SessionId, "c2");

        manager.TryDetachByConnectionId("c1", AttachmentDetachReason.ConnectorDisconnect, out _).Should().BeTrue();
        manager.TryGetBySessionId(b.SessionId, out var remaining).Should().BeTrue();
        remaining!.OperatorSessionId.Should().Be("op-b");
    }

    /// <summary>
    /// Verifies failed attach leaves no partial manager or registry attachment state.
    /// </summary>
    [Fact]
    public void FailedAttach_LeavesNoPartialState()
    {
        var (manager, registry, _) = Seed();
        var fail = manager.TryAttach("op-1", "deadbeef", "conn-1");
        fail.Succeeded.Should().BeFalse();
        fail.Status.Should().Be(AttachmentStatus.SessionNotFound);
        manager.Count.Should().Be(0);
        registry.List().Single().State.Should().Be(ConnectorSessionState.Connected);
    }

    /// <summary>
    /// Verifies device RX with no attachment returns <see cref="DeviceRxEnqueueResult.NoAttachment"/>.
    /// </summary>
    [Fact]
    public void EnqueueDeviceRx_NoAttachment_Discards()
    {
        var (manager, _, sessionId) = Seed();
        manager.TryEnqueueDeviceRx(sessionId, "hello"u8.ToArray()).Should().Be(DeviceRxEnqueueResult.NoAttachment);
    }

    /// <summary>
    /// Verifies outbound queue overflow surfaces as <see cref="DeviceRxEnqueueResult.Overflow"/>.
    /// </summary>
    [Fact]
    public async Task EnqueueDeviceRx_Overflow_ReturnsOverflow()
    {
        var (manager, _, sessionId) = Seed();
        var attach = manager.TryAttach("op-1", sessionId, "conn-1", operatorOutboundBytes: 4096);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.IsTerminalActive = true;

        var first = new byte[3000];
        var second = new byte[3000];
        manager.TryEnqueueDeviceRx(sessionId, first).Should().Be(DeviceRxEnqueueResult.Enqueued);
        manager.TryEnqueueDeviceRx(sessionId, second).Should().Be(DeviceRxEnqueueResult.Overflow);

        // Ownership transferred on enqueue: dequeued instance is the first array.
        var got = await attach.Attachment.ToOperator.DequeueAsync(CancellationToken.None);
        ReferenceEquals(got, first).Should().BeTrue();
    }

    /// <summary>
    /// Verifies suppressed (CLI-mode) enqueue retains caller ownership and does not queue bytes.
    /// </summary>
    [Fact]
    public void EnqueueDeviceRx_Suppressed_RetainsCallerOwnership()
    {
        var (manager, _, sessionId) = Seed();
        var attach = manager.TryAttach("op-1", sessionId, "conn-1", operatorOutboundBytes: 4096);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.IsTerminalActive = false;

        var payload = new byte[] { 1, 2, 3 };
        manager.TryEnqueueDeviceRx(sessionId, payload).Should().Be(DeviceRxEnqueueResult.Suppressed);
        attach.Attachment.ToOperator.QueuedBytes.Should().Be(0);
        payload[0] = 9; // still caller-owned
        payload[0].Should().Be(9);
    }

    /// <summary>
    /// Verifies <see cref="OperatorAttachmentManager.ClearAll"/> detaches every attachment.
    /// </summary>
    [Fact]
    public void ClearAll_DetachesEveryone()
    {
        var registry = new ConnectorSessionRegistry();
        var a = registry.TryRegister("A", "c1", null, null, null, "COM1", null, T0).Session!;
        var b = registry.TryRegister("B", "c2", null, null, null, "COM2", null, T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        manager.TryAttach("op-a", a.SessionId, "c1");
        manager.TryAttach("op-b", b.SessionId, "c2");
        manager.ClearAll();
        manager.Count.Should().Be(0);
    }
}
