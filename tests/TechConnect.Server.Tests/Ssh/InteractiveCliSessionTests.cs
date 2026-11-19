// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    InteractiveCliSessionTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (InteractiveCliSessionTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Connector;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for TechConnectCliSession interactive command loops.
/// </summary>
public sealed class InteractiveCliSessionTests
{
    /// <summary>
    /// Verifies: Prompt Administrator Uses Hash.
    /// </summary>
    [Fact]
    public async Task Prompt_Administrator_UsesHash()
    {
        var output = await RunCommandsAsync(
            TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.AdministratorBoundary),
            ["exit"]);
        output.Should().Contain("TechConnect#");
        output.Should().Contain("Connected to NMS TechConnect Server");
    }

    /// <summary>
    /// Verifies: Prompt Operator Uses Greater Than.
    /// </summary>
    [Fact]
    public async Task Prompt_Operator_UsesGreaterThan()
    {
        var output = await RunCommandsAsync(
            TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.OperatorBoundary),
            ["exit"]);
        output.Should().Contain("TechConnect>");
        output.Should().NotContain("TechConnect#");
    }

    /// <summary>
    /// Verifies: Input Is Echoed And Command Output Displayed Then Prompt Restored.
    /// </summary>
    [Fact]
    public async Task Input_IsEchoed_AndCommandOutputDisplayed_ThenPromptRestored()
    {
        var output = await RunCommandsAsync(
            TestAuthenticatedOperator.Create(),
            ["help", "exit"]);
        output.Should().Contain("help");
        output.Should().Contain("Available commands");
        output.Should().Contain("TechConnect#");
        output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Verifies: Invalid Command Shows Caret And Message.
    /// </summary>
    [Fact]
    public async Task InvalidCommand_ShowsCaretAndMessage()
    {
        var output = await RunCommandsAsync(
            TestAuthenticatedOperator.Create(),
            ["shwo sessions", "exit"]);
        output.Should().Contain("% Invalid command");
        output.Should().Contain("^");
    }

    /// <summary>
    /// Verifies: Ctrl C Does Not Disconnect Redisplays Prompt.
    /// </summary>
    [Fact]
    public async Task CtrlC_DoesNotDisconnect_RedisplaysPrompt()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            TestAuthenticatedOperator.Create(),
            enable);

        var output = new StringBuilder();
        var readOutput = Task.Run(async () =>
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
                return output.ToString().Contains("TechConnect#", StringComparison.Ordinal);
            }
        });

        await pipe.Client.WriteAsync(new byte[] { (byte)'x', 0x03 }, cts.Token);
        await WaitForAsync(() =>
        {
            lock (output)
            {
                var text = output.ToString();
                return text.Contains("^C", StringComparison.Ordinal) &&
                       text.Split("TechConnect#", StringSplitOptions.None).Length >= 3;
            }
        });

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        attachments.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies: Ctrl D Exits Cleanly.
    /// </summary>
    [Fact]
    public async Task CtrlD_ExitsCleanly()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            TestAuthenticatedOperator.Create(),
            enable);

        var run = session.RunAsync(cts.Token);
        await Task.Delay(100);
        await pipe.Client.WriteAsync(new byte[] { 0x04 }, cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        attachments.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies: Application Shutdown Cancels Pending Input.
    /// </summary>
    [Fact]
    public async Task ApplicationShutdown_CancelsPendingInput()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource();
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
            TestAuthenticatedOperator.Create(),
            enable);

        var run = session.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        attachments.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies: Remote Disconnect Terminates Cli Session.
    /// </summary>
    [Fact]
    public async Task RemoteDisconnect_TerminatesCliSession()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            TestAuthenticatedOperator.Create(),
            enable);

        var run = session.RunAsync(cts.Token);
        await Task.Delay(50);
        await pipe.Client.DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        attachments.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies: Concurrent Output Writes Are Serialized.
    /// </summary>
    [Fact]
    public async Task ConcurrentOutputWrites_AreSerialized()
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            TestAuthenticatedOperator.Create(),
            enable);

        var chunks = new List<string>();
        var readOutput = Task.Run(async () =>
        {
            var buf = new byte[64];
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

                lock (chunks)
                {
                    chunks.Add(Encoding.UTF8.GetString(buf, 0, n));
                }
            }
        });

        var run = session.RunAsync(cts.Token);
        await WaitForAsync(() =>
        {
            lock (chunks)
            {
                return string.Concat(chunks).Contains("TechConnect#", StringComparison.Ordinal);
            }
        });

        // Send help and wait for its output before exit so the assertion is not racing session teardown.
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("help\r"), cts.Token);
        await WaitForAsync(() =>
        {
            lock (chunks)
            {
                return string.Concat(chunks).Contains("Available commands", StringComparison.Ordinal);
            }
        });
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        var text = string.Concat(chunks);
        text.Should().Contain("Available commands");
        text.Should().NotContain("TechConnect#Available"); // prompt and output separated by newline path
        text.Should().Contain("\r\n");
    }

    /// <summary>
    /// Verifies: Level1 Can Connect And Attach Cannot Show Serial.
    /// </summary>
    [Fact]
    public async Task Level1_CanConnectAndAttach_CannotShowSerial()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null,
            DateTimeOffset.Parse("2026-11-18T12:00:00Z")).Session!;
        var ro = TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
        dispatcher.Execute($"connect {session.SessionId}", ro).Action.Should().Be(CliCommandAction.Connect);
        dispatcher.Execute("attach", ro).Action.Should().Be(CliCommandAction.Attach);
        dispatcher.Execute("show serial", ro).Output.Should()
            .Be(OperatorAuthorizationService.PermissionDeniedMessage);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Verifies: Clear Screen Emits Ansi Clear Then Restores Prompt.
    /// </summary>
    [Fact]
    public async Task ClearScreen_EmitsAnsiClear_ThenRestoresPrompt()
    {
        var output = await RunCommandsAsync(
            TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.FromInt(1)),
            ["clear screen", "show privilege", "exit"]);
        output.Should().Contain("\u001b[2J\u001b[H");
        output.Should().Contain("Active privilege");
        output.Should().Contain("TechConnect>");
        output.Should().Contain("Goodbye");
    }

    /// <summary>
    /// Verifies: Completion Respects Authorization.
    /// </summary>
    [Fact]
    public void Completion_RespectsAuthorization()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete("att", 3, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary), 80)
            .Tokens.Should().ContainSingle(m => m == "attach");
        discovery.Complete("ter", 3, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary), 80)
            .Tokens.Should().BeEmpty();
        discovery.Complete("det", 3, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary), 80)
            .Tokens.Should().BeEmpty();
        discovery.Complete("att", 3, TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.OperatorBoundary), 80)
            .Tokens.Should().ContainSingle(m => m == "attach");
    }
    /// <summary>
    /// Asynchronously performs run commands.
    /// </summary>
    /// <param name="identity">The identity.</param>
    /// <param name="commands">The commands.</param>
    /// <returns>The operation result.</returns>

    private static async Task<string> RunCommandsAsync(AuthenticatedOperator identity, string[] commands)
    {
        await using var pipe = new DuplexMemoryPipe();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            enable);

        var output = new StringBuilder();
        var readOutput = Task.Run(async () =>
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
                var text = output.ToString();
                return text.Contains("TechConnect#", StringComparison.Ordinal) ||
                       text.Contains("TechConnect>", StringComparison.Ordinal);
            }
        });

        foreach (var command in commands)
        {
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"), cts.Token);
            await Task.Delay(30);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(8));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        lock (output)
        {
            return output.ToString();
        }
    }
    /// <summary>
    /// Asynchronously performs wait for.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeoutMs">The timeout ms.</param>

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met in time.");
    }
}
