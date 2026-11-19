// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialTerminalPaintPerfTests.cs
//
// Purpose:
// STA WinForms paint/write allocation and timing probe for PERF-UI-01.
// Writes results to %TEMP%\TechConnect-PERF-UI-01.txt (not a pass/fail gate).
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Console;

/// <summary>
/// Measurement harness for <see cref="SerialTerminalControl"/> paint/write paths.
/// </summary>
public sealed class SerialTerminalPaintPerfTests
{
    /// <summary>
    /// Scenario: measure paint allocations and durations for representative VT workloads.
    /// Expected: completes and records metrics; does not assert absolute budgets.
    /// </summary>
    [Fact]
    public async Task MeasurePaintAndWrite_RecordsMetrics()
    {
        await StaTask.Run(() =>
        {
            const int cols = 120;
            const int rows = 40;
            const int paints = 80;

            using var form = new Form
            {
                Width = 1100,
                Height = 780,
                Text = "PERF-UI-01 paint probe"
            };
            var control = new SerialTerminalControl { Dock = DockStyle.Fill };
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();

            var sb = new StringBuilder();
            sb.AppendLine($"timestamp_utc={DateTime.UtcNow:O}");
            sb.AppendLine($"runtime={Environment.Version}");
            sb.AppendLine($"os={Environment.OSVersion}");
            sb.AppendLine($"viewport≈{cols}x{rows} paints={paints}");
            sb.AppendLine();

            foreach (var kind in new[] { "ascii", "sgr", "cursor", "utf8" })
            {
                control.ClearTerminal();
                var payload = BuildWorkload(kind, cols, rows);
                var mid = Math.Max(1, payload.Length / 2);
                control.Write(payload.AsSpan(0, mid));
                control.Write(payload.AsSpan(mid));
                Application.DoEvents();

                var paint = MeasurePaint(control, paints);
                sb.AppendLine(
                    $"PAINT kind={kind} avg_ms={paint.AvgMs:F3} p95_ms={paint.P95Ms:F3} " +
                    $"alloc_per_paint={paint.AllocPerPaint} gen0={paint.Gen0}");

                control.ClearTerminal();
                var write = MeasureWrite(control, payload, repeats: 40);
                sb.AppendLine(
                    $"WRITE kind={kind} total_ms={write.TotalMs:F2} alloc={write.Alloc} " +
                    $"thruput_Bps={write.ThroughputBps:F0} payload={payload.Length}");
                sb.AppendLine();
            }

            // Sustained line bursts with dirty invalidation (production-like; no forced full Refresh).
            control.ClearTerminal();
            var line = Encoding.UTF8.GetBytes("boot log line with status OK\r\n");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var a0 = GC.GetTotalAllocatedBytes(precise: true);
            var g0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            var paintCount = 0;
            long paintedRows = 0;
            for (var i = 0; i < 40; i++)
            {
                control.Write(line);
                Application.DoEvents();
                paintedRows += control.LastPaintedRowCountForTests;
                paintCount++;
            }

            sw.Stop();
            var a1 = GC.GetTotalAllocatedBytes(precise: true);
            sb.AppendLine(
                $"SUSTAINED writes=40 paints={paintCount} elapsed_ms={sw.Elapsed.TotalMilliseconds:F1} " +
                $"alloc={a1 - a0} gen0={GC.CollectionCount(0) - g0} avg_rows_painted={paintedRows / (double)paintCount:F2}");

            // Dirty-path workloads: seed full screen, then measure incremental updates.
            SeedFullScreen(control, cols, rows);
            control.Refresh();
            Application.DoEvents();
            var idle = MeasurePaint(control, paints);
            sb.AppendLine(
                $"PAINT kind=idle_no_change avg_ms={idle.AvgMs:F3} p95_ms={idle.P95Ms:F3} " +
                $"alloc_per_paint={idle.AllocPerPaint} gen0={idle.Gen0}");

            SeedFullScreen(control, cols, rows);
            control.Refresh();
            Application.DoEvents();
            var inPlace = MeasureWriteThenPaint(
                control,
                Encoding.UTF8.GetBytes("\x1b[5;1Hin-place-" + new string('#', 16)),
                paints: 40);
            sb.AppendLine(
                $"DIRTY kind=in_place_line avg_ms={inPlace.AvgMs:F3} p95_ms={inPlace.P95Ms:F3} " +
                $"alloc_per={inPlace.AllocPer} avg_rows={inPlace.AvgRows:F2} full_inv_ratio={inPlace.FullInvalidateRatio:F2}");

            SeedFullScreen(control, cols, rows);
            control.Refresh();
            Application.DoEvents();
            var cursorOnly = MeasureWriteThenPaint(
                control,
                Encoding.UTF8.GetBytes("\x1b[1;1H"),
                paints: 40);
            sb.AppendLine(
                $"DIRTY kind=cursor_only avg_ms={cursorOnly.AvgMs:F3} p95_ms={cursorOnly.P95Ms:F3} " +
                $"alloc_per={cursorOnly.AllocPer} avg_rows={cursorOnly.AvgRows:F2} full_inv_ratio={cursorOnly.FullInvalidateRatio:F2}");

            SeedFullScreen(control, cols, rows);
            control.Refresh();
            Application.DoEvents();
            var scrolling = MeasureWriteThenPaint(
                control,
                Encoding.UTF8.GetBytes(new string('x', Math.Min(32, cols)) + "\r\n"),
                paints: 40);
            sb.AppendLine(
                $"DIRTY kind=scroll_line avg_ms={scrolling.AvgMs:F3} p95_ms={scrolling.P95Ms:F3} " +
                $"alloc_per={scrolling.AllocPer} avg_rows={scrolling.AvgRows:F2} full_inv_ratio={scrolling.FullInvalidateRatio:F2}");

            var path = Path.Combine(Path.GetTempPath(), "TechConnect-PERF-UI-01.txt");
            File.WriteAllText(path, sb.ToString());

            // Sanity: control still renders after the probe.
            control.GetVisibleText().Length.Should().BeGreaterThan(0);
            form.Close();
        });
    }

    private static void SeedFullScreen(SerialTerminalControl control, int cols, int rows)
    {
        control.ClearTerminal();
        var sb = new StringBuilder();
        for (var r = 0; r < rows; r++)
        {
            sb.Append(new string((char)('A' + (r % 26)), cols)).Append("\r\n");
        }

        control.Write(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static byte[] BuildWorkload(string kind, int cols, int rows)
    {
        var sb = new StringBuilder();
        switch (kind)
        {
            case "ascii":
                for (var r = 0; r < rows; r++)
                {
                    sb.Append(new string((char)('A' + (r % 26)), cols)).Append("\r\n");
                }

                break;
            case "sgr":
                for (var r = 0; r < rows; r++)
                {
                    sb.Append($"\x1b[{30 + (r % 8)}m");
                    sb.Append(new string((char)('a' + (r % 26)), cols));
                    sb.Append("\x1b[0m\r\n");
                }

                break;
            case "cursor":
                sb.Append("\x1b[2J\x1b[H");
                for (var r = 0; r < rows; r++)
                {
                    sb.Append($"\x1b[{r + 1};1H");
                    sb.Append($"row-{r}-").Append(new string('.', Math.Max(0, cols - 10)));
                }

                break;
            case "utf8":
                for (var r = 0; r < rows; r++)
                {
                    sb.Append(string.Concat(Enumerable.Repeat("caféΔ ", Math.Max(1, cols / 6)))).Append("\r\n");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static (double AvgMs, double P95Ms, long AllocPerPaint, long Gen0) MeasurePaint(
        SerialTerminalControl control,
        int paints)
    {
        for (var i = 0; i < 5; i++)
        {
            control.Refresh();
            Application.DoEvents();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var g0 = GC.CollectionCount(0);
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var times = new double[paints];
        var sw = new Stopwatch();
        for (var i = 0; i < paints; i++)
        {
            sw.Restart();
            control.Refresh();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
            Application.DoEvents();
        }

        var alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        Array.Sort(times);
        return (
            times.Average(),
            times[Math.Max(0, (int)(paints * 0.95) - 1)],
            (alloc1 - alloc0) / paints,
            GC.CollectionCount(0) - g0);
    }

    private static (double TotalMs, long Alloc, double ThroughputBps) MeasureWrite(
        SerialTerminalControl control,
        byte[] payload,
        int repeats)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var a0 = GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < repeats; i++)
        {
            control.Write(payload);
        }

        sw.Stop();
        var a1 = GC.GetTotalAllocatedBytes(precise: true);
        var totalBytes = (long)payload.Length * repeats;
        return (sw.Elapsed.TotalMilliseconds, a1 - a0, totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 1e-9));
    }

    private static (double AvgMs, double P95Ms, long AllocPer, double AvgRows, double FullInvalidateRatio)
        MeasureWriteThenPaint(SerialTerminalControl control, byte[] payload, int paints)
    {
        for (var i = 0; i < 3; i++)
        {
            control.Write(payload);
            Application.DoEvents();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var times = new double[paints];
        long rows = 0;
        var full = 0;
        var sw = new Stopwatch();
        for (var i = 0; i < paints; i++)
        {
            sw.Restart();
            control.Write(payload);
            Application.DoEvents();
            sw.Stop();
            times[i] = sw.Elapsed.TotalMilliseconds;
            if (control.LastInvalidateWasFullForTests)
            {
                full++;
            }

            rows += control.LastPaintedRowCountForTests;
        }

        var alloc1 = GC.GetTotalAllocatedBytes(precise: true);
        Array.Sort(times);
        return (
            times.Average(),
            times[Math.Max(0, (int)(paints * 0.95) - 1)],
            (alloc1 - alloc0) / paints,
            rows / (double)paints,
            full / (double)paints);
    }
}
