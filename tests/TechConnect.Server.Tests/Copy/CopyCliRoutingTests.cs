// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyCliRoutingTests.cs
//
// Purpose:
// CLI-level routing for copy: discovery, parse/dispatch, interactive prompts, and
// guards against leaking connect serial-configuration context into copy.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Copy;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// CLI-level routing for <c>copy</c>: discovery, parse/dispatch, and interactive prompts.
/// Guards against leaking connect serial-configuration context into copy.
/// </summary>
public sealed class CopyCliRoutingTests
{
    /// <summary>Fixed timestamp for session registration fixtures.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Creates an administrator-boundary authenticated operator fixture.
    /// </summary>
    private static AuthenticatedOperator Admin(string opSessionId = "op-copy") =>
        TestAuthenticatedOperator.Create("admin", PrivilegeLevel.AdministratorBoundary, operatorSessionId: opSessionId);

    /// <summary>
    /// Creates a privilege-1 NOC authenticated operator fixture.
    /// </summary>
    private static AuthenticatedOperator Noc(string opSessionId = "op-copy") =>
        TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: opSessionId);

    /// <summary>
    /// Verifies: Bare Copy Shows Copy Usage Not Incomplete Generic.
    /// </summary>
    [Fact]
    public void BareCopy_ShowsCopyUsage_NotIncompleteGeneric()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var result = dispatcher.Execute("copy", Noc());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("Usage: copy");
        result.Output.Should().Contain("serial:");
        result.Output.Should().NotContain("baud");
        result.Action.Should().NotBe(CliCommandAction.Connect);
    }

    /// <summary>
    /// Verifies: Scheme Only With Destination Dispatches Copy Action.
    /// </summary>
    [Theory]
    [InlineData("http:", (int)CopySourceKind.Http)]
    [InlineData("https:", (int)CopySourceKind.Https)]
    [InlineData("tftp:", (int)CopySourceKind.Tftp)]
    [InlineData("scp:", (int)CopySourceKind.Scp)]
    public void SchemeOnly_WithDestination_DispatchesCopyAction(string scheme, int kindValue)
    {
        var kind = (CopySourceKind)kindValue;
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = Noc();
        attachments.TryAttach(op.OperatorSessionId, session.SessionId, "c1", op).Succeeded.Should().BeTrue();

        var result = dispatcher.Execute($"copy {scheme} serial:{session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.Copy);
        result.CopyRequest.Should().NotBeNull();
        result.CopyRequest!.Source.Kind.Should().Be(kind);
        result.CopyRequest.Source.Host.Should().BeNull();
        result.CopyRequest.SessionId.Should().Be(session.SessionId);
    }

    /// <summary>
    /// Verifies: Modem Scheme With Destination Dispatches Copy Action.
    /// </summary>
    [Theory]
    [InlineData("xmodem:", (int)CopySourceKind.Xmodem)]
    [InlineData("ymodem:", (int)CopySourceKind.Ymodem)]
    public void ModemScheme_WithDestination_DispatchesCopyAction(string scheme, int kindValue)
    {
        var kind = (CopySourceKind)kindValue;
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = Noc();
        attachments.TryAttach(op.OperatorSessionId, session.SessionId, "c1", op).Succeeded.Should().BeTrue();

        var result = dispatcher.Execute($"copy {scheme} serial:{session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.Copy);
        result.CopyRequest!.Source.Kind.Should().Be(kind);
        result.CopyRequest.Source.IsModem.Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Complete Url Bypasses Interactive Stub.
    /// </summary>
    [Theory]
    [InlineData("http://192.0.2.10/firmware.bin", (int)CopySourceKind.Http, "192.0.2.10", "/firmware.bin")]
    [InlineData("https://192.0.2.10/firmware.bin", (int)CopySourceKind.Https, "192.0.2.10", "/firmware.bin")]
    public void CompleteUrl_BypassesInteractiveStub(
        string url,
        int kindValue,
        string host,
        string path)
    {
        var kind = (CopySourceKind)kindValue;
        var (dispatcher, registry, attachments, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var op = Noc();
        attachments.TryAttach(op.OperatorSessionId, session.SessionId, "c1", op).Succeeded.Should().BeTrue();

        var result = dispatcher.Execute($"copy {url} serial:{session.SessionId}", op);
        result.Action.Should().Be(CliCommandAction.Copy);
        result.CopyRequest!.Source.Kind.Should().Be(kind);
        result.CopyRequest.Source.Host.Should().Be(host);
        result.CopyRequest.Source.Path.Should().Be(path);
    }

    /// <summary>
    /// Verifies: Missing Destination Returns Usage Not Connect.
    /// </summary>
    [Fact]
    public void MissingDestination_ReturnsUsage_NotConnect()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var result = dispatcher.Execute("copy http:", Noc());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("Usage: copy");
        result.Output.Should().NotContain("baud");
        result.CopyRequest.Should().BeNull();
    }

    /// <summary>
    /// Verifies: Invalid Destination Rejected.
    /// </summary>
    [Fact]
    public void InvalidDestination_Rejected()
    {
        var (dispatcher, _, _, _, _, _, _, _, _, _) = CliTestFactory.CreateDispatcher();
        var result = dispatcher.Execute("copy http://h/f.bin flash:foo", Noc());
        result.Action.Should().Be(CliCommandAction.Continue);
        result.Output.Should().Contain("serial:");
        result.Output.Should().NotContain("baud");
    }

    /// <summary>
    /// Verifies: Parser Copy Never Resolves To Connect.
    /// </summary>
    [Fact]
    public void Parser_CopyNeverResolvesToConnect()
    {
        foreach (var input in new[]
                 {
                     "copy",
                     "copy http:",
                     "copy https:",
                     "copy tftp:",
                     "copy scp:",
                     "copy xmodem:",
                     "copy ymodem:",
                     "copy http: serial:abcd",
                     "copy https://example.com/firmware.bin serial:abcd"
                 })
        {
            var parsed = CliCommandParser.Parse(input);
            if (parsed.Status == CliParseStatus.Resolved)
            {
                parsed.CommandId.Should().Be(CliCommandId.Copy, because: input);
                parsed.CommandId.Should().NotBe(CliCommandId.Connect, because: input);
            }
            else if (parsed.Status == CliParseStatus.Incomplete)
            {
                parsed.Node!.CommandId.Should().Be(CliCommandId.Copy, because: input);
            }
        }
    }

    /// <summary>
    /// Verifies: Help After Copy Source Shows Destination Not Serial Config.
    /// </summary>
    [Fact]
    public void Help_AfterCopySource_ShowsDestination_NotSerialConfig()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);

        foreach (var scheme in new[] { "http:", "https:", "tftp:", "scp:", "xmodem:", "ymodem:" })
        {
            var line = $"copy {scheme} ";
            var help = discovery.Help(line, line.Length, Admin(), 80);
            help.Kind.Should().Be(CliDiscoveryKind.ContextHelp, because: line);
            help.DisplayText.Should().Contain("serial:", because: line);
            help.DisplayText.Should().NotContain("baud", because: line);
            help.DisplayText.Should().NotContain("databits", because: line);
            help.DisplayText.Should().NotContain("parity", because: line);
            help.DisplayText.Should().NotContain("stopbits", because: line);
            help.Tokens.Should().NotContain("baud");
        }

        var complete = discovery.Complete($"copy http: ", $"copy http: ".Length, Admin(), 80);
        complete.Tokens.Should().Contain($"serial:{session.SessionId}");
        complete.Tokens.Should().NotContain("baud");
        complete.Tokens.Should().NotContain("databits");
        complete.Tokens.Should().NotContain("parity");
        complete.Tokens.Should().NotContain("stopbits");
    }

    /// <summary>
    /// Verifies: Help Bare Copy Shows Source Not Serial Config.
    /// </summary>
    [Fact]
    public void Help_BareCopy_ShowsSource_NotSerialConfig()
    {
        var discovery = new CliCommandDiscovery();
        var help = discovery.Help("copy ", 5, Admin(), 80);
        help.Kind.Should().Be(CliDiscoveryKind.ContextHelp);
        help.DisplayText.Should().Contain("<source>");
        help.DisplayText.Should().NotContain("baud");
        help.Tokens.Should().NotContain("baud");
    }

    /// <summary>
    /// Verifies: Complete Copy Source Lists Schemes Only.
    /// </summary>
    [Fact]
    public void Complete_CopySource_ListsSchemesOnly()
    {
        var discovery = new CliCommandDiscovery();
        var result = discovery.Complete("copy ", 5, Admin(), 80);
        result.Tokens.Should().Contain("http:");
        result.Tokens.Should().Contain("https:");
        result.Tokens.Should().Contain("tftp:");
        result.Tokens.Should().Contain("scp:");
        result.Tokens.Should().Contain("xmodem:");
        result.Tokens.Should().Contain("ymodem:");
        result.Tokens.Should().NotContain("baud");
        result.Tokens.Should().NotContain("connect");
    }

    /// <summary>
    /// Verifies: Connect Serial Configuration Prompting Unchanged.
    /// </summary>
    [Fact]
    public void Connect_SerialConfigurationPrompting_Unchanged()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var discovery = new CliCommandDiscovery(registry);
        var line = $"connect {session.SessionId} ";
        var help = discovery.Help(line, line.Length, Admin(), 80);
        help.Tokens.Should().Equal("baud", "databits", "parity", "stopbits");
        help.DisplayText.Should().Contain("Serial baud rate");

        var complete = discovery.Complete(line, line.Length, Admin(), 80);
        complete.Tokens.Should().Equal("baud", "databits", "parity", "stopbits");
    }

    /// <summary>
    /// Verifies: Interactive Http Scheme Only Prompts For Host And Filename.
    /// </summary>
    [Fact]
    public async Task Interactive_HttpSchemeOnly_PromptsForHostAndFilename()
    {
        var captured = await RunCopyInteractiveAsync(
            "copy http: serial:{session}",
            [
                ("Address or name of remote host", "192.0.2.10"),
                ("Source filename", "/firmware/device.bin"),
                ("% CAPTURED:", null)
            ]);

        captured.Output.Should().Contain("Address or name of remote host");
        captured.Output.Should().Contain("Source filename");
        captured.Output.Should().NotContain("baud");
        captured.Output.Should().NotContain("Serial baud rate");
        captured.Factory.LastSource.Should().NotBeNull();
        captured.Factory.LastSource!.Kind.Should().Be(CopySourceKind.Http);
        captured.Factory.LastSource.Host.Should().Be("192.0.2.10");
        captured.Factory.LastSource.Path.Should().Be("/firmware/device.bin");
        captured.Output.Should().Contain("TechConnect#");
    }

    /// <summary>
    /// Verifies: Interactive Scheme Only Prompts For Host.
    /// </summary>
    [Theory]
    [InlineData("https:", (int)CopySourceKind.Https)]
    [InlineData("tftp:", (int)CopySourceKind.Tftp)]
    public async Task Interactive_SchemeOnly_PromptsForHost(string scheme, int kindValue)
    {
        var kind = (CopySourceKind)kindValue;
        var captured = await RunCopyInteractiveAsync(
            $"copy {scheme} serial:{{session}}",
            [
                ("Address or name of remote host", "192.0.2.20"),
                ("Source filename", "/a.bin"),
                ("% CAPTURED:", null)
            ]);

        captured.Factory.LastSource.Should().NotBeNull();
        captured.Factory.LastSource!.Kind.Should().Be(kind);
        captured.Factory.LastSource.Host.Should().Be("192.0.2.20");
        captured.Output.Should().NotContain("baud");
    }

    /// <summary>
    /// Verifies: Interactive Scp Scheme Only Prompts Host File And Username.
    /// </summary>
    [Fact]
    public async Task Interactive_ScpSchemeOnly_PromptsHostFileAndUsername()
    {
        var captured = await RunCopyInteractiveAsync(
            "copy scp: serial:{session}",
            [
                ("Address or name of remote host", "192.0.2.30"),
                ("Source filename", "path/fw.bin"),
                ("Username", "admin"),
                ("% CAPTURED:", null)
            ]);

        captured.Output.Should().Contain("Username");
        captured.Factory.LastSource.Should().NotBeNull();
        captured.Factory.LastSource!.Kind.Should().Be(CopySourceKind.Scp);
        captured.Factory.LastSource.Host.Should().Be("192.0.2.30");
        captured.Factory.LastSource.Username.Should().Be("admin");
        captured.Output.Should().NotContain("baud");
    }

    /// <summary>
    /// Verifies: Interactive Complete Https Url Skips Host Filename Prompts.
    /// </summary>
    [Fact]
    public async Task Interactive_CompleteHttpsUrl_SkipsHostFilenamePrompts()
    {
        var captured = await RunCopyInteractiveAsync(
            "copy https://example.com/firmware.bin serial:{session}",
            [
                ("Source: https://example.com/firmware.bin", null),
                ("% CAPTURED:", null)
            ]);

        captured.Output.Should().NotContain("Address or name of remote host");
        captured.Output.Should().NotContain("Source filename");
        captured.Factory.LastSource.Should().NotBeNull();
        captured.Factory.LastSource!.Host.Should().Be("example.com");
        captured.Factory.LastSource.Path.Should().Be("/firmware.bin");
        captured.Output.Should().NotContain("baud");
    }

    /// <summary>
    /// Verifies: Interactive Complete Http Url Skips Host Filename Prompts.
    /// </summary>
    [Fact]
    public async Task Interactive_CompleteHttpUrl_SkipsHostFilenamePrompts()
    {
        var captured = await RunCopyInteractiveAsync(
            "copy http://example.com/a.bin serial:{session}",
            [
                ("Source: http://example.com/a.bin", null),
                ("% CAPTURED:", null)
            ]);

        captured.Output.Should().NotContain("Address or name of remote host");
        captured.Factory.LastSource!.Kind.Should().Be(CopySourceKind.Http);
    }

    /// <summary>
    /// Verifies: Interactive Modem Enters Receive Workflow Not Serial Config.
    /// </summary>
    [Theory]
    [InlineData("xmodem:", "XMODEM")]
    [InlineData("ymodem:", "YMODEM")]
    public async Task Interactive_Modem_EntersReceiveWorkflow_NotSerialConfig(string scheme, string label)
    {
        var identity = Admin();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-modem", null, "host", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();
            attachments.TryAttach(identity.OperatorSessionId, registered.SessionId, registered.ConnectionId, identity)
                .Succeeded.Should().BeTrue();

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
                audit: audit,
                copySources: new CapturingCopySourceFactory());

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
            await WaitForAsync(() => Snapshot(output).Contains("TechConnect#", StringComparison.Ordinal));

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"copy {scheme} serial:{registered.SessionId}\r"),
                cts.Token);
            await WaitForAsync(() => Snapshot(output).Contains($"Begin {label} upload", StringComparison.Ordinal));

            var text = Snapshot(output);
            text.Should().NotContain("baud");
            text.Should().NotContain("Serial baud rate");
            text.Should().Contain("SSH terminal");
            text.Should().Contain("compatible terminal client");

            // Tear down while modem receive is waiting — proves copy owned the workflow, not connect.
            cts.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }

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
    /// Verifies: Cancelled Copy Returns To Normal Prompt.
    /// </summary>
    [Fact]
    public async Task CancelledCopy_ReturnsToNormalPrompt()
    {
        var identity = Admin();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-cancel", null, "host", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();
            attachments.TryAttach(identity.OperatorSessionId, registered.SessionId, registered.ConnectionId, identity)
                .Succeeded.Should().BeTrue();

            await using var pipe = new DuplexMemoryPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var factory = new CapturingCopySourceFactory();
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
                audit: audit,
                copySources: factory);

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
            await WaitForAsync(() => Snapshot(output).Contains("TechConnect#", StringComparison.Ordinal));

            await pipe.Client.WriteAsync(
                Encoding.UTF8.GetBytes($"copy http: serial:{registered.SessionId}\r"),
                cts.Token);
            await WaitForAsync(() =>
                Snapshot(output).Contains("Address or name of remote host", StringComparison.Ordinal));

            await pipe.Client.WriteAsync(new byte[] { 0x03 }, cts.Token);
            await WaitForAsync(() =>
            {
                var text = Snapshot(output);
                return text.Contains("Copy cancelled", StringComparison.Ordinal) ||
                       (text.Contains("TechConnect#", StringComparison.Ordinal) &&
                        text.LastIndexOf("TechConnect#", StringComparison.Ordinal) >
                        text.IndexOf("Address or name", StringComparison.Ordinal));
            });

            factory.LastSource.Should().BeNull();
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
        }
    }

    /// <summary>
    /// Runs an interactive copy CLI session against a capturing source factory.
    /// </summary>
    /// <param name="commandTemplate">
    /// Copy command text sent to the CLI (may include placeholders resolved by the test).
    /// </param>
    /// <param name="steps">
    /// Each step waits for <c>WaitFor</c> in the output, then optionally sends <c>Reply</c>.
    /// </param>
    /// <returns>Captured terminal output and the capturing source factory used by the session.</returns>
    private static async Task<(string Output, CapturingCopySourceFactory Factory)> RunCopyInteractiveAsync(
        string commandTemplate,
        (string WaitFor, string? Reply)[] steps)
    {
        var identity = Admin();
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();
        var registered = registry.TryRegister("ID", "c-copy", null, "host", null, "COM1", null, T0).Session!;
        var (connection, peer, serverCert, ca) =
            await TestConnectorConnectionFactory.CreateAuthenticatedAsync(registered.ConnectionId);
        await using (connection)
        using (peer)
        using (serverCert)
        using (ca)
        {
            tracker.TryAdd(connection).Should().BeTrue();
            attachments.TryAttach(identity.OperatorSessionId, registered.SessionId, registered.ConnectionId, identity)
                .Succeeded.Should().BeTrue();

            var factory = new CapturingCopySourceFactory();
            await using var pipe = new DuplexMemoryPipe();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
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
                audit: audit,
                copySources: factory);

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
            await WaitForAsync(() => Snapshot(output).Contains("TechConnect#", StringComparison.Ordinal));

            var command = commandTemplate.Replace("{session}", registered.SessionId, StringComparison.Ordinal);
            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"), cts.Token);

            foreach (var (waitFor, reply) in steps)
            {
                await WaitForAsync(() => Snapshot(output).Contains(waitFor, StringComparison.Ordinal));
                if (reply is not null)
                {
                    await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes(reply + "\r"), cts.Token);
                }
            }

            await WaitForAsync(() =>
            {
                var text = Snapshot(output);
                return text.Contains("TechConnect#", StringComparison.Ordinal) &&
                       text.LastIndexOf("TechConnect#", StringComparison.Ordinal) >
                       text.IndexOf(command, StringComparison.Ordinal);
            });

            await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("exit\r"), cts.Token);
            await run.WaitAsync(TimeSpan.FromSeconds(12));
            cts.Cancel();
            try
            {
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            return (Snapshot(output), factory);
        }
    }

    /// <summary>
    /// Thread-safe snapshot of the captured SSH/CLI output buffer.
    /// </summary>
    private static string Snapshot(StringBuilder output)
    {
        lock (output)
        {
            return output.ToString();
        }
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> is true or the timeout elapses.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
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

    /// <summary>
    /// Captures the completed copy source then fails open so transfer does not require serial I/O.
    /// </summary>
    private sealed class CapturingCopySourceFactory : ICopyFileSourceFactory
    {
        /// <summary>Last source passed to <see cref="OpenAsync"/> before the intentional failure.</summary>
        public CopySourceUri? LastSource { get; private set; }

        /// <summary>
        /// Captures <paramref name="source"/> then throws so transfer I/O is not required.
        /// </summary>
        /// <param name="source">Completed copy source from CLI prompting.</param>
        /// <param name="promptPasswordAsync">Unused password prompt callback.</param>
        /// <param name="confirmScpHostKeyAsync">Unused SCP host-key callback.</param>
        /// <param name="cancellationToken">Unused cancellation token.</param>
        /// <returns>Never returns; always throws.</returns>
        /// <exception cref="InvalidOperationException">Always thrown with a <c>% CAPTURED:</c> marker.</exception>
        public Task<ICopyFileSource> OpenAsync(
            CopySourceUri source,
            Func<string, CancellationToken, Task<string?>> promptPasswordAsync,
            Func<ScpHostKeyInfo, CancellationToken, Task<bool>>? confirmScpHostKeyAsync,
            CancellationToken cancellationToken)
        {
            LastSource = source;
            throw new InvalidOperationException($"% CAPTURED:{source.DisplayUri}");
        }
    }
}
