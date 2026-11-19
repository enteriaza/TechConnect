// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialTerminalControlTests.cs
//
// Purpose:
// Verifies SerialTerminalControl blank start, write/clear, and dispose safety on STA.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// STA tests for <see cref="SerialTerminalControl"/> display lifecycle.
/// </summary>
public sealed class SerialTerminalControlTests
{
    /// <summary>
    /// Scenario: control constructed and shown.
    /// Expected: blank visible text and no placeholder content.
    /// </summary>
    [Fact]
    public async Task StartsBlank_WithoutPlaceholder()
    {
        await StaTask.Run(() =>
        {
            using var control = new SerialTerminalControl
            {
                Width = 640,
                Height = 320
            };
            control.CreateControl();
            control.HasReceivedOutput.Should().BeFalse();
            control.GetVisibleText().Trim().Should().BeEmpty();
            control.GetVisibleText().Should().NotContain("console ready");
        });
    }

    /// <summary>
    /// Scenario: write ANSI-coloured text then clear.
    /// Expected: text appears without raw ESC noise; clear returns to blank.
    /// </summary>
    [Fact]
    public async Task Write_RendersText_Clear_ReturnsBlank()
    {
        await StaTask.Run(() =>
        {
            using var control = new SerialTerminalControl
            {
                Width = 640,
                Height = 320
            };
            control.CreateControl();
            control.Write("\x1b[32mrouter>\x1b[0m"u8);
            control.GetVisibleText().Should().Contain("router>");
            control.GetVisibleText().Should().NotContain("\u001b");
            control.ClearTerminal();
            control.HasReceivedOutput.Should().BeFalse();
            control.GetVisibleText().Trim().Should().BeEmpty();
        });
    }

    /// <summary>
    /// Scenario: dispose then write/clear/get text.
    /// Expected: no exceptions from post-dispose display calls.
    /// </summary>
    [Fact]
    public async Task Dispose_IgnoresSubsequentDisplayCalls()
    {
        await StaTask.Run(() =>
        {
            var control = new SerialTerminalControl
            {
                Width = 400,
                Height = 200
            };
            control.CreateControl();
            control.Dispose();
            var write = () => control.Write("x"u8);
            var clear = () => control.ClearTerminal();
            var text = () => control.GetVisibleText();
            write.Should().NotThrow();
            clear.Should().NotThrow();
            text.Should().NotThrow();
            text().Should().BeEmpty();
        });
    }

    /// <summary>
    /// Scenario: UTF-8 and SGR attributes then forced paint.
    /// Expected: visible text decodes; refresh does not throw or clear content.
    /// </summary>
    [Fact]
    public async Task Utf8AndSgr_SurviveForcedPaint()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 800, Height = 400 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            control.Write("\x1b[1;4;31mcafé\x1b[0m Δ\r\n"u8);
            control.Refresh();
            Application.DoEvents();

            var text = control.GetVisibleText();
            text.Should().Contain("café");
            text.Should().Contain("Δ");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: fragmented UTF-8 and CSI across Write calls with Invalidate/Refresh.
    /// Expected: final glyph and attributes assemble correctly.
    /// </summary>
    [Fact]
    public async Task FragmentedUtf8AndVt_AssembleAcrossWrites()
    {
        await StaTask.Run(() =>
        {
            using var control = new SerialTerminalControl { Width = 640, Height = 320 };
            control.CreateControl();
            control.Write(new byte[] { 0x1B, 0x5B, 0x33 }); // ESC [ 3
            control.Write("2m"u8);
            control.Write(new byte[] { 0xC3 });
            control.Write(new byte[] { 0xA9 });
            control.Write("x\x1b[0m"u8);
            control.Refresh();
            control.GetVisibleText().Should().Contain("éx");
        });
    }

    /// <summary>
    /// Scenario: large burst of lines with mouse-wheel scroll and paint.
    /// Expected: scrollback retains content; paint remains safe.
    /// </summary>
    [Fact]
    public async Task LargeBurst_ScrollbackAndPaint_RemainStable()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 900, Height = 500 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            for (var i = 0; i < 200; i++)
            {
                control.Write(System.Text.Encoding.UTF8.GetBytes($"line-{i:D3}\r\n"));
            }

            control.Refresh();
            control.Host.ScrollLines(-20);
            control.Refresh();
            Application.DoEvents();

            control.GetVisibleText().Should().Contain("line-");
            control.Host.ScrollToBottom();
            control.Refresh();
            control.GetVisibleText().Should().Contain("line-199");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: overflow recovery write path then paint.
    /// Expected: resync write succeeds and subsequent text is visible.
    /// </summary>
    [Fact]
    public async Task WriteAfterDisplayOverflow_ThenPaint_ShowsNewText()
    {
        await StaTask.Run(() =>
        {
            using var control = new SerialTerminalControl { Width = 640, Height = 320 };
            control.CreateControl();
            control.Write("stale\r\n"u8);
            control.WriteAfterDisplayOverflow("fresh-after-reset\r\n"u8);
            control.Refresh();
            control.GetVisibleText().Should().Contain("fresh-after-reset");
        });
    }

    /// <summary>
    /// Scenario: paint then dispose; recreate and paint again.
    /// Expected: GDI caches empty after dispose; new instance paints without throw.
    /// </summary>
    [Fact]
    public async Task Dispose_ClearsGdiCaches_AndRecreationPaints()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 640, Height = 360 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            control.Write("\x1b[31mred\x1b[32mgreen\x1b[0m\r\n"u8);
            control.Refresh();
            Application.DoEvents();
            control.BrushCacheCountForTests.Should().BeGreaterThan(0);

            control.Dispose();
            control.BrushCacheCountForTests.Should().Be(0);
            control.PenCacheCountForTests.Should().Be(0);

            var control2 = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Clear();
            form.Controls.Add(control2);
            Application.DoEvents();
            control2.Write("ok\r\n"u8);
            control2.Refresh();
            control2.GetVisibleText().Should().Contain("ok");
            control2.Dispose();
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: many distinct RGB SGR colours in one viewport paint.
    /// Expected: brush cache stays within the soft capacity cap.
    /// </summary>
    [Fact]
    public async Task ManyRgbColours_BrushCacheRemainsBounded()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 1100, Height = 700 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < 500; i++)
            {
                var r = (i * 17) % 256;
                var g = (i * 29) % 256;
                var b = (i * 43) % 256;
                sb.Append($"\x1b[38;2;{r};{g};{b}m#");
            }

            sb.Append("\x1b[0m\r\n");
            control.Write(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
            control.Refresh();
            Application.DoEvents();

            control.BrushCacheCountForTests.Should().BeLessThanOrEqualTo(384);
            control.Dispose();
            control.BrushCacheCountForTests.Should().Be(0);
            form.Close();
        });
    }
}
