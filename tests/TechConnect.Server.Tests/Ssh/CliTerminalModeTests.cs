// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliTerminalModeTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (CliTerminalModeTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authorization;
using TechConnect.Server.Connector;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for serial terminal mode and Ctrl+] detach behaviour.
/// </summary>
public sealed class CliTerminalModeTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Verifies: Help Includes Connect Attach And Disconnect Commands.
    /// </summary>
    [Fact]
    public void Help_IncludesConnectAttachAndDisconnectCommands()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var help = dispatcher.Execute("help", CliTestFactory.Operator());
        help.Output.Should().Contain("connect <session-id>");
        help.Output.Should().NotContain("set baud");
        help.Output.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Output.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        help.Output.Should().Contain("show serial");
        help.Output.Should().Contain("Ctrl+]");
    }

    /// <summary>
    /// Verifies: Connect Unknown Session Returns Error.
    /// </summary>
    [Fact]
    public void Connect_UnknownSession_ReturnsError()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var result = dispatcher.Execute("connect deadbeef", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("not found");
    }

    /// <summary>
    /// Verifies: Connect Known Session Returns Connect Action With Default Serial Settings.
    /// </summary>
    [Fact]
    public void Connect_KnownSession_ReturnsConnectActionWithDefaultSerialSettings()
    {
        var (dispatcher, registry, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var result = dispatcher.Execute($"connect {session.SessionId}", CliTestFactory.Operator());
        result.Action.Should().Be(CliCommandAction.Connect);
        result.ConnectSessionId.Should().Be(session.SessionId);
        result.SerialSettings.Should().Be(ConnectSerialSettings.Defaults);
    }

    /// <summary>
    /// Verifies: Connect Incomplete Or Invalid Syntax Is Rejected.
    /// </summary>
    [Fact]
    public void Connect_IncompleteOrInvalidSyntax_IsRejected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        dispatcher.Execute("connect", CliTestFactory.Operator()).Output.Should().Be("% Incomplete command");
        dispatcher.Execute("connect a b", CliTestFactory.Operator()).Output.Should().Contain("Incomplete serial parameter");
    }

    /// <summary>
    /// Verifies: Detach Control Byte Is Gs And Splits Buffer Correctly.
    /// </summary>
    [Fact]
    public void DetachControlByte_IsGs_AndSplitsBufferCorrectly()
    {
        TechConnectCliDispatcher.DetachControlByte.Should().Be(0x1D);

        var input = new byte[] { 0x00, (byte)'A', 0x0D, 0x0A, 0x1D, (byte)'Z', 0xFF };
        var detachIndex = input.AsSpan().IndexOf(TechConnectCliDispatcher.DetachControlByte);
        detachIndex.Should().Be(4);
        input.AsSpan(0, detachIndex).ToArray().Should().Equal(0x00, (byte)'A', 0x0D, 0x0A);
        input.AsSpan(detachIndex + 1).ToArray().Should().Equal((byte)'Z', 0xFF);
    }

    /// <summary>
    /// Verifies: Cli Session Unknown Connect Then Exit Restores Prompt.
    /// </summary>
    [Fact]
    public async Task CliSession_UnknownConnect_ThenExit_RestoresPrompt()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var clientTcp = new System.Net.Sockets.TcpClient();
        var clientConnect = clientTcp.ConnectAsync(System.Net.IPAddress.Loopback, port);
        using var serverTcp = await listener.AcceptTcpClientAsync();
        await clientConnect;
        await using var serverStream = serverTcp.GetStream();
        await using var clientStream = clientTcp.GetStream();

        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = new TechConnectCliSession(
            serverStream,
            dispatcher,
            attachments,
            registry,
            tracker,
            serialCache,
            pendingBaud,
            authorization,
            disconnect,
            NullLogger.Instance,
            TestAuthenticatedOperator.Create("test-op"),
            enable);

        var outputBuffer = new MemoryStream();
        var readOutput = Task.Run(async () =>
        {
            var buf = new byte[1024];
            while (!cts.IsCancellationRequested)
            {
                int n;
                try
                {
                    n = await clientStream.ReadAsync(buf, cts.Token);
                }
                catch
                {
                    break;
                }

                if (n == 0)
                {
                    break;
                }

                lock (outputBuffer)
                {
                    outputBuffer.Write(buf, 0, n);
                }
            }
        }, CancellationToken.None);

        var run = session.RunAsync(cts.Token);
        await WaitForOutputAsync(outputBuffer, "TechConnect#", TimeSpan.FromSeconds(3));
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes("connect deadbeef\r\n"), cts.Token);
        await WaitForOutputAsync(outputBuffer, "not found", TimeSpan.FromSeconds(3));
        await clientStream.WriteAsync(Encoding.UTF8.GetBytes("exit\r\n"), cts.Token);

        await run.WaitAsync(TimeSpan.FromSeconds(8));
        await WaitForOutputAsync(outputBuffer, "Goodbye", TimeSpan.FromSeconds(3));
        cts.Cancel();
        try
        {
            await readOutput.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        string output;
        lock (outputBuffer)
        {
            output = Encoding.UTF8.GetString(outputBuffer.ToArray());
        }

        output.Should().Contain("Connected to NMS TechConnect Server");
        output.Should().Contain("not found");
        output.Should().Contain("TechConnect#");
        output.Should().Contain("Goodbye");
        attachments.Count.Should().Be(0);
    }
    /// <summary>
    /// Asynchronously performs wait for output.
    /// </summary>
    /// <param name="outputBuffer">The output buffer.</param>
    /// <param name="needle">The needle.</param>
    /// <param name="timeout">The timeout.</param>

    private static async Task WaitForOutputAsync(MemoryStream outputBuffer, string needle, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            string snapshot;
            lock (outputBuffer)
            {
                snapshot = Encoding.UTF8.GetString(outputBuffer.ToArray());
            }

            if (snapshot.Contains(needle, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        string final;
        lock (outputBuffer)
        {
            final = Encoding.UTF8.GetString(outputBuffer.ToArray());
        }

        throw new TimeoutException($"Timed out waiting for '{needle}'. Output was:{Environment.NewLine}{final}");
    }
}
