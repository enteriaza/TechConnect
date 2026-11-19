// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialTerminalDirtyPaintTests.cs
//
// Purpose:
// Correctness coverage for PERF-UI-01 dirty-row invalidation (BufferLine.Version).
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Verifies dirty-row vs full invalidation behaviour on <see cref="SerialTerminalControl"/>.
/// </summary>
public sealed class SerialTerminalDirtyPaintTests
{
    /// <summary>
    /// Scenario: fill the viewport, paint once, then overwrite one mid-screen line in place (no scroll).
    /// Expected: dirty invalidation (not full) and far fewer rows painted than the viewport height.
    /// </summary>
    [Fact]
    public async Task InPlaceLineUpdate_PaintsFewerRowsThanFullRefresh()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 1000, Height = 700 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var cols = Math.Max(40, control.Host.Cols);
            var rows = Math.Max(20, control.Host.Rows);
            var fill = new StringBuilder();
            for (var r = 0; r < rows; r++)
            {
                fill.Append(new string((char)('A' + (r % 26)), cols)).Append("\r\n");
            }

            control.Write(Encoding.UTF8.GetBytes(fill.ToString()));
            control.Refresh();
            Application.DoEvents();
            control.LastPaintedRowCountForTests.Should().Be(rows);

            // CUP to row 5 and replace text without LF — YDisp unchanged.
            control.Write(Encoding.UTF8.GetBytes("\x1b[5;1Hin-place-update"));
            Application.DoEvents(); // dirty Invalidate rects only (do not Refresh)

            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BePositive();
            control.LastPaintedRowCountForTests.Should().BeLessThan(rows / 2);
            control.GetVisibleText().Should().Contain("in-place-update");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: cursor-only CUP after a painted screen.
    /// Expected: not a full invalidate; visible text unchanged.
    /// </summary>
    [Fact]
    public async Task CursorOnlyMove_DoesNotFullInvalidate()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 900, Height = 600 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("hello world\r\nsecond\r\n"));
            control.Refresh();
            Application.DoEvents();
            var before = control.GetVisibleText();

            control.Write(Encoding.UTF8.GetBytes("\x1b[1;1H"));
            Application.DoEvents();
            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.GetVisibleText().Should().Be(before);
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: clear / overflow resync / resize-equivalent full reset paths.
    /// Expected: full invalidate fingerprint reset; content remains correct.
    /// </summary>
    [Fact]
    public async Task ClearAndOverflowResync_ForceFullInvalidate()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 900, Height = 600 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("before-clear\r\n"));
            control.Refresh();
            Application.DoEvents();

            control.ClearTerminal();
            control.LastInvalidateWasFullForTests.Should().BeTrue();
            control.Refresh();
            Application.DoEvents();
            control.GetVisibleText().Trim().Should().BeEmpty();

            control.Write(Encoding.UTF8.GetBytes("\x1b[")); // incomplete CSI
            control.WriteAfterDisplayOverflow(Encoding.UTF8.GetBytes("recovered\r\n"));
            control.Refresh();
            Application.DoEvents();
            control.GetVisibleText().Should().Contain("recovered");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: SGR-coloured multi-row fill then one coloured line update.
    /// Expected: colours still render (non-empty text) and dirty path stays partial.
    /// </summary>
    [Fact]
    public async Task SgrLineUpdate_RemainsCorrectAndPartial()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 1000, Height = 700 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var rows = Math.Max(20, control.Host.Rows);
            var sb = new StringBuilder();
            for (var r = 0; r < rows; r++)
            {
                sb.Append($"\x1b[{30 + (r % 8)}mrow-{r}\x1b[0m\r\n");
            }

            control.Write(Encoding.UTF8.GetBytes(sb.ToString()));
            control.Refresh();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("\x1b[10;1H\x1b[31mNEW\x1b[0m"));
            Application.DoEvents();
            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BeLessThan(rows / 2);
            control.GetVisibleText().Should().Contain("NEW");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: viewport already full; appending a line scrolls (YDisp changes).
    /// Expected: full invalidate — every viewport row remaps.
    /// </summary>
    [Fact]
    public async Task ScrollingAppend_ForcesFullInvalidate()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 1000, Height = 700 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var cols = Math.Max(40, control.Host.Cols);
            var rows = Math.Max(20, control.Host.Rows);
            var fill = new StringBuilder();
            for (var r = 0; r < rows + 2; r++)
            {
                fill.Append(new string((char)('A' + (r % 26)), cols)).Append("\r\n");
            }

            control.Write(Encoding.UTF8.GetBytes(fill.ToString()));
            control.Refresh();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("scroll-tail\r\n"));
            Application.DoEvents();
            control.LastInvalidateWasFullForTests.Should().BeTrue();
            control.GetVisibleText().Should().Contain("scroll-tail");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: cursor CUP from one row to another without cell edits.
    /// Expected: partial invalidate covering old and new cursor rows (≤2 painted).
    /// </summary>
    [Fact]
    public async Task CursorMoveAcrossRows_DirtiesOldAndNewRows()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            control.Write(Encoding.UTF8.GetBytes("\x1b[2;1H"));
            Application.DoEvents();
            control.Write(Encoding.UTF8.GetBytes("\x1b[8;3H"));
            Application.DoEvents();

            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BeInRange(1, 2);
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: multiple Writes to the same viewport row before the UI paints.
    /// Expected: one partial paint; final text visible; pending mask cleared afterward.
    /// </summary>
    [Fact]
    public async Task MultipleUpdatesSameRow_BeforePaint_Coalesce()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            control.Write(Encoding.UTF8.GetBytes("\x1b[4;1Hfirst"));
            control.Write(Encoding.UTF8.GetBytes("\x1b[4;1Hsecond"));
            control.Write(Encoding.UTF8.GetBytes("\x1b[4;1Hfinal-coalesce"));
            Application.DoEvents();

            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BeLessThan(control.Host.Rows / 2);
            control.GetVisibleText().Should().Contain("final-coalesce");
            Span<bool> pending = stackalloc bool[Math.Max(8, control.Host.Rows)];
            control.TryCopyPendingPaintRowsForTests(pending).Should().BeTrue();
            for (var i = 0; i < control.Host.Rows; i++)
            {
                pending[i].Should().BeFalse();
            }

            form.Close();
        });
    }

    /// <summary>
    /// Scenario: two different rows dirtied before paint.
    /// Expected: both updates visible; painted row count small and partial.
    /// </summary>
    [Fact]
    public async Task UpdatesToDifferentRows_BeforePaint_BothVisible()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            control.Write(Encoding.UTF8.GetBytes("\x1b[3;1Halpha-row"));
            control.Write(Encoding.UTF8.GetBytes("\x1b[9;1Hbeta-row"));
            Application.DoEvents();

            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BeLessThan(control.Host.Rows / 2);
            var text = control.GetVisibleText();
            text.Should().Contain("alpha-row");
            text.Should().Contain("beta-row");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: Write during OnPaint after pending mask was snapshotted/cleared.
    /// Expected: nested update remains scheduled and becomes visible after the next paint.
    /// </summary>
    [Fact]
    public async Task UpdateDuringPaint_IsNotLost()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            var nestedScheduled = false;
            control.BeforePaintRowsForTests = () =>
            {
                if (nestedScheduled)
                {
                    return;
                }

                nestedScheduled = true;
                control.Write(Encoding.UTF8.GetBytes("\x1b[11;1Hnested-during-paint"));
            };

            control.Write(Encoding.UTF8.GetBytes("\x1b[5;1Hprimary-dirty"));
            Application.DoEvents(); // paints primary; nested Write schedules second dirty set
            control.BeforePaintRowsForTests = null;
            Application.DoEvents(); // paints nested row

            control.GetVisibleText().Should().Contain("primary-dirty");
            control.GetVisibleText().Should().Contain("nested-during-paint");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: row painted at version N is mutated to N+1 after PaintLine (before fingerprint).
    /// Expected: fingerprint stays at N; row remains pending; follow-up paint shows latest text.
    /// </summary>
    [Fact]
    public async Task NestedSameRowUpdate_AfterPaintLine_DoesNotAdvancePaintedVersionPastRendered()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            const int targetRow = 4; // 1-based CUP row 5
            control.Write(Encoding.UTF8.GetBytes("\x1b[5;1Hversion-ten"));
            Application.DoEvents();

            var mutated = false;
            var midPassChecked = false;
            control.AfterPaintRowForTests = row =>
            {
                if (mutated || row != targetRow)
                {
                    return;
                }

                mutated = true;
                control.Write(Encoding.UTF8.GetBytes("\x1b[5;1Hversion-eleven"));
            };
            control.AfterPaintPassForTests = () =>
            {
                if (!mutated || midPassChecked)
                {
                    return;
                }

                midPassChecked = true;
                var painted = control.GetPaintedLineVersionForTests(targetRow);
                var live = control.GetBufferLineVersionForTests(targetRow);
                live.Should().BeGreaterThan(painted);
                control.PendingPaintFullForTests.Should().BeFalse();
                Span<bool> pending = stackalloc bool[control.Host.Rows];
                control.TryCopyPendingPaintRowsForTests(pending).Should().BeTrue();
                pending[targetRow].Should().BeTrue();
            };

            control.Write(Encoding.UTF8.GetBytes("\x1b[5;1Hversion-ten-b"));
            Application.DoEvents();
            control.AfterPaintRowForTests = null;
            control.AfterPaintPassForTests = null;

            mutated.Should().BeTrue();
            midPassChecked.Should().BeTrue();
            control.GetVisibleText().Should().Contain("version-eleven");
            control.GetPaintedLineVersionForTests(targetRow)
                .Should().Be(control.GetBufferLineVersionForTests(targetRow));
            Span<bool> finalPending = stackalloc bool[control.Host.Rows];
            if (control.TryCopyPendingPaintRowsForTests(finalPending))
            {
                finalPending[targetRow].Should().BeFalse();
            }

            form.Close();
        });
    }

    /// <summary>
    /// Scenario: multiple nested Writes to the same row after PaintLine.
    /// Expected: fingerprint stays behind live version until a clean follow-up paint.
    /// </summary>
    [Fact]
    public async Task MultipleNestedSameRowUpdates_AfterPaintLine_RemainPending()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            const int targetRow = 6;
            var nested = 0;
            var midPassChecked = false;
            control.AfterPaintRowForTests = row =>
            {
                if (row != targetRow || nested >= 2)
                {
                    return;
                }

                nested++;
                control.Write(Encoding.UTF8.GetBytes($"\x1b[7;1Hnested-{nested}"));
            };
            control.AfterPaintPassForTests = () =>
            {
                if (nested < 2 || midPassChecked)
                {
                    return;
                }

                midPassChecked = true;
                control.GetBufferLineVersionForTests(targetRow)
                    .Should().BeGreaterThan(control.GetPaintedLineVersionForTests(targetRow));
                Span<bool> pending = stackalloc bool[control.Host.Rows];
                control.TryCopyPendingPaintRowsForTests(pending).Should().BeTrue();
                pending[targetRow].Should().BeTrue();
            };

            control.Write(Encoding.UTF8.GetBytes("\x1b[7;1Hbase-row"));
            Application.DoEvents();
            control.AfterPaintRowForTests = null;
            control.AfterPaintPassForTests = null;

            nested.Should().Be(2);
            midPassChecked.Should().BeTrue();
            control.GetVisibleText().Should().Contain("nested-2");
            control.GetPaintedLineVersionForTests(targetRow)
                .Should().Be(control.GetBufferLineVersionForTests(targetRow));
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: nested Write dirties another row after the outer row paints.
    /// Expected: outer fingerprint matches live; other row stays pending then paints.
    /// </summary>
    [Fact]
    public async Task NestedOtherRowUpdate_AfterPaintLine_PreservesSeparateDirtyBit()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            const int outerRow = 3;
            const int otherRow = 10;
            var nested = false;
            var midPassChecked = false;
            control.AfterPaintRowForTests = row =>
            {
                if (nested || row != outerRow)
                {
                    return;
                }

                nested = true;
                control.Write(Encoding.UTF8.GetBytes("\x1b[11;1Hother-row-nested"));
            };
            control.AfterPaintPassForTests = () =>
            {
                if (!nested || midPassChecked)
                {
                    return;
                }

                midPassChecked = true;
                control.GetPaintedLineVersionForTests(outerRow)
                    .Should().Be(control.GetBufferLineVersionForTests(outerRow));
                Span<bool> pending = stackalloc bool[control.Host.Rows];
                control.TryCopyPendingPaintRowsForTests(pending).Should().BeTrue();
                pending[otherRow].Should().BeTrue();
            };

            control.Write(Encoding.UTF8.GetBytes("\x1b[4;1Houter-row"));
            Application.DoEvents();
            control.AfterPaintRowForTests = null;
            control.AfterPaintPassForTests = null;

            nested.Should().BeTrue();
            midPassChecked.Should().BeTrue();
            control.GetVisibleText().Should().Contain("outer-row");
            control.GetVisibleText().Should().Contain("other-row-nested");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: nested Write dirties ≥50% of rows during partial paint (forces full invalidate).
    /// Expected: full pending remains at end of outer pass; follow-up paint completes.
    /// </summary>
    [Fact]
    public async Task FullInvalidationDuringPartialPaint_RemainsPending()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            SeedAndPaint(control, lineCount: Math.Max(12, control.Host.Rows));

            var nested = false;
            var midPassChecked = false;
            control.AfterPaintRowForTests = row =>
            {
                if (nested || row != 2)
                {
                    return;
                }

                nested = true;
                // Touch every row so InvalidateDirtyRows promotes to a full invalidate.
                var mass = new StringBuilder();
                for (var r = 1; r <= control.Host.Rows; r++)
                {
                    mass.Append($"\x1b[{r};1H*");
                }

                control.Write(Encoding.UTF8.GetBytes(mass.ToString()));
            };
            control.AfterPaintPassForTests = () =>
            {
                if (!nested || midPassChecked)
                {
                    return;
                }

                midPassChecked = true;
                control.PendingPaintFullForTests.Should().BeTrue();
            };

            control.Write(Encoding.UTF8.GetBytes("\x1b[3;1Hpartial-then-full"));
            Application.DoEvents();
            control.AfterPaintRowForTests = null;
            control.AfterPaintPassForTests = null;

            nested.Should().BeTrue();
            midPassChecked.Should().BeTrue();
            control.GetVisibleText().Should().Contain("*");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: scroll (full invalidate) then an in-place edit.
    /// Expected: scroll is full; subsequent in-place update is partial again.
    /// </summary>
    [Fact]
    public async Task ScrollThenInPlaceUpdate_ReturnsToPartial()
    {
        await StaTask.Run(() =>
        {
            using var form = CreateForm(out var control);
            var cols = Math.Max(40, control.Host.Cols);
            var rows = Math.Max(20, control.Host.Rows);
            var fill = new StringBuilder();
            for (var r = 0; r < rows + 3; r++)
            {
                fill.Append(new string((char)('A' + (r % 26)), cols)).Append("\r\n");
            }

            control.Write(Encoding.UTF8.GetBytes(fill.ToString()));
            control.Refresh();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("after-scroll\r\n"));
            Application.DoEvents();
            control.LastInvalidateWasFullForTests.Should().BeTrue();

            control.Write(Encoding.UTF8.GetBytes("\x1b[6;1Hpartial-again"));
            Application.DoEvents();
            control.LastInvalidateWasFullForTests.Should().BeFalse();
            control.LastPaintedRowCountForTests.Should().BeLessThan(rows / 2);
            control.GetVisibleText().Should().Contain("partial-again");
            form.Close();
        });
    }

    /// <summary>
    /// Scenario: dispose while dirty state exists; recreation paints cleanly.
    /// Expected: disposed control ignores Write; new control renders after seed.
    /// </summary>
    [Fact]
    public async Task DisposeAndRecreation_StartsClean()
    {
        await StaTask.Run(() =>
        {
            using var form = new Form { Width = 900, Height = 600 };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            control.Write(Encoding.UTF8.GetBytes("dispose-me\r\n"));
            Application.DoEvents();
            form.Controls.Remove(control);
            control.Dispose();
            control.Write(Encoding.UTF8.GetBytes("should-ignore"));

            var replacement = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(replacement);
            Application.DoEvents();
            replacement.Write(Encoding.UTF8.GetBytes("recreated-ok\r\n"));
            replacement.Refresh();
            Application.DoEvents();
            replacement.GetVisibleText().Should().Contain("recreated-ok");
            replacement.BrushCacheCountForTests.Should().BeGreaterThanOrEqualTo(0);
            form.Close();
        });
    }

    private static Form CreateForm(out SerialTerminalControl control)
    {
        var form = new Form { Width = 1000, Height = 700 };
        control = new SerialTerminalControl { Dock = DockStyle.Fill };
        form.Controls.Add(control);
        form.Show();
        Application.DoEvents();
        return form;
    }

    private static void SeedAndPaint(SerialTerminalControl control, int lineCount)
    {
        var cols = Math.Max(40, control.Host.Cols);
        var fill = new StringBuilder();
        for (var r = 0; r < lineCount; r++)
        {
            fill.Append(new string((char)('A' + (r % 26)), Math.Min(cols, 40))).Append("\r\n");
        }

        control.Write(Encoding.UTF8.GetBytes(fill.ToString()));
        control.Refresh();
        Application.DoEvents();
    }
}
