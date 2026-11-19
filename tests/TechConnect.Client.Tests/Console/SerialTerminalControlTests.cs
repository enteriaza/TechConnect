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
}
