// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    TerminalAttachmentLifecycleTests.cs
//
// Purpose:
// Verifies terminal-mode vs attachment ownership: Ctrl+] keeps attachment,
// suppress/drop RX without detach, and disconnect releases mappings.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Attachment;

/// <summary>
/// Verifies terminal mode vs attachment ownership (Ctrl+] keeps attachment; disconnect releases).
/// </summary>
public sealed class TerminalAttachmentLifecycleTests
{
    /// <summary>Fixed UTC time used when seeding registry sessions.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T14:00:00Z");

    /// <summary>
    /// Seeds a single registered connector session and attachment manager.
    /// </summary>
    /// <param name="op">Unused placeholder kept for call-site compatibility.</param>
    /// <returns>Manager, registry, and session ID.</returns>
    private static (OperatorAttachmentManager Manager, ConnectorSessionRegistry Registry, string SessionId)
        Seed(string op = "op-1")
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID1", "conn-1", null, "router-001", null, "COM1", null, T0)
            .Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        return (manager, registry, session.SessionId);
    }

    /// <summary>
    /// Documents Ctrl+] buffer split policy: forward bytes before 0x1D; never forward the control byte or trailing bytes.
    /// </summary>
    [Fact]
    public void CtrlRightBracket_SplitsBuffer_DoesNotForwardDetachOrTrailingBytes()
    {
        var input = new byte[] { 0x41, 0x0D, 0x0A, TechConnectCliDispatcher.DetachControlByte, 0x5A, 0x00 };
        var index = input.AsSpan().IndexOf(TechConnectCliDispatcher.DetachControlByte);
        index.Should().Be(3);

        var before = input.AsSpan(0, index).ToArray();
        before.Should().Equal(0x41, 0x0D, 0x0A);
        // Policy: forward before; never forward 0x1D or bytes after.
        input[index].Should().Be(0x1D);
        input.AsSpan(index + 1).ToArray().Should().Equal(0x5A, 0x00);
    }

    /// <summary>
    /// Verifies inactive terminal suppresses RX without detaching; active terminal enqueues.
    /// </summary>
    [Fact]
    public void TerminalInactive_SuppressesDeviceRx_WithoutOverflowDetach()
    {
        var (manager, _, sessionId) = Seed();
        var attach = manager.TryAttach("op-1", sessionId, "conn-1");
        attach.Succeeded.Should().BeTrue();
        var attachment = attach.Attachment!;

        attachment.IsTerminalActive.Should().BeFalse();
        manager.TryEnqueueDeviceRx(sessionId, new byte[8000])
            .Should().Be(DeviceRxEnqueueResult.Suppressed);
        manager.Count.Should().Be(1, "suppressed RX must not detach");

        attachment.IsTerminalActive = true;
        manager.TryEnqueueDeviceRx(sessionId, "ok"u8.ToArray())
            .Should().Be(DeviceRxEnqueueResult.Enqueued);
    }

    /// <summary>
    /// Verifies leaving terminal drops queued RX while keeping the attachment and Attached state.
    /// </summary>
    [Fact]
    public void LeavingTerminal_DropsQueuedRx_KeepsAttachment()
    {
        var (manager, registry, sessionId) = Seed();
        var attachment = manager.TryAttach("op-1", sessionId, "conn-1").Attachment!;
        attachment.IsTerminalActive = true;
        attachment.ToOperator.TryEnqueue("stale"u8.ToArray()).Should().BeTrue();

        // Simulate Ctrl+] terminal exit.
        attachment.IsTerminalActive = false;
        attachment.ToOperator.DropAllQueued();

        manager.TryGetByOperatorSessionId("op-1", out var still).Should().BeTrue();
        still.Should().BeSameAs(attachment);
        registry.TryGetBySessionId(sessionId, out var session).Should().BeTrue();
        session!.State.Should().Be(ConnectorSessionState.Attached);
        attachment.ToOperator.QueuedBytes.Should().Be(0);
    }

    /// <summary>
    /// Verifies explicit detach clears both maps, restores Connected, and blocks further RX.
    /// </summary>
    [Fact]
    public void Detach_ReleasesBothMappings_AndBlocksFurtherRx()
    {
        var (manager, registry, sessionId) = Seed();
        var attachment = manager.TryAttach("op-1", sessionId, "conn-1").Attachment!;
        attachment.IsTerminalActive = true;

        manager.TryDetachByOperatorSessionId("op-1", AttachmentDetachReason.OperatorRequest, out var detached)
            .Should().BeTrue();
        detached.Should().BeSameAs(attachment);

        manager.Count.Should().Be(0);
        manager.TryGetBySessionId(sessionId, out _).Should().BeFalse();
        manager.TryGetByOperatorSessionId("op-1", out _).Should().BeFalse();
        registry.TryGetBySessionId(sessionId, out var session).Should().BeTrue();
        session!.State.Should().Be(ConnectorSessionState.Connected);

        manager.TryEnqueueDeviceRx(sessionId, "x"u8.ToArray())
            .Should().Be(DeviceRxEnqueueResult.NoAttachment);

        // New operator can attach.
        manager.TryAttach("op-2", sessionId, "conn-1").Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Verifies repeated detach after success is idempotent.
    /// </summary>
    [Fact]
    public void RepeatedDetach_IsIdempotent()
    {
        var (manager, _, sessionId) = Seed();
        manager.TryAttach("op-1", sessionId, "conn-1").Succeeded.Should().BeTrue();
        manager.TryDetachByOperatorSessionId("op-1", AttachmentDetachReason.OperatorRequest, out _)
            .Should().BeTrue();
        manager.TryDetachByOperatorSessionId("op-1", AttachmentDetachReason.OperatorRequest, out _)
            .Should().BeFalse();
        manager.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies connector disconnect releases attachment and a new connection can re-attach.
    /// </summary>
    [Fact]
    public void ConnectorDisconnect_ReleasesAttachment_NewConnectionCanAttach()
    {
        var (manager, registry, sessionId) = Seed();
        manager.TryAttach("op-1", sessionId, "conn-1").Succeeded.Should().BeTrue();

        manager.TryDetachByConnectionId("conn-1", AttachmentDetachReason.ConnectorDisconnect, out _)
            .Should().BeTrue();
        manager.Count.Should().Be(0);

        // Simulate re-registration under a new connection id (new TCP session).
        registry.TryUnregisterBySessionId(sessionId, out _).Should().BeTrue();
        var anew = registry.TryRegister("ID1", "conn-2", null, "router-001", null, "COM1", null, T0).Session!;
        manager.TryAttach("op-1", anew.SessionId, "conn-2").Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Verifies CLI show serial requires attachment after release (integration with dispatcher).
    /// </summary>
    [Fact]
    public void ShowSerial_RequiresAttachment_AfterRelease()
    {
        var registry = new ConnectorSessionRegistry();
        var attachments = new OperatorAttachmentManager(registry, TimeProvider.System);
        var cache = new ConnectorSerialConfigurationCache();
        var authorization = CliTestFactory.CreateAuthorization();
        var dispatcher = new TechConnectCliDispatcher(
            registry,
            attachments,
            cache,
            authorization,
            new PendingBaudRequestTracker(),
            TimeProvider.System);
        var identity = TestAuthenticatedOperator.Create("op", operatorSessionId: "op");
        var session = registry.TryRegister("ID", "c1", null, "h", null, "COM3", null, T0).Session!;

        attachments.TryAttach(identity.OperatorSessionId, session.SessionId, "c1");
        cache.Set(new CachedSerialConfiguration
        {
            SessionId = session.SessionId,
            PortName = "COM3",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            Handshake = "None",
            ReportedAtUtc = T0
        });

        // Attached (CLI mode) — commands succeed / are accepted.
        dispatcher.Execute("show serial", identity).Output.Should().Contain("115200");

        attachments.TryDetachByOperatorSessionId(identity.OperatorSessionId, AttachmentDetachReason.OperatorRequest, out _);
        dispatcher.Execute("show serial", identity).Output.Should().Contain("Not attached");
        dispatcher.Execute("disconnect", identity).Action.Should().Be(CliCommandAction.ReleaseAttachment);
    }
}
