// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    InteractiveLineReaderTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (InteractiveLineReaderTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for InteractiveLineReader editing, history, and discovery.
/// </summary>
public sealed class InteractiveLineReaderTests
{
    /// <summary>
    /// Verifies: Character Input Is Echoed.
    /// </summary>
    [Fact]
    public async Task CharacterInput_IsEchoed()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "TechConnect#");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("ab\r"));
        var result = await readTask;

        result.Outcome.Should().Be(LineReadOutcome.Line);
        result.Text.Should().Be("ab");
        output.ToString().Should().Contain("a").And.Contain("b");
    }

    /// <summary>
    /// Verifies: Enter Submits Line.
    /// </summary>
    [Theory]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Enter_SubmitsLine(string terminator)
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "TechConnect>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("hi" + terminator));
        var result = await readTask;

        result.Text.Should().Be("hi");
        result.Outcome.Should().Be(LineReadOutcome.Line);
    }

    /// <summary>
    /// Verifies: Crlf Executes Exactly One Command.
    /// </summary>
    [Fact]
    public async Task Crlf_ExecutesExactlyOneCommand()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "TechConnect>");

        var first = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("one\r\n"));
        (await first).Text.Should().Be("one");

        var second = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("two\n"));
        (await second).Text.Should().Be("two");
    }

    /// <summary>
    /// Verifies: Backspace Removes Character From Buffer And Display.
    /// </summary>
    [Fact]
    public async Task Backspace_RemovesCharacterFromBufferAndDisplay()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(new byte[] { (byte)'a', (byte)'b', 0x08, (byte)'c', (byte)'\r' });
        var result = await readTask;

        result.Text.Should().Be("ac");
    }

    /// <summary>
    /// Verifies: Delete Removes Character At Cursor.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesCharacterAtCursor()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("abc"));
        await pipe.Client.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'D', 0x1B, (byte)'[', (byte)'D' });
        await pipe.Client.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'3', (byte)'~' });
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("\r"));
        var result = await readTask;

        result.Text.Should().Be("ac");
    }

    /// <summary>
    /// Verifies: Ctrl C Cancels Line.
    /// </summary>
    [Fact]
    public async Task CtrlC_CancelsLine()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(new byte[] { (byte)'x', 0x03 });
        var result = await readTask;

        result.Outcome.Should().Be(LineReadOutcome.Cancelled);
        output.ToString().Should().Contain("^C");
    }

    /// <summary>
    /// Verifies: Ctrl D On Empty Exits.
    /// </summary>
    [Fact]
    public async Task CtrlD_OnEmpty_Exits()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(new byte[] { 0x04 });
        (await readTask).Outcome.Should().Be(LineReadOutcome.Exit);
    }

    /// <summary>
    /// Verifies: Ctrl U Clears Current Line.
    /// </summary>
    [Fact]
    public async Task CtrlU_ClearsCurrentLine()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(new byte[] { (byte)'a', (byte)'b', 0x15, (byte)'z', (byte)'\r' });
        (await readTask).Text.Should().Be("z");
    }

    /// <summary>
    /// Verifies: Empty Input Returns Empty Line.
    /// </summary>
    [Fact]
    public async Task EmptyInput_ReturnsEmptyLine()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("\r"));
        var result = await readTask;
        result.Outcome.Should().Be(LineReadOutcome.Line);
        result.Text.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies: Maximum Line Length Rejects Additional Characters.
    /// </summary>
    [Fact]
    public async Task MaximumLineLength_RejectsAdditionalCharacters()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>", maxLineLength: 3);

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("abcd\r"));
        var result = await readTask;

        result.Text.Should().Be("abc");
        output.ToString().Should().Contain("\a");
    }

    /// <summary>
    /// Verifies: Input Split Across Multiple Reads Is Assembled.
    /// </summary>
    [Fact]
    public async Task InputSplitAcrossMultipleReads_IsAssembled()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("he"));
        await Task.Delay(20);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("llo\r"));
        (await readTask).Text.Should().Be("hello");
    }

    /// <summary>
    /// Verifies: Multiple Commands In One Stream Are Read Sequentially.
    /// </summary>
    [Fact]
    public async Task MultipleCommandsInOneStream_AreReadSequentially()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("one\ntwo\n"));
        (await reader.ReadLineAsync(CancellationToken.None)).Text.Should().Be("one");
        (await reader.ReadLineAsync(CancellationToken.None)).Text.Should().Be("two");
    }

    /// <summary>
    /// Verifies: Disconnect During Input Returns Exit Without Executing Partial.
    /// </summary>
    [Fact]
    public async Task DisconnectDuringInput_ReturnsExitWithoutExecutingPartial()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("partial"));
        await pipe.Client.DisposeAsync();
        var result = await readTask;
        result.Outcome.Should().Be(LineReadOutcome.Exit);
    }

    /// <summary>
    /// Verifies: History Up Down Recalls Commands.
    /// </summary>
    [Fact]
    public async Task History_UpDown_RecallsCommands()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "P>");

        var first = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show sessions\r"));
        await first;

        var second = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(new byte[] { 0x1B, (byte)'[', (byte)'A', (byte)'\r' });
        (await second).Text.Should().Be("show sessions");
    }

    /// <summary>
    /// Verifies: Tab Completes Unambiguous Command Inline.
    /// </summary>
    [Fact]
    public async Task Tab_CompletesUnambiguousCommand_Inline()
    {
        var discovery = new CliCommandDiscovery();
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(
            pipe.Server,
            output,
            "TechConnect#",
            (line, cursor) => discovery.Complete(line, cursor, Admin(), 80),
            (line, cursor) => discovery.Help(line, cursor, Admin(), 80));

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("att"));
        await pipe.Client.WriteAsync(new byte[] { 0x09 });
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("\r"));
        (await readTask).Text.Should().Be("attach");
    }

    /// <summary>
    /// Verifies: Tab Ambiguous Lists Next Tokens Extends Common Prefix.
    /// </summary>
    [Fact]
    public async Task Tab_Ambiguous_ListsNextTokens_ExtendsCommonPrefix()
    {
        var discovery = new CliCommandDiscovery();
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(
            pipe.Server,
            output,
            "TechConnect#",
            (line, cursor) => discovery.Complete(line, cursor, Admin(), 80),
            (line, cursor) => discovery.Help(line, cursor, Admin(), 80));

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show s"));
        await pipe.Client.WriteAsync(new byte[] { 0x09 });
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("\r"));
        var result = await readTask;

        result.Text.Should().Be("show se");
        var text = output.ToString();
        text.Should().Contain("sessions");
        text.Should().Contain("session");
        text.Should().Contain("serial");
    }

    /// <summary>
    /// Verifies: Question Mark Connect Shows Session Placeholder Preserves Buffer.
    /// </summary>
    [Fact]
    public async Task QuestionMark_Connect_ShowsSessionPlaceholder_PreservesBuffer()
    {
        var discovery = new CliCommandDiscovery();
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(
            pipe.Server,
            output,
            "TechConnect>",
            (line, cursor) => discovery.Complete(line, cursor, ReadOnly(), 80),
            (line, cursor) => discovery.Help(line, cursor, ReadOnly(), 80));

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("connect "));
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("?"));
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("deadbeef\r"));
        var result = await readTask;

        result.Text.Should().Be("connect deadbeef");
        output.ToString().Should().Contain("<session-id>");
        output.ToString().Should().Contain("Connector session identifier");
        result.Text.Should().NotContain("?");
    }

    /// <summary>
    /// Verifies: Tab Connect Serial Keyword Completes Unique.
    /// </summary>
    [Fact]
    public async Task Tab_ConnectSerialKeyword_CompletesUnique()
    {
        var discovery = new CliCommandDiscovery();
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(
            pipe.Server,
            output,
            "TechConnect>",
            (line, cursor) => discovery.Complete(line, cursor, ReadOnly(), 80),
            (line, cursor) => discovery.Help(line, cursor, ReadOnly(), 80));

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("connect deadbeef pa"));
        await pipe.Client.WriteAsync(new byte[] { 0x09 });
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("\r"));
        (await readTask).Text.Should().Be("connect deadbeef parity ");
    }

    /// <summary>
    /// Verifies: Question Mark Shows Help Does Not Insert Or Execute.
    /// </summary>
    [Fact]
    public async Task QuestionMark_ShowsHelp_DoesNotInsertOrExecute()
    {
        var discovery = new CliCommandDiscovery();
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(
            pipe.Server,
            output,
            "TechConnect#",
            (line, cursor) => discovery.Complete(line, cursor, Admin(), 80),
            (line, cursor) => discovery.Help(line, cursor, Admin(), 80));

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("show "));
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("?"));
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("sessions\r"));
        var result = await readTask;

        result.Text.Should().Be("show sessions");
        output.ToString().Should().Contain("sessions");
        output.ToString().Should().Contain("List connector sessions");
        result.Text.Should().NotContain("?");
    }

    /// <summary>
    /// Verifies: History Redacts Copy Url Passwords.
    /// </summary>
    [Fact]
    public async Task History_RedactsCopyUrlPasswords()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = CreateReader(pipe.Server, output, "TechConnect#");

        var readTask = reader.ReadLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(
            Encoding.UTF8.GetBytes("copy https://alice:s3cret@host/a.bin serial:abc\r"));
        var result = await readTask;

        result.Text.Should().Contain("s3cret"); // raw submitted line still has password for execution
        reader.History.Should().ContainSingle();
        reader.History[0].Should().NotContain("s3cret");
        reader.History[0].Should().Contain("alice:****@");
    }

    /// <summary>
    /// Verifies: Discovery Read Only Discloses Attach Not Removed Commands.
    /// </summary>
    [Fact]
    public void Discovery_ReadOnly_DisclosesAttach_NotRemovedCommands()
    {
        var discovery = new CliCommandDiscovery();
        discovery.Complete("c", 1, ReadOnly(), 80).Tokens.Should().Contain("connect");
        discovery.Complete("a", 1, ReadOnly(), 80).Tokens.Should().Contain("attach");
        discovery.Complete("set", 3, ReadOnly(), 80).Kind.Should().Be(CliDiscoveryKind.None);
        discovery.Complete("t", 1, ReadOnly(), 80).Tokens.Should().Contain("traceroute");
        discovery.Complete("ter", 3, ReadOnly(), 80).Kind.Should().Be(CliDiscoveryKind.None);
        discovery.Complete("a", 1, Admin(), 80).Tokens.Should().Contain("attach");
        discovery.Complete("c", 1, Admin(), 80).Tokens.Should().Contain("connect");
    }
    /// <summary>
    /// Performs admin.
    /// </summary>
    /// <returns>The operation result.</returns>

    private static AuthenticatedOperator Admin() =>
        TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.AdministratorBoundary);
    /// <summary>
    /// Performs read only.
    /// </summary>
    /// <returns>The operation result.</returns>

    private static AuthenticatedOperator ReadOnly() =>
        TestAuthenticatedOperator.Create(privilege: PrivilegeLevel.ReadOnlyBoundary);
    /// <summary>
    /// Performs create reader.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="output">The output.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="complete">The complete.</param>
    /// <param name="help">The help.</param>
    /// <param name="maxLineLength">The max line length.</param>
    /// <returns>The operation result.</returns>

    private static InteractiveLineReader CreateReader(
        Stream server,
        StringBuilder output,
        string prompt,
        Func<string, int, CliDiscoveryResult>? complete = null,
        Func<string, int, CliDiscoveryResult>? help = null,
        int maxLineLength = InteractiveLineReader.DefaultMaxLineLength)
    {
        return new InteractiveLineReader(
            server,
            (bytes, _) =>
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                return default;
            },
            () => prompt,
            complete ?? ((_, _) => CliDiscoveryResult.None),
            help ?? ((_, _) => CliDiscoveryResult.None),
            () => 80,
            maxLineLength);
    }
}
