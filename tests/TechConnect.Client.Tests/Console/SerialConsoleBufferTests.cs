// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialConsoleBufferTests.cs
//
// Purpose:
// Verifies SerialConsoleBuffer UTF-8 decoding, TX filtering, drain/trim behaviour, and display helpers.
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Unit tests for <see cref="TechConnect.Client.Forms.SerialConsoleBuffer"/> decode, drain, trim, and TX filtering.
/// </summary>
public sealed class SerialConsoleBufferTests
{
    /// <summary>
    /// Scenario: enqueue fragmented RX text chunks then drain.
    /// Expected: contiguous 'Starting device...' without RX prefixes, pipes, or artificial newlines.
    /// </summary>
    [Fact]
    public void MultipleReads_FormContinuousText_WithoutArtificialNewlinesOrPrefixes()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("Start"u8.ToArray()));
        buffer.Enqueue(TestHelpers.Chunk("ing"u8.ToArray()));
        buffer.Enqueue(TestHelpers.Chunk(" device..."u8.ToArray()));

        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("Starting device...");
        text.Should().NotContain("RX");
        text.Should().NotContain("|");
        text.Should().NotContain("\n");
    }

    /// <summary>
    /// Scenario: enqueue one byte at a time for 'Starting'.
    /// Expected: drain yields 'Starting' without bracket/RX decorations.
    /// </summary>
    [Fact]
    public void SingleByteReads_FormContinuousText()
    {
        var buffer = new SerialConsoleBuffer();
        foreach (var b in "Starting"u8.ToArray())
        {
            buffer.Enqueue(TestHelpers.Chunk(new[] { b }));
        }

        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("Starting");
        text.Should().NotContain("[");
        text.Should().NotContain("RX");
    }

    /// <summary>
    /// Scenario: enqueue lines ending in CRLF.
    /// Expected: drained text preserves exact CRLF pairs without duplication.
    /// </summary>
    [Fact]
    public void CrLf_PreservedWithoutDuplicateBreaks()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("line1\r\n"u8.ToArray()));
        buffer.Enqueue(TestHelpers.Chunk("line2\r\n"u8.ToArray()));

        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("line1\r\nline2\r\n");
    }

    /// <summary>
    /// Scenario: enqueue LF-only line breaks.
    /// Expected: drained text preserves single LF characters.
    /// </summary>
    [Fact]
    public void LfOnly_Preserved()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("a\nb\n"u8.ToArray()));
        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("a\nb\n");
    }

    /// <summary>
    /// Scenario: UTF-8 bytes for e-acute arrive split across two enqueues.
    /// Expected: drain decodes to 'e cafe' with the correct character.
    /// </summary>
    [Fact]
    public void Utf8Multibyte_SplitAcrossReads_DecodesCorrectly()
    {
        // U+00E9 (é) is C3 A9 in UTF-8
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xC3 }));
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xA9 }));
        buffer.Enqueue(TestHelpers.Chunk(" cafe"u8.ToArray()));

        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("é cafe");
    }

    /// <summary>
    /// Scenario: enqueue TX then RX chunks.
    /// Expected: drain shows only RX text; TX secret is absent.
    /// </summary>
    [Fact]
    public void TxChunks_DoNotAppearInConsole()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("secret-tx", SerialDataDirection.Tx));
        buffer.Enqueue(TestHelpers.Chunk("visible-rx", SerialDataDirection.Rx));

        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("visible-rx");
        text.Should().NotContain("secret-tx");
    }

    /// <summary>
    /// Scenario: drain an empty buffer.
    /// Expected: false with empty text and zero trim.
    /// </summary>
    [Fact]
    public void TryDrain_Empty_ReturnsFalse()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.TryDrain(out var text, out var trim).Should().BeFalse();
        text.Should().BeEmpty();
        trim.Should().Be(0);
    }

    /// <summary>
    /// Scenario: drain twice with new enqueues between.
    /// Expected: first drain concatenates prior chunks; second drain returns only the new chunk.
    /// </summary>
    [Fact]
    public void TryDrain_PreservesOrderAcrossBatches()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk("one"));
        buffer.Enqueue(TestHelpers.Chunk("two"));
        buffer.TryDrain(out var first, out _).Should().BeTrue();
        first.Should().Be("onetwo");

        buffer.Enqueue(TestHelpers.Chunk("three"));
        buffer.TryDrain(out var second, out _).Should().BeTrue();
        second.Should().Be("three");
    }

    /// <summary>
    /// Scenario: enqueue far more than maxCharacters then drain.
    /// Expected: text exceeds max and trim > 0; subsequent small enqueue drains cleanly.
    /// </summary>
    [Fact]
    public void Trim_WhenOverMax_ReportsCharactersToTrim()
    {
        var buffer = new SerialConsoleBuffer(maxCharacters: 4096);

        for (var i = 0; i < 200; i++)
        {
            buffer.Enqueue(TestHelpers.Chunk(new string('x', 40)));
        }

        buffer.TryDrain(out var text, out var trim).Should().BeTrue();
        text.Length.Should().BeGreaterThan(4096);
        trim.Should().BeGreaterThan(0);

        buffer.Enqueue(TestHelpers.Chunk("after"));
        buffer.TryDrain(out var more, out var trim2).Should().BeTrue();
        more.Should().Be("after");
        trim2.Should().BeGreaterThanOrEqualTo(0);
    }

    /// <summary>
    /// Scenario: incomplete UTF-8 pending, then ClearPending, then enqueue ASCII.
    /// Expected: drain yields only the post-clear ASCII.
    /// </summary>
    [Fact]
    public void ClearPending_DiscardsUndrainedAndResetsDecoder()
    {
        var buffer = new SerialConsoleBuffer();
        buffer.Enqueue(TestHelpers.Chunk(new byte[] { 0xC3 })); // incomplete UTF-8
        buffer.ClearPending();
        buffer.Enqueue(TestHelpers.Chunk("ok"u8.ToArray()));
        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().Be("ok");
    }

    /// <summary>
    /// Scenario: overflow trim then NotifyDisplayCleared before a small enqueue.
    /// Expected: next drain trim is 0 for the small text.
    /// </summary>
    [Fact]
    public void NotifyDisplayCleared_ResetsTrimBaseline()
    {
        var buffer = new SerialConsoleBuffer(maxCharacters: 4096);

        for (var i = 0; i < 200; i++)
        {
            buffer.Enqueue(TestHelpers.Chunk(new string('y', 40)));
        }

        buffer.TryDrain(out _, out var trim).Should().BeTrue();
        trim.Should().BeGreaterThan(0);

        buffer.NotifyDisplayCleared();
        buffer.Enqueue(TestHelpers.Chunk("small"));
        buffer.TryDrain(out var text, out var trimAfterClear).Should().BeTrue();
        text.Should().Be("small");
        trimAfterClear.Should().Be(0);
    }

    /// <summary>
    /// Scenario: DecodeForDisplay on printable ASCII with tab/CRLF.
    /// Expected: string is unchanged.
    /// </summary>
    [Fact]
    public void DecodeForDisplay_PreservesPrintableAndWhitespace()
    {
        var data = Encoding.ASCII.GetBytes("A\tB\r\nC");
        SerialConsoleBuffer.DecodeForDisplay(data).Should().Be("A\tB\r\nC");
    }

    /// <summary>
    /// Scenario: DecodeForDisplay on NUL/SOH plus 'x'.
    /// Expected: C0 controls become middle-dot placeholders.
    /// </summary>
    [Fact]
    public void DecodeForDisplay_ReplacesC0Controls()
    {
        var data = new byte[] { 0x00, 0x01, (byte)'x' };
        SerialConsoleBuffer.DecodeForDisplay(data).Should().Be("··x");
    }

    /// <summary>
    /// Scenario: enqueue 16KiB of 'z' and drain once.
    /// Expected: exact length, no RX prefix, payload intact.
    /// </summary>
    [Fact]
    public void LargeContinuousStream_SingleDrain_NoRxPrefix()
    {
        var buffer = new SerialConsoleBuffer();
        var large = Encoding.ASCII.GetBytes(new string('z', 16_384));
        buffer.Enqueue(TestHelpers.Chunk(large));
        buffer.TryDrain(out var text, out _).Should().BeTrue();
        text.Should().HaveLength(16_384);
        text.Should().NotContain("RX");
        text.Should().Be(new string('z', 16_384));
    }
}
