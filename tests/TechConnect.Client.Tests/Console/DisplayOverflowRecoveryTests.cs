// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    DisplayOverflowRecoveryTests.cs
//
// Purpose:
// Verifies SerialConsoleBuffer overflow drop accounting and VT parser soft-reset
// recovery after display-buffer overflow (REL-01).
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Observable overflow recovery tests for the display path (buffer + VT host).
/// </summary>
/// <remarks>
/// Mirrors the MainForm flush policy: when drain reports drops, soft-reset then write.
/// Does not touch serial transport or remote protocol bytes.
/// </remarks>
public sealed class DisplayOverflowRecoveryTests
{
    /// <summary>
    /// Applies the same flush policy as MainForm after TryDrain.
    /// </summary>
    private static void FlushToHost(
        VtTerminalHost host,
        byte[] data,
        long droppedSincePreviousDrain)
    {
        if (droppedSincePreviousDrain > 0)
        {
            host.ResyncAfterDisplayOverflow();
        }

        if (data.Length > 0)
        {
            host.Write(data);
        }
    }

    /// <summary>
    /// Scenario: overflow while ordinary ASCII text is pending.
    /// Expected: drops reported, pending stays bounded, remaining text renders after recovery.
    /// </summary>
    [Fact]
    public void Overflow_OrdinaryText_ReportsDrops_AndRemainsBounded()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        buffer.Enqueue(TestHelpers.Chunk(new string('a', 3000)));
        buffer.Enqueue(TestHelpers.Chunk(new string('b', 3000)));

        buffer.PendingByteCount.Should().Be(4096);
        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().BeGreaterThan(0);
        data.Length.Should().Be(4096);
        buffer.PendingByteCount.Should().Be(0);

        using var host = new VtTerminalHost(cols: 80, rows: 10, scrollbackLines: 100);
        FlushToHost(host, data, dropped);
        host.HasReceivedOutput.Should().BeTrue();
        host.GetVisibleText().Should().Contain("b");
    }

    /// <summary>
    /// Scenario: overflow splits a UTF-8 multi-byte character across the pending window.
    /// Expected: reset recovery then subsequent valid UTF-8 renders correctly.
    /// </summary>
    [Fact]
    public void Overflow_MidUtf8_ThenValidOutput_RendersAfterRecovery()
    {
        const int cap = 4096;
        var buffer = new SerialConsoleBuffer(maxPendingBytes: cap);

        // Fill to cap, then append a 2-byte UTF-8 sequence so trim drops into the sequence.
        buffer.Enqueue(TestHelpers.Chunk(new string('x', cap)));
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xC3, 0xA9 })); // é
        buffer.PendingByteCount.Should().Be(cap);

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(2);
        // After dropping 2 oldest 'x' bytes, pending is still cap bytes ending with C3 A9.
        data.Length.Should().Be(cap);

        using var host = new VtTerminalHost(cols: 40, rows: 5);
        FlushToHost(host, data, dropped);

        // Fresh valid output after recovery must not be stuck in a broken UTF-8 state.
        host.Write("\r\nok\r\n"u8);
        host.GetVisibleText().Should().Contain("ok");
    }

    /// <summary>
    /// Scenario: overflow leaves a partial ANSI CSI sequence at the pending head.
    /// Expected: soft-reset abandons incomplete CSI; subsequent text renders.
    /// </summary>
    [Fact]
    public void Overflow_MidAnsiEscape_ThenValidOutput_RendersAfterRecovery()
    {
        const int cap = 4096;
        var buffer = new SerialConsoleBuffer(maxPendingBytes: cap);

        buffer.Enqueue(TestHelpers.Chunk(new string('y', cap - 2)));
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0x1B, (byte)'[' })); // incomplete CSI
        buffer.Enqueue(TestHelpers.Chunk(new string('z', 10)));

        buffer.PendingByteCount.Should().BeLessThanOrEqualTo(cap);
        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().BeGreaterThan(0);

        using var host = new VtTerminalHost(cols: 40, rows: 5);
        FlushToHost(host, data, dropped);

        host.Write("recovered\r\n"u8);
        host.GetVisibleText().Should().Contain("recovered");
    }

    /// <summary>
    /// Scenario: incomplete CSI written to the host, then overflow reset, then plain text.
    /// Expected: Reset clears parser state so plain text is not consumed as CSI final.
    /// </summary>
    [Fact]
    public void Reset_AbandonsIncompleteCsi_WithoutClosingTransport()
    {
        using var host = new VtTerminalHost(cols: 40, rows: 5);
        host.Write(new byte[] { 0x1B, (byte)'[' }); // incomplete CSI
        host.ResyncAfterDisplayOverflow();
        host.Write("plain"u8);
        host.GetLine(0).Should().Be("plain");
    }

    /// <summary>
    /// Scenario: normal fragmented UTF-8 and ANSI without overflow.
    /// Expected: no soft-reset required; sequences decode correctly.
    /// </summary>
    [Fact]
    public void NormalFragmentedUtf8AndAnsi_NoUnnecessaryReset()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 8192);
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xC3 }));
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xA9 }));
        buffer.Enqueue(TestHelpers.Chunk("\u001b[1mBold\u001b[0m"u8.ToArray()));

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        dropped.Should().Be(0);

        using var host = new VtTerminalHost(cols: 40, rows: 5);
        // dropped == 0 → Write only (no ResyncAfterDisplayOverflow).
        host.Write(data);
        host.GetLine(0).Should().StartWith("é");
        host.GetLine(0).Should().Contain("Bold");
    }

    /// <summary>
    /// Scenario: overflow drops are display-only.
    /// Expected: SerialDataChunk used for enqueue is unchanged (transport path untouched).
    /// </summary>
    [Fact]
    public void Overflow_DoesNotMutateOriginalTransportChunk()
    {
        var payload = Encoding.ASCII.GetBytes(new string('q', 5000));
        var chunk = new SerialDataChunk
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Direction = SerialDataDirection.Rx,
            Data = payload
        };

        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        buffer.Enqueue(chunk);
        buffer.TryDrain(out _, out var dropped).Should().BeTrue();
        dropped.Should().BeGreaterThan(0);

        chunk.Data.ToArray().Should().Equal(payload);
        chunk.Data.Length.Should().Be(5000);
    }

    /// <summary>
    /// Scenario: repeated overflow enqueues.
    /// Expected: pending never exceeds soft cap; cumulative drops increase.
    /// </summary>
    [Fact]
    public void Overflow_KeepsPendingBounded_AndAccountsDrops()
    {
        var buffer = new SerialConsoleBuffer(maxPendingBytes: 4096);
        long lastDropped = 0;

        for (var i = 0; i < 5; i++)
        {
            buffer.Enqueue(TestHelpers.Chunk(new string('m', 3000)));
            buffer.PendingByteCount.Should().BeLessThanOrEqualTo(buffer.MaxPendingBytes);
        }

        buffer.TryDrain(out var data, out var dropped).Should().BeTrue();
        data.Length.Should().Be(4096);
        dropped.Should().BeGreaterThan(lastDropped);
        buffer.PendingByteCount.Should().Be(0);
    }
}
