// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshRemoteEndpointTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (SshRemoteEndpointTests).
//
// -----------------------------------------------------------------------------
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Inspection;
using TechConnect.Server.Ssh;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Ssh.Sessions;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for SSH remote endpoint capture and formatting.
/// </summary>
public sealed class SshRemoteEndpointTests
{
    /// <summary>
    /// Verifies: Format Endpoint Ipv4And Ipv6.
    /// </summary>
    [Fact]
    public void FormatEndpoint_Ipv4AndIpv6()
    {
        TechConnectSshServer.FormatEndpoint(new IPEndPoint(IPAddress.Parse("10.0.0.5"), 51234))
            .Should().Be("10.0.0.5:51234");
        TechConnectSshServer.FormatEndpoint(new IPEndPoint(IPAddress.Parse("2001:db8::1"), 22))
            .Should().Be("[2001:db8::1]:22");
    }

    /// <summary>
    /// Verifies: Try Format Remote Endpoint Non Network Stream Returns Null.
    /// </summary>
    [Fact]
    public void TryFormatRemoteEndpoint_NonNetworkStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        TechConnectSshServer.TryFormatRemoteEndpoint(ms).Should().BeNull();
        TechConnectSshServer.TryFormatRemoteEndpoint(null).Should().BeNull();
    }

    /// <summary>
    /// Verifies: Tracker Stores Independent Endpoints And Removes On Disconnect.
    /// </summary>
    [Fact]
    public void Tracker_StoresIndependentEndpoints_AndRemovesOnDisconnect()
    {
        var tracker = new SshOperatorSessionTracker();
        // SshServerSession requires config; use lightweight fake via CLI records for show users,
        // and verify tracker remote map with TryAdd/Remove using distinct session objects is covered
        // through CLI registration path below.
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-a",
            Username = "alice",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = "10.0.0.5:1000",
            ConnectedAtUtc = DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            Columns = 80,
            Rows = 24
        });
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-b",
            Username = "bob",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = "10.0.0.6:2000",
            ConnectedAtUtc = DateTimeOffset.Parse("2026-11-18T12:01:00Z"),
            Columns = 80,
            Rows = 24
        });

        var listed = tracker.ListCliSessions();
        listed.Should().HaveCount(2);
        listed[0].RemoteEndpoint.Should().Be("10.0.0.5:1000");
        listed[1].RemoteEndpoint.Should().Be("10.0.0.6:2000");

        tracker.UnregisterCliSession("op-a");
        tracker.ListCliSessions().Should().ContainSingle(u => u.OperatorSessionId == "op-b");

        // Reconnect with a different endpoint
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-a",
            Username = "alice",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = "10.0.0.9:3000",
            ConnectedAtUtc = DateTimeOffset.Parse("2026-11-18T12:05:00Z"),
            Columns = 80,
            Rows = 24
        });
        tracker.ListCliSessions().Single(u => u.OperatorSessionId == "op-a")
            .RemoteEndpoint.Should().Be("10.0.0.9:3000");
    }

    /// <summary>
    /// Verifies: Show Users Formats Endpoints And Missing Fallback.
    /// </summary>
    [Fact]
    public void ShowUsers_FormatsEndpoints_AndMissingFallback()
    {
        var tracker = new SshOperatorSessionTracker();
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-1",
            Username = "noc",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = "198.51.100.10:54321",
            ConnectedAtUtc = DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            Columns = 80,
            Rows = 24
        });
        tracker.RegisterCliSession(new OperatorCliSessionRecord
        {
            OperatorSessionId = "op-2",
            Username = "ops",
            LoginPrivilege = PrivilegeLevel.FromInt(1),
            ActivePrivilege = PrivilegeLevel.FromInt(1),
            RemoteEndpoint = null,
            ConnectedAtUtc = DateTimeOffset.Parse("2026-11-18T12:01:00Z"),
            Columns = 80,
            Rows = 24
        });

        var (dispatcher, _, _, _, _, _, _, _, _, _) =
            CliTestFactory.CreateDispatcher(operatorSessions: tracker);
        var output = dispatcher.Execute(
                "show users",
                TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1)))
            .Output!;
        output.Should().Contain("198.51.100.10:54321");
        output.Should().Contain("ops");
        output.Should().Contain("op-2");
        // Missing endpoint falls back to '-' in the REMOTE column for ops.
        var opsLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.StartsWith("ops", StringComparison.Ordinal));
        opsLine.Should().Contain("-");
    }

    /// <summary>
    /// Verifies: Interactive Session Registers Remote Endpoint For Show Users.
    /// </summary>
    [Fact]
    public async Task InteractiveSession_RegistersRemoteEndpointForShowUsers()
    {
        await using var pipe = new DuplexMemoryPipe();
        var tracker = new SshOperatorSessionTracker();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, connTracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher(operatorSessions: tracker);
        var identity = TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-ep");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = new TechConnectCliSession(
            pipe.Server,
            dispatcher,
            attachments,
            registry,
            connTracker,
            serialCache,
            pendingBaud,
            authorization,
            disconnect,
            NullLogger.Instance,
            identity,
            enable,
            operatorSessions: tracker,
            remoteEndpoint: "203.0.113.50:4000");

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
                }            }
        });

        var run = session.RunAsync(cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("TechConnect>", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show users\rexit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // EOF (not cancellation) ends the reader, so output the session already wrote is fully drained.
        await pipe.DisposeAsync();
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        text.Should().Contain("203.0.113.50:4000");
        tracker.ListCliSessions().Should().BeEmpty(); // unregistered after exit
    }
    /// <summary>
    /// Asynchronously performs wait for.
    /// </summary>
    /// <param name="condition">The condition.</param>

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var start = DateTime.UtcNow;
        while (!condition())
        {
            if (DateTime.UtcNow - start > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException();
            }

            await Task.Delay(20);
        }
    }
}
