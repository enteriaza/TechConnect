// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorSessionSnapshotTests.cs
//
// Purpose:
// Verifies ConnectorSessionSnapshotAssembler projections, pending-baud scoping,
// concurrent list/unregister safety, and CLI formatter integration.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Sessions;

/// <summary>
/// Regression tests for session snapshot assembly and related CLI display formatting.
/// </summary>
public sealed class ConnectorSessionSnapshotTests
{
    /// <summary>Fixed UTC time for deterministic snapshot durations.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T14:00:00Z");

    /// <summary>
    /// Verifies a registered session without attachment projects Connected with no operator/serial/pending baud.
    /// </summary>
    [Fact]
    public void Build_ConnectedWithoutAttachment()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        var session = registry.TryRegister(
            "AABBCCDD11223344",
            "c1",
            "10.0.0.1:1",
            "edge-1",
            "2.1.0",
            "COM1",
            "Tls13",
            T0).Session!;

        var snap = ConnectorSessionSnapshotAssembler.Build(
            session,
            attachments,
            serial,
            pending,
            T0.AddMinutes(5));

        snap.ConnectionState.Should().Be("Connected");
        snap.OperatorAttached.Should().BeFalse();
        snap.OperatorUsername.Should().BeNull();
        snap.ConnectionDuration.Should().Be(TimeSpan.FromMinutes(5));
        snap.Hostname.Should().Be("edge-1");
        snap.ApplicationVersion.Should().Be("2.1.0");
        snap.SerialConfigurationReported.Should().BeFalse();
        snap.BaudRequestPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies attachment, terminal, serial cache, and pending baud fields assemble correctly.
    /// </summary>
    [Fact]
    public void Build_WithAttachmentAndSerialAndPendingBaud()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        var op = TestAuthenticatedOperator.Create("noc1", operatorSessionId: "op-1");
        var session = registry.TryRegister(
            "ID1",
            "c1",
            "10.0.0.2:2",
            "host",
            "1.0",
            "COM3",
            "Tls12",
            T0).Session!;

        attachments.TryAttach(op.OperatorSessionId, session.SessionId, "c1", op).Succeeded.Should().BeTrue();
        attachments.TryGetBySessionId(session.SessionId, out var attachment);
        attachment!.IsTerminalActive = true;

        serial.Set(new CachedSerialConfiguration
        {
            SessionId = session.SessionId,
            PortName = "COM3",
            BaudRate = 9600,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            Handshake = "None",
            ReportedAtUtc = T0.AddSeconds(10)
        });

        pending.TryRegister(op.OperatorSessionId, session.SessionId, 115200, out _, out _)
            .Should().BeTrue();

        var snap = ConnectorSessionSnapshotAssembler.Build(
            session,
            attachments,
            serial,
            pending,
            T0.AddMinutes(1));

        snap.OperatorAttached.Should().BeTrue();
        snap.OperatorUsername.Should().Be("noc1");
        snap.TerminalActive.Should().BeTrue();
        snap.SerialConfigurationReported.Should().BeTrue();
        snap.BaudRate.Should().Be(9600);
        snap.BaudRequestPending.Should().BeTrue();
        snap.PendingBaudRate.Should().Be(115200);
        snap.ConnectionState.Should().Be("Connected");
    }

    /// <summary>
    /// Verifies BuildAll reflects independent attachment state per session.
    /// </summary>
    [Fact]
    public void BuildAll_MultipleSessions_IndependentAttachment()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        var a = registry.TryRegister("A", "c1", null, "a", null, "COM1", null, T0).Session!;
        var b = registry.TryRegister("B", "c2", null, "b", null, "COM2", null, T0.AddSeconds(1)).Session!;
        var op = TestAuthenticatedOperator.Create("op", operatorSessionId: "op-a");
        attachments.TryAttach(op.OperatorSessionId, a.SessionId, "c1", op);

        var snaps = ConnectorSessionSnapshotAssembler.BuildAll(
            registry,
            attachments,
            serial,
            pending,
            T0.AddMinutes(1));

        snaps.Should().HaveCount(2);
        snaps.Single(s => s.SessionId == a.SessionId).OperatorAttached.Should().BeTrue();
        snaps.Single(s => s.SessionId == b.SessionId).OperatorAttached.Should().BeFalse();
    }

    /// <summary>
    /// Verifies pending baud on one connector does not appear on another session snapshot.
    /// </summary>
    [Fact]
    public void PendingBaud_IsScopedToConnectorSession()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        var a = registry.TryRegister("A", "c1", null, "a", null, "COM1", null, T0).Session!;
        var b = registry.TryRegister("B", "c2", null, "b", null, "COM2", null, T0).Session!;
        pending.TryRegister("op1", a.SessionId, 9600, out _, out _).Should().BeTrue();

        var snapB = ConnectorSessionSnapshotAssembler.Build(b, attachments, serial, pending, T0);
        snapB.BaudRequestPending.Should().BeFalse();

        var snapA = ConnectorSessionSnapshotAssembler.Build(a, attachments, serial, pending, T0);
        snapA.BaudRequestPending.Should().BeTrue();
        snapA.PendingBaudRate.Should().Be(9600);
    }

    /// <summary>
    /// Verifies concurrent BuildAll and unregister do not throw.
    /// </summary>
    [Fact]
    public async Task ConcurrentListAndUnregister_DoesNotThrow()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        for (var i = 0; i < 20; i++)
        {
            registry.TryRegister($"ID{i:X4}{i:X4}{i:X4}{i:X4}", $"c{i}", null, $"h{i}", null, "COM1", null, T0);
        }

        var errors = new List<Exception>();
        var listTask = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 50; i++)
                {
                    _ = ConnectorSessionSnapshotAssembler.BuildAll(
                        registry,
                        attachments,
                        serial,
                        pending,
                        T0);
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        });

        var removeTask = Task.Run(() =>
        {
            try
            {
                foreach (var session in registry.List().ToArray())
                {
                    registry.TryUnregisterBySessionId(session.SessionId, out _);
                }
            }
            catch (Exception ex)
            {
                lock (errors)
                {
                    errors.Add(ex);
                }
            }
        });

        await Task.WhenAll(listTask, removeTask);
        errors.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies empty and narrow-width session list formatting still includes session identity.
    /// </summary>
    [Fact]
    public void FormatSessionList_EmptyAndNarrowWidth()
    {
        ConnectorSessionDisplayFormatter.FormatSessionList([], 80)
            .Should().Be("No active connector sessions.");

        var (registry, attachments, serial, pending) = CreateParts();
        var session = registry.TryRegister(
            "AABBCCDD11223344",
            "c1",
            "10.0.0.1:49152",
            "very-long-hostname-that-must-truncate",
            "1.2.3",
            "COM1",
            "Tls13",
            T0).Session!;
        var snap = ConnectorSessionSnapshotAssembler.Build(session, attachments, serial, pending, T0);

        var narrow = ConnectorSessionDisplayFormatter.FormatSessionList([snap], 50);
        narrow.Should().Contain(session.SessionId);
        narrow.Should().Contain("Connected");

        var wide = ConnectorSessionDisplayFormatter.FormatSessionList([snap], 100);
        wide.Should().Contain("SESSION");
        wide.Should().Contain("VERSION");
        wide.Should().Contain("1.2.3");
    }

    /// <summary>
    /// Verifies detail formatting shows Not reported when serial has not been cached.
    /// </summary>
    [Fact]
    public void FormatSessionDetail_MissingSerial_ShowsNotReported()
    {
        var (registry, attachments, serial, pending) = CreateParts();
        var session = registry.TryRegister("ID", "c1", null, null, null, null, null, T0).Session!;
        var snap = ConnectorSessionSnapshotAssembler.Build(session, attachments, serial, pending, T0);
        var text = ConnectorSessionDisplayFormatter.FormatSessionDetail(snap);
        text.Should().Contain("Not reported");
        text.Should().Contain("Attached");
        text.Should().Contain("No");
        text.Should().Contain("Pending Baud Request");
    }

    /// <summary>
    /// Creates registry, attachment manager, serial cache, and pending baud tracker for assembly tests.
    /// </summary>
    /// <returns>Fresh interdependent components sharing one registry.</returns>
    private static (
        ConnectorSessionRegistry Registry,
        OperatorAttachmentManager Attachments,
        ConnectorSerialConfigurationCache Serial,
        PendingBaudRequestTracker Pending) CreateParts()
    {
        var registry = new ConnectorSessionRegistry();
        var attachments = new OperatorAttachmentManager(registry, TimeProvider.System);
        return (registry, attachments, new ConnectorSerialConfigurationCache(), new PendingBaudRequestTracker());
    }
}
