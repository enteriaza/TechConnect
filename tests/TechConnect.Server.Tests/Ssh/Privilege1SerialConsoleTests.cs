// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    Privilege1SerialConsoleTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (Privilege1SerialConsoleTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Focused coverage: privilege 1+ can connect (attach + serial config + terminal), TX/RX, Ctrl+],
/// resume with attach, and disconnect (release attachment).
/// Administrative serial commands remain at their higher minimums.
/// </summary>
public sealed class Privilege1SerialConsoleTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Verifies: Privilege1 Connect Tx Rx Ctrl Bracket Attach Disconnect.
    /// </summary>
    [Fact]
    public async Task Privilege1_ConnectTxRxCtrlBracketAttachDisconnect()
    {
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1), operatorSessionId: "op-p1");
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-p1", null, "host-p1", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            await using var pipe = new DuplexMemoryPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var session = new TechConnectCliSession(
                pipe.Server,
                dispatcher,
                attachments,
                registry,
                tracker,
                serialCache,
                pendingBaud,
                authorization,
                disconnect,
                NullLogger.Instance,
                identity,
                enable,
                audit: audit);

            var output = new StringBuilder();
            var reader = Task.Run(async () =>
            {
                var buf = new byte[1024];
                while (!cts.IsCancellationRequested)
                {
                    int n;
                    try
                    {
                        n = await pipe.Client.ReadAsync(buf, cts.Token);
                    }
                    catch
                    {
                        break;
                    }

                    if (n == 0)
                    {
                        break;
                    }

                    lock (output)
                    {
                        output.Append(Encoding.UTF8.GetString(buf, 0, n));
                    }
                }
            });

            var run = session.RunAsync(cts.Token);
            await WaitForAsync(() => Snapshot(output).Contains("TechConnect>", StringComparison.Ordinal));

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"connect {registered.SessionId}\r"),
                cts.Token);
            // connect waits for the connector's serial configuration result before entering terminal.
            await pendingBaud.CompletePendingSerialConfigAsync(registered.SessionId, identity.OperatorSessionId);
            await WaitForAsync(() => Snapshot(output).Contains("Connected to", StringComparison.Ordinal));

            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out var attachment)
                .Should().BeTrue();
            await WaitForAsync(() => attachment!.IsTerminalActive);
            attachment!.IsTerminalActive.Should().BeTrue();

            var tx = new byte[] { 0x00, 0x41, 0x0D, 0xFF };
            await pipe.Client.WriteAsync(tx, cts.Token);
            await WaitForAsync(() => attachment.BytesToDevice >= tx.Length);
            attachment.BytesFromOperator.Should().BeGreaterThanOrEqualTo(tx.Length);

            var rx = new byte[] { 0x1B, 0x5B, 0x41, 0x0A };
            attachments.TryEnqueueDeviceRx(registered.SessionId, rx)
                .Should().Be(DeviceRxEnqueueResult.Enqueued);
            await WaitForAsync(() => attachment.BytesToOperator >= rx.Length);

            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() => Snapshot(output).Contains("still attached", StringComparison.Ordinal));
            attachment.IsTerminalActive.Should().BeFalse();
            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _).Should().BeTrue();

            // Administrative commands remain denied at privilege 1.
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show serial\r"), cts.Token);
            await WaitForAsync(() =>
                Snapshot(output).Contains(OperatorAuthorizationService.PermissionDeniedMessage, StringComparison.Ordinal));
            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"disconnect {registered.SessionId}\r"),
                cts.Token);
            await WaitForAsync(() =>
            {
                var t = Snapshot(output);
                return t.LastIndexOf(OperatorAuthorizationService.PermissionDeniedMessage, StringComparison.Ordinal) >
                       t.IndexOf("show serial", StringComparison.Ordinal);
            });

            // Resume terminal on the retained attachment, then leave again.
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("attach\r"), cts.Token);
            await WaitForAsync(() => Snapshot(output).Contains("Resuming terminal", StringComparison.Ordinal));
            await WaitForAsync(() => attachment.IsTerminalActive);
            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() => !attachment.IsTerminalActive);
            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _).Should().BeTrue();

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("disconnect\r"), cts.Token);
            await WaitForAsync(() => !attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _));

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            try
            {
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Verifies: Privilege7 Retains Attach And Show Serial.
    /// </summary>
    [Fact]
    public void Privilege7_RetainsAttachAndShowSerial()
    {
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7), operatorSessionId: "op-p7");
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-p7", null, "host-p7", null, "COM1", null, T0).Session!;

        var connect = dispatcher.Execute($"connect {registered.SessionId} baud 115200", identity);
        connect.Action.Should().Be(CliCommandAction.Connect);
        connect.ConnectSessionId.Should().Be(registered.SessionId);
        connect.SerialSettings!.BaudRate.Should().Be(115200);
        attachments.TryAttach(identity.OperatorSessionId, registered.SessionId, registered.ConnectionId)
            .Succeeded.Should().BeTrue();

        dispatcher.Execute("attach", identity).Action.Should().Be(CliCommandAction.Attach);
        dispatcher.Execute("show serial", identity).Output.Should().Contain("Not reported");
        dispatcher.Execute("show serial", identity).Output.Should()
            .NotBe(OperatorAuthorizationService.PermissionDeniedMessage);
        dispatcher.Execute("disconnect", identity).Action.Should().Be(CliCommandAction.ReleaseAttachment);
        dispatcher.Execute($"disconnect {registered.SessionId}", identity).Action.Should()
            .Be(CliCommandAction.AdminDisconnect);
    }

    /// <summary>
    /// Verifies: Policy No Stale Level7Gate On Basic Serial Console Commands.
    /// </summary>
    [Fact]
    public void Policy_NoStaleLevel7Gate_OnBasicSerialConsoleCommands()
    {
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.Connect).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.Attach).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.ShowSerial).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.DisconnectSelf).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.Disconnect).Should().BeFalse();

        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(7), CliCommandId.ShowSerial).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(7), CliCommandId.Disconnect).Should().BeTrue();
    }
    /// <summary>
    /// Performs snapshot.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <returns>The operation result.</returns>

    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }
    /// <summary>
    /// Asynchronously performs wait for.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException("Condition was not met in time.");
    }
}
