// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    VtTerminalHostTests.cs
//
// Purpose:
// Verifies VtTerminalHost blank start, VT100/ANSI processing, incremental input,
// scrollback, resize, and disposal behaviour.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using XTerm.Common;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Unit tests for <see cref="VtTerminalHost"/> VT100/ANSI screen state processing.
/// </summary>
public sealed class VtTerminalHostTests
{
    /// <summary>
    /// Scenario: newly constructed host.
    /// Expected: blank visible screen, no placeholder text, cursor hidden, no fabricated output.
    /// </summary>
    [Fact]
    public void InitialState_IsBlank_WithoutPlaceholderOrCursor()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 12);
        host.HasReceivedOutput.Should().BeFalse();
        host.Terminal.CursorVisible.Should().BeFalse();
        host.GetVisibleText().Trim().Should().BeEmpty();
        string.Join("", host.Terminal.GetVisibleLines()).Should().NotContain("console ready");
        string.Join("", host.Terminal.GetVisibleLines()).Should().NotContain("Serial TX/RX");
    }

    /// <summary>
    /// Scenario: printable ASCII with CR/LF.
    /// Expected: lines appear at the cursor without inventing extra breaks.
    /// </summary>
    [Fact]
    public void PrintableAscii_AndCrLf_RenderOnScreen()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 10);
        host.Write("line1\r\nline2"u8);
        host.GetLine(0).Should().Be("line1");
        host.GetLine(1).Should().Be("line2");
        host.HasReceivedOutput.Should().BeTrue();
        host.Terminal.CursorVisible.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: UTF-8 é split across two writes.
    /// Expected: character decodes correctly.
    /// </summary>
    [Fact]
    public void Utf8_SplitAcrossWrites_DecodesCorrectly()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write(new byte[] { 0xC3 });
        host.Write(new byte[] { 0xA9 });
        host.Write(" cafe"u8);
        host.GetLine(0).Should().Be("é cafe");
    }

    /// <summary>
    /// Scenario: backspace after printable characters.
    /// Expected: previous cell is erased/overwritten by subsequent print.
    /// </summary>
    [Fact]
    public void Backspace_ErasesPreviousCharacter()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("ab\bc"u8);
        host.GetLine(0).Should().Be("ac");
    }

    /// <summary>
    /// Scenario: horizontal tab.
    /// Expected: cursor advances to the next tab stop without corrupting prior text.
    /// </summary>
    [Fact]
    public void HorizontalTab_AdvancesCursor()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("A\tB"u8);
        var line = host.GetLine(0);
        line.Should().StartWith("A");
        line.Should().Contain("B");
        line.IndexOf('B', StringComparison.Ordinal).Should().BeGreaterThan(1);
    }

    /// <summary>
    /// Scenario: CSI 2J clear screen then home and print.
    /// Expected: prior content cleared; new text at origin.
    /// </summary>
    [Fact]
    public void ClearScreen_ErasesPriorContent()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 8);
        host.Write("stale\r\n"u8);
        host.Write("\x1b[2J\x1b[Hfresh"u8);
        host.GetVisibleText().Should().Contain("fresh");
        host.GetVisibleText().Should().NotContain("stale");
    }

    /// <summary>
    /// Scenario: CUP cursor positioning.
    /// Expected: text is written at the requested row/column.
    /// </summary>
    [Fact]
    public void CursorPositioning_WritesAtTargetCell()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 10);
        host.Write("\x1b[3;5HX"u8);
        host.GetLine(2).Should().Be("    X");
    }

    /// <summary>
    /// Scenario: cursor up/down/left/right.
    /// Expected: subsequent print lands at the moved cursor.
    /// </summary>
    [Fact]
    public void CursorMovement_RelativeMoves()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 10);
        host.Write("\x1b[5;5H*\x1b[1A\x1b[1D^"u8);
        host.GetLine(3).Should().Contain("^");
        host.GetLine(4).Should().Contain("*");
    }

    /// <summary>
    /// Scenario: EL erase from cursor to end of line.
    /// Expected: trailing characters on the line are cleared.
    /// </summary>
    [Fact]
    public void EraseLine_FromCursorToEnd()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("ABCDEF\r\x1b[K"u8);
        host.GetLine(0).TrimEnd().Should().BeEmpty();
    }

    /// <summary>
    /// Scenario: SGR foreground colour, bold, and reset.
    /// Expected: attributes applied then cleared.
    /// </summary>
    [Fact]
    public void Colors_Bold_AndReset_ApplyAttributes()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("\x1b[1;31mR\x1b[0mN"u8);
        var line = host.Terminal.Buffer.Lines[0]!;
        var red = line[0];
        var normal = line[1];
        red.Attributes.IsBold().Should().BeTrue();
        red.Attributes.GetFgColorKind().Should().Be(ColorMode.Palette16);
        red.Attributes.GetFgColor().Should().Be(1);
        normal.Attributes.IsBold().Should().BeFalse();
        normal.Attributes.GetFgColorKind().Should().Be(ColorMode.Default);
    }

    /// <summary>
    /// Scenario: reverse video SGR.
    /// Expected: inverse attribute set on the cell.
    /// </summary>
    [Fact]
    public void ReverseVideo_SetsInverseAttribute()
    {
        using var host = new VtTerminalHost(cols: 20, rows: 4);
        host.Write("\x1b[7mX\x1b[0m"u8);
        host.Terminal.Buffer.Lines[0]![0].Attributes.IsInverse().Should().BeTrue();
    }

    /// <summary>
    /// Scenario: underline SGR.
    /// Expected: underline attribute set on the cell.
    /// </summary>
    [Fact]
    public void Underline_SetsUnderlineAttribute()
    {
        using var host = new VtTerminalHost(cols: 20, rows: 4);
        host.Write("\x1b[4mU\x1b[0m"u8);
        host.Terminal.Buffer.Lines[0]![0].Attributes.IsUnderline().Should().BeTrue();
    }

    /// <summary>
    /// Scenario: save and restore cursor (CSI s / CSI u).
    /// Expected: print after restore lands at the saved position.
    /// </summary>
    [Fact]
    public void SaveAndRestoreCursor_RestoresPosition()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 10);
        host.Write("\x1b[4;6H\x1b[s\x1b[1;1HZ\x1b[uY"u8);
        host.GetLine(0).Should().StartWith("Z");
        host.GetLine(3).Should().Contain("Y");
    }

    /// <summary>
    /// Scenario: CSI escape sequence split across multiple writes.
    /// Expected: sequence is completed and applied when the remainder arrives.
    /// </summary>
    [Fact]
    public void EscapeSequence_SplitAcrossWrites_IsParsed()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 8);
        host.Write([(byte)0x1B]);
        host.Write("["u8);
        host.Write("2"u8);
        host.Write("J"u8);
        host.Write([(byte)0x1B, (byte)'[']);
        host.Write("Hok"u8);
        host.GetLine(0).Should().Be("ok");
    }

    /// <summary>
    /// Scenario: multiple sequences and text in one buffer.
    /// Expected: all are applied in order.
    /// </summary>
    [Fact]
    public void MultipleSequences_InOneWrite_AreApplied()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 8);
        host.Write("\x1b[2J\x1b[H\x1b[31mA\x1b[0mB"u8);
        host.GetLine(0).Should().Be("AB");
        host.Terminal.Buffer.Lines[0]![0].Attributes.GetFgColor().Should().Be(1);
        host.Terminal.Buffer.Lines[0]![1].Attributes.GetFgColorKind().Should().Be(ColorMode.Default);
    }

    /// <summary>
    /// Scenario: incomplete escape sequence then continuation.
    /// Expected: no premature printable garbage; final text correct.
    /// </summary>
    [Fact]
    public void IncompleteEscape_ThenContinuation_CompletesCorrectly()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("\x1b[3"u8);
        host.GetLine(0).Should().BeEmpty();
        host.Write("1mX\x1b[0m"u8);
        host.GetLine(0).Should().Be("X");
        host.Terminal.Buffer.Lines[0]![0].Attributes.GetFgColor().Should().Be(1);
    }

    /// <summary>
    /// Scenario: overwrite existing line via CR without LF (line editing).
    /// Expected: previous content on the line is replaced from column 0.
    /// </summary>
    [Fact]
    public void CarriageReturn_WithoutLf_UpdatesExistingLine()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write("old-prompt>\rnew>"u8);
        host.GetLine(0).Should().StartWith("new>");
    }

    /// <summary>
    /// Scenario: more lines than rows.
    /// Expected: viewport scrolls; older lines remain in scrollback.
    /// </summary>
    [Fact]
    public void Scrolling_MovesViewport_AndKeepsScrollback()
    {
        using var host = new VtTerminalHost(cols: 20, rows: 5, scrollbackLines: 100);
        for (var i = 0; i < 12; i++)
        {
            host.Write(Encoding.ASCII.GetBytes($"L{i:00}\r\n"));
        }

        host.IsScrolledToBottom.Should().BeTrue();
        host.GetVisibleText().Should().Contain("L11");
        host.ScrollLines(-20);
        host.IsScrolledToBottom.Should().BeFalse();
        host.GetVisibleText().Should().Contain("L00");
        host.ScrollToBottom();
        host.IsScrolledToBottom.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: resize after content.
    /// Expected: dimensions update; host remains usable.
    /// </summary>
    [Fact]
    public void Resize_UpdatesDimensions()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 10);
        host.Write("hello"u8);
        host.Resize(60, 20);
        host.Cols.Should().Be(60);
        host.Rows.Should().Be(20);
        host.GetVisibleText().Should().Contain("hello");
    }

    /// <summary>
    /// Scenario: Clear recreates a blank terminal.
    /// Expected: no prior text; HasReceivedOutput false; cursor hidden.
    /// </summary>
    [Fact]
    public void Clear_ResetsToBlankState()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 8);
        host.Write("gone"u8);
        host.Clear();
        host.HasReceivedOutput.Should().BeFalse();
        host.Terminal.CursorVisible.Should().BeFalse();
        host.GetVisibleText().Trim().Should().BeEmpty();
    }

    /// <summary>
    /// Scenario: dispose then write.
    /// Expected: ObjectDisposedException; no background work left running.
    /// </summary>
    [Fact]
    public void Dispose_PreventsFurtherWrites()
    {
        var host = new VtTerminalHost(cols: 20, rows: 5);
        host.Dispose();
        var act = () => host.Write("x"u8);
        act.Should().Throw<ObjectDisposedException>();
    }

    /// <summary>
    /// Scenario: ED erase from beginning of screen to cursor (CSI 1J) after positioned text.
    /// Expected: earlier cells cleared while later content may remain depending on cursor.
    /// </summary>
    [Fact]
    public void EraseScreen_FromStartToCursor()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 6);
        host.Write("AAAA\r\nBBBB\r\nCCCC"u8);
        host.Write("\x1b[2;3H\x1b[1J"u8);
        host.GetLine(0).TrimEnd().Should().BeEmpty();
    }

    /// <summary>
    /// Scenario: background colour SGR.
    /// Expected: background palette index applied.
    /// </summary>
    [Fact]
    public void BackgroundColor_AppliesAttribute()
    {
        using var host = new VtTerminalHost(cols: 20, rows: 4);
        host.Write("\x1b[44mX\x1b[0m"u8);
        var cell = host.Terminal.Buffer.Lines[0]![0];
        cell.Attributes.GetBgColorKind().Should().Be(ColorMode.Palette16);
        cell.Attributes.GetBgColor().Should().Be(4);
    }
}
