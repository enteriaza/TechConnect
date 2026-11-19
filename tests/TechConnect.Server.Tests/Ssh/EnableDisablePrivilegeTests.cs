// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    EnableDisablePrivilegeTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (EnableDisablePrivilegeTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for enable/disable privilege elevation and restoration.
/// </summary>
public sealed class EnableDisablePrivilegeTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Verifies: Active Privilege Starts Equal To Original.
    /// </summary>
    [Fact]
    public void ActivePrivilege_StartsEqualToOriginal()
    {
        var identity = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1));
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var ms = new MemoryStream();
        var session = new TechConnectCliSession(
            ms,
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
            enable);

        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(1));
        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
        session.ActivePrivilege.Should().Be(session.AuthenticatedOperator.Privilege);
    }

    /// <summary>
    /// Verifies: Enable Target At Or Below Active Does Not Prompt Or Reduce Privilege.
    /// </summary>
    [Fact]
    public async Task Enable_TargetAtOrBelowActive_DoesNotPromptOrReducePrivilege()
    {
        var identity = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.FromInt(15));
        var enableSvc = CliTestFactory.CreateEnableService(); // no secret configured
        var (output, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(() => text().Contains("TechConnect#", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("enable 7\r"), cts.Token);
                await Task.Delay(100, cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        output.Should().NotContain("Password:");
        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(15));
        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(15));
        output.Should().Contain("TechConnect#");
    }

    /// <summary>
    /// Verifies: Local Enable Secret Success Raises Active Privilege.
    /// </summary>
    [Fact]
    public async Task LocalEnable_SecretSuccess_RaisesActivePrivilege()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("secret");
        var enableSvc = CliTestFactory.CreateEnableService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));
        var (output, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(() => text().Contains("TechConnect>", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("enable 15\r"), cts.Token);
                await WaitForAsync(() => text().Contains("Password:", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("secret\r"), cts.Token);
                await WaitForAsync(() => text().Contains("TechConnect#", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(15));
        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(1));
        output.Should().Contain("Password:");
        output.Should().Contain("TechConnect#");
    }

    /// <summary>
    /// Verifies: Local Enable Wrong Secret Leaves Active Unchanged.
    /// </summary>
    [Fact]
    public async Task LocalEnable_WrongSecret_LeavesActiveUnchanged()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("secret");
        var enableSvc = CliTestFactory.CreateEnableService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));
        var (output, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(() => text().Contains("TechConnect>", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("enable\r"), cts.Token);
                await WaitForAsync(() => text().Contains("Password:", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("wrong\r"), cts.Token);
                await WaitForAsync(
                    () => text().Contains("Enable authentication failed", StringComparison.Ordinal),
                    cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
        output.Should().Contain("Enable authentication failed");
        output.Should().NotContain("wrong");
        output.Should().NotContain("secret");
    }

    /// <summary>
    /// Verifies: Local Enable Missing Secret Hash Fails Closed.
    /// </summary>
    [Fact]
    public async Task LocalEnable_MissingSecretHash_FailsClosed()
    {
        var enableSvc = CliTestFactory.CreateEnableService(enableSecretHash: null);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));
        var (_, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(() => text().Contains("TechConnect>", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("enable\r"), cts.Token);
                await WaitForAsync(() => text().Contains("Password:", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("anything\r"), cts.Token);
                await WaitForAsync(
                    () => text().Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
                          text().Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                    cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
    }

    /// <summary>
    /// Verifies: Disable Returns To Level1.
    /// </summary>
    [Fact]
    public async Task Disable_ReturnsToLevel1()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("secret");
        var enableSvc = CliTestFactory.CreateEnableService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(7));
        var (_, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(() => text().Contains("TechConnect>", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("enable 15\r"), cts.Token);
                await WaitForAsync(() => text().Contains("Password:", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("secret\r"), cts.Token);
                await WaitForAsync(() => text().Contains("TechConnect#", StringComparison.Ordinal), cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("disable\r"), cts.Token);
                await WaitForAsync(
                    () => text().Split("TechConnect>", StringSplitOptions.None).Length >= 3,
                    cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(7));
    }

    /// <summary>
    /// Verifies: Disable From Any Active Returns To Level1.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(11)]
    [InlineData(15)]
    public async Task Disable_FromAnyActive_ReturnsToLevel1(int loginPrivilege)
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("secret");
        var enableSvc = CliTestFactory.CreateEnableService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(loginPrivilege));
        var (_, session) = await RunInteractiveAsync(
            identity,
            enableSvc,
            async (client, text, cts) =>
            {
                await WaitForAsync(
                    () => text().Contains("TechConnect", StringComparison.Ordinal),
                    cts.Token);
                if (loginPrivilege < 15)
                {
                    await client.WriteAsync(Encoding.UTF8.GetBytes("enable 15\r"), cts.Token);
                    await WaitForAsync(() => text().Contains("Password:", StringComparison.Ordinal), cts.Token);
                    await client.WriteAsync(Encoding.UTF8.GetBytes("secret\r"), cts.Token);
                    await WaitForAsync(() => text().Contains("TechConnect#", StringComparison.Ordinal), cts.Token);
                }

                await client.WriteAsync(Encoding.UTF8.GetBytes("disable\r"), cts.Token);
                await WaitForAsync(
                    () =>
                    {
                        var t = text();
                        return t.Contains("TechConnect>", StringComparison.Ordinal) &&
                               (loginPrivilege == 15 ||
                                t.LastIndexOf("TechConnect>", StringComparison.Ordinal) >
                                t.LastIndexOf("TechConnect#", StringComparison.Ordinal));
                    },
                    cts.Token);
                await client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            });

        session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(loginPrivilege));
    }

    /// <summary>
    /// Verifies: Privilege1 Can Connect And Enter Terminal.
    /// </summary>
    [Fact]
    public async Task Privilege1_CanConnectAndEnterTerminal()
    {
        var identity = TestAuthenticatedOperator.Create("ro", PrivilegeLevel.FromInt(1), operatorSessionId: "op-1");
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            await using var pipe = new DuplexMemoryPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("TechConnect>", StringComparison.Ordinal);
                }
            });

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"connect {registered.SessionId}\r"),
                cts.Token);
            // connect waits for the connector's serial configuration result before entering terminal.
            await pendingBaud.CompletePendingSerialConfigAsync(registered.SessionId, identity.OperatorSessionId);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("Connected to", StringComparison.Ordinal);
                }
            });

            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out var attachment)
                .Should().BeTrue();
            await WaitForAsync(() => attachment!.IsTerminalActive);
            attachment!.IsTerminalActive.Should().BeTrue();

            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("still attached", StringComparison.Ordinal);
                }
            });

            attachment.IsTerminalActive.Should().BeFalse();
            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _).Should().BeTrue();

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("disconnect\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return !attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _);
                }
            });

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
    /// Verifies: Privilege7 Can Connect And Enter Terminal.
    /// </summary>
    [Fact]
    public async Task Privilege7_CanConnectAndEnterTerminal()
    {
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.FromInt(7), operatorSessionId: "op-7");
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c7", null, "host7", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();

            await using var pipe = new DuplexMemoryPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
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
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("TechConnect>", StringComparison.Ordinal);
                }
            });

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"connect {registered.SessionId}\r"),
                cts.Token);
            // connect waits for the connector's serial configuration result before entering terminal.
            await pendingBaud.CompletePendingSerialConfigAsync(registered.SessionId, identity.OperatorSessionId);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("Connected to", StringComparison.Ordinal);
                }
            });

            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out var attachment)
                .Should().BeTrue();
            await WaitForAsync(() => attachment!.IsTerminalActive);
            attachment!.IsTerminalActive.Should().BeTrue();

            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("still attached", StringComparison.Ordinal);
                }
            });

            attachment.IsTerminalActive.Should().BeFalse();
            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out _).Should().BeTrue();

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
    /// Verifies: Disable While Terminal Active Keeps Terminal At Level1.
    /// </summary>
    [Fact]
    public async Task Disable_WhileTerminalActive_KeepsTerminalAtLevel1()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("secret");
        var enableSvc = CliTestFactory.CreateEnableService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1), operatorSessionId: "op-d");
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, _) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "cd", null, "host-d", null, "COM1", null, T0).Session!;
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
                enableSvc,
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
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("TechConnect>", StringComparison.Ordinal);
                }
            });

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("enable 15\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("Password:", StringComparison.Ordinal);
                }
            });
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("secret\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("TechConnect#", StringComparison.Ordinal);
                }
            });

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"connect {registered.SessionId}\r"),
                cts.Token);
            // connect waits for the connector's serial configuration result before entering terminal.
            await pendingBaud.CompletePendingSerialConfigAsync(registered.SessionId, identity.OperatorSessionId);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("Connected to", StringComparison.Ordinal);
                }
            });

            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out var attachment)
                .Should().BeTrue();
            await WaitForAsync(() => attachment!.IsTerminalActive);
            attachment!.IsTerminalActive.Should().BeTrue();

            // Leave terminal briefly so disable can be typed at the CLI, then re-enter and
            // prove disable while attached still allows terminal at level 1.
            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("still attached", StringComparison.Ordinal);
                }
            });

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("disable\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    var t = output.ToString();
                    return t.Contains("TechConnect>", StringComparison.Ordinal) &&
                           t.LastIndexOf("TechConnect>", StringComparison.Ordinal) >
                           t.LastIndexOf("TechConnect#", StringComparison.Ordinal);
                }
            });

            session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));
            session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.FromInt(1));
            attachments.TryGetByOperatorSessionId(identity.OperatorSessionId, out attachment)
                .Should().BeTrue();
            attachment!.IsTerminalActive.Should().BeFalse();

            int enteringCountBefore;
            lock (output)
            {
                enteringCountBefore = CountOccurrences(output.ToString(), "Resuming terminal");
            }

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("attach\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return CountOccurrences(output.ToString(), "Resuming terminal") > enteringCountBefore;
                }
            });
            await WaitForAsync(() => attachment.IsTerminalActive);

            attachment.IsTerminalActive.Should().BeTrue();
            session.ActivePrivilege.Should().Be(PrivilegeLevel.FromInt(1));

            // Transmit while at privilege 1.
            var payload = new byte[] { 0x41, 0x42, 0x00, 0xFF };
            await pipe.Client.WriteAsync(payload, cts.Token);
            await WaitForAsync(() => attachment.BytesToDevice >= payload.Length);

            // Receive while at privilege 1.
            var rx = new byte[] { 0x01, 0x02, 0x0D, 0x0A };
            attachments.TryEnqueueDeviceRx(registered.SessionId, rx)
                .Should().Be(DeviceRxEnqueueResult.Enqueued);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("\u0001\u0002\r\n", StringComparison.Ordinal) ||
                           attachment.BytesToOperator >= rx.Length;
                }
            });

            attachment.BytesToOperator.Should().BeGreaterThanOrEqualTo(rx.Length);

            // Admin commands remain denied at level 1.
            await pipe.Client.WriteAsync(new byte[] { TechConnectCliDispatcher.DetachControlByte }, cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains("still attached", StringComparison.Ordinal);
                }
            });
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show serial\r"), cts.Token);
            await WaitForAsync(() =>
            {
                lock (output)
                {
                    return output.ToString().Contains(
                        OperatorAuthorizationService.PermissionDeniedMessage,
                        StringComparison.Ordinal);
                }
            });

            var disableEvents = audit.GetRecent(50)
                .Where(e => e.EventType == AuditEventType.DisableToLevel1)
                .ToList();
            disableEvents.Should().ContainSingle();

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
    /// Asynchronously performs run interactive.
    /// </summary>
    /// <param name="identity">The identity.</param>
    /// <param name="enableSvc">The enable svc.</param>
    /// <param name="script">The script.</param>
    /// <returns>The operation result.</returns>

    private static async Task<(string Output, TechConnectCliSession Session)> RunInteractiveAsync(
        AuthenticatedOperator identity,
        TechConnect.Server.Authentication.EnablePrivilegeService enableSvc,
        Func<Stream, Func<string>, CancellationTokenSource, Task> script)
    {
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, _) =
            CliTestFactory.CreateDispatcher();
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
            enableSvc,
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
        string Snapshot()
        {
            lock (output)
            {
                return output.ToString();
            }
        }

        await script(pipe.Client, Snapshot, cts);
        await run.WaitAsync(TimeSpan.FromSeconds(12));
        cts.Cancel();
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        return (Snapshot(), session);
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
    /// <summary>
    /// Performs count occurrences.
    /// </summary>
    /// <param name="haystack">The haystack.</param>
    /// <param name="needle">The needle.</param>
    /// <returns>The operation result.</returns>

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
