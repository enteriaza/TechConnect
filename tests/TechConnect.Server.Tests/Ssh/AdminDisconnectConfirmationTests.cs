// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AdminDisconnectConfirmationTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (AdminDisconnectConfirmationTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for interactive admin disconnect confirmation prompts.
/// </summary>
public sealed class AdminDisconnectConfirmationTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T17:00:00Z");

    /// <summary>
    /// Verifies: Decline Or Empty Cancels Without Teardown.
    /// </summary>
    [Theory]
    [InlineData("\r")]
    [InlineData("n\r")]
    [InlineData("nope\r")]
    public async Task DeclineOrEmpty_Cancels_WithoutTeardown(string confirmInput)
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("CERT", "c1", null, "host", null, "COM1", null, T0).Session!;
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cli = new TechConnectCliSession(
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
            admin,
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

        var run = cli.RunAsync(cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("TechConnect#", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(
            Encoding.UTF8.GetBytes($"disconnect {session.SessionId}\r"),
            cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("Confirm [y/N]:", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(confirmInput), cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("Disconnect cancelled.", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        registry.TryGetBySessionId(session.SessionId, out _).Should().BeTrue();
        audit.GetRecent(20).Should().Contain(e =>
            e.EventType == AuditEventType.AdministrativeDisconnectDeclined);
        audit.GetRecent(20).Should().NotContain(e =>
            e.EventType == AuditEventType.AdministrativeDisconnectInitiated);
    }

    /// <summary>
    /// Verifies: Ctrl C During Confirm Cancels.
    /// </summary>
    [Fact]
    public async Task CtrlC_DuringConfirm_Cancels()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("CERT", "c1", null, "host", null, "COM1", null, T0).Session!;
        var admin = TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var cli = new TechConnectCliSession(
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
            admin,
            enable,
            audit: audit);

        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            var buf = new byte[512];
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

        var run = cli.RunAsync(cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("TechConnect#", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(
            Encoding.UTF8.GetBytes($"disconnect {session.SessionId}\r"),
            cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("Confirm [y/N]:", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(new byte[] { 0x03 }, cts.Token); // Ctrl+C
        await WaitForAsync(() =>
        {
            lock (output)
            {
                return output.ToString().Contains("Disconnect cancelled.", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        audit.GetRecent(20).Should().Contain(e =>
            e.EventType == AuditEventType.AdministrativeDisconnectCancelled);
        registry.TryGetBySessionId(session.SessionId, out _).Should().BeTrue();
    }
    /// <summary>
    /// Asynchronously performs wait for.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeoutMs">The timeout ms.</param>

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (Environment.TickCount64 - start < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met.");
    }
}
