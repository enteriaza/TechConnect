// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialTerminalControl.cs
//
// Purpose:
// WinForms control that paints VtTerminalHost screen state as a monospaced
// VT100/ANSI serial console (output display only).
//
// -----------------------------------------------------------------------------

using System.ComponentModel;
using System.Drawing.Text;
using System.Text;
using XTerm.Buffer;
using XTerm.Common;

namespace TechConnect.Client.Forms;

/// <summary>
/// Output-only WinForms terminal view backed by <see cref="VtTerminalHost"/>.
/// </summary>
/// <remarks>
/// Does not send keyboard input to the serial port. Scrollback is available via
/// the mouse wheel. Terminal grid size tracks the client area and font metrics.
/// Paint path reuses a <see cref="StringBuilder"/> for attribute runs and caches
/// brushes/pens by ARGB to limit Gen0 pressure under sustained serial output (PERF-UI-01).
/// Dirty-row invalidation uses XTerm.NET <c>BufferLine.Version</c> (no library fork) so
/// unchanged rows are not repainted after small updates (PERF-UI-01 CPU / repaint).
/// </remarks>
internal sealed class SerialTerminalControl : Control
{
    /// <summary>Default Consolas face used for serial console rendering.</summary>
    private static readonly Font DefaultTerminalFont = new("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>
    /// When at least this fraction of rows is dirty, invalidate the full viewport
    /// (avoids many small Invalidate rects under full-screen redraws).
    /// </summary>
    private const double FullInvalidateDirtyFraction = 0.5;

    /// <summary>Headless VT session whose buffer is painted by this control.</summary>
    private readonly VtTerminalHost _host;

    /// <summary>Reusable builder for attribute-run text within a paint pass.</summary>
    private readonly StringBuilder _runBuilder = new(128);

    /// <summary>Cached solid brushes keyed by ARGB (disposed with the control).</summary>
    private readonly Dictionary<int, SolidBrush> _brushCache = new();

    /// <summary>Cached pens keyed by ARGB for underline strokes.</summary>
    private readonly Dictionary<int, Pen> _penCache = new();

    /// <summary>
    /// Soft cap on cached brushes/pens. Truecolor (RGB) attributes can invent many distinct
    /// ARGB values; flushing the cache at this limit prevents unbounded GDI handle growth.
    /// </summary>
    private const int MaxCachedGdiObjects = 384;

    /// <summary>
    /// <c>BufferLine.Version</c> values from the last completed paint, indexed by viewport row.
    /// </summary>
    private long[]? _paintedLineVersions;

    /// <summary>Buffer <c>YDisp</c> captured at last paint.</summary>
    private int _paintedYDisp = int.MinValue;

    /// <summary>Column count captured at last paint.</summary>
    private int _paintedCols;

    /// <summary>Row count captured at last paint.</summary>
    private int _paintedRows;

    /// <summary>Viewport cursor row painted last (-1 when cursor was not drawn).</summary>
    private int _paintedCursorRow = -1;

    /// <summary>Viewport cursor column painted last.</summary>
    private int _paintedCursorCol = -1;

    /// <summary>Whether the cursor was drawn on the last paint.</summary>
    private bool _paintedCursorVisible;

    /// <summary>Reusable scratch flags for dirty-row classification (UI thread only).</summary>
    private bool[]? _dirtyRowsScratch;

    /// <summary>
    /// Rows that must be painted on the next <see cref="OnPaint"/>. Double-buffering often
    /// expands <see cref="PaintEventArgs.ClipRectangle"/> to the full client, so clip alone
    /// cannot limit work — this mask is the source of truth for partial repaints.
    /// </summary>
    private bool[]? _pendingPaintRows;

    /// <summary>
    /// Scratch copy of the pending mask for the in-flight paint plan (UI thread only).
    /// </summary>
    private bool[]? _paintPlanScratch;

    /// <summary>
    /// Per-row <c>BufferLine.Version</c> observed immediately before <see cref="PaintLine"/>
    /// for the in-flight paint (UI thread only). Fingerprints use these values so a nested
    /// update cannot advance the painted version past the contents that were rendered.
    /// </summary>
    private long[]? _paintVersionScratch;

    /// <summary>When true, the next paint renders every viewport row.</summary>
    private bool _pendingPaintFull = true;

    /// <summary>Cached cell width in pixels for the current font.</summary>
    private int _cellWidth = 8;

    /// <summary>Cached cell height in pixels for the current font.</summary>
    private int _cellHeight = 16;

    /// <summary>Cached bold variant of <see cref="Control.Font"/> for attribute runs.</summary>
    private Font? _boldFont;

    /// <summary>True while the control is disposing and must ignore paint/write.</summary>
    private bool _isDisposing;

    /// <summary>
    /// Initializes an empty terminal control with a blank VT host.
    /// </summary>
    public SerialTerminalControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable,
            true);

        TabStop = false;
        BackColor = Color.Black;
        ForeColor = Color.LightGray;
        Font = DefaultTerminalFont;
        _host = new VtTerminalHost(cols: 80, rows: 24);
    }

    /// <summary>
    /// The underlying VT host (parser + screen state).
    /// </summary>
    internal VtTerminalHost Host => _host;

    /// <summary>
    /// Whether any serial output has been written since the last clear.
    /// </summary>
    public bool HasReceivedOutput => _host.HasReceivedOutput;

    /// <summary>
    /// Writes raw serial octets into the VT parser and invalidates the view.
    /// </summary>
    /// <param name="data">Bytes drained from <see cref="SerialConsoleBuffer"/>.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_isDisposing || IsDisposed || data.IsEmpty)
        {
            return;
        }

        var stickToBottom = _host.IsScrolledToBottom;
        _host.Write(data);
        if (stickToBottom)
        {
            _host.ScrollToBottom();
        }

        InvalidateDirtyRows();
    }

    /// <summary>
    /// Resynchronizes the VT parser after display-buffer overflow drops, then writes.
    /// </summary>
    /// <param name="data">Bytes drained from <see cref="SerialConsoleBuffer"/> after overflow.</param>
    /// <remarks>
    /// Does not close the serial port or remote transport. Performs a full terminal
    /// <c>Reset</c> to abandon incomplete UTF-8/VT parser state after overflow drops.
    /// </remarks>
    public void WriteAfterDisplayOverflow(ReadOnlySpan<byte> data)
    {
        if (_isDisposing || IsDisposed)
        {
            return;
        }

        _host.ResyncAfterDisplayOverflow();
        ResetPaintedState();
        Write(data);
    }

    /// <summary>
    /// Clears pending screen/scrollback state and repaints a blank terminal.
    /// </summary>
    public void ClearTerminal()
    {
        if (_isDisposing || IsDisposed)
        {
            return;
        }

        _host.Clear();
        ScheduleFullPaint();
    }

    /// <summary>
    /// Returns the visible viewport text for tests and assertions.
    /// </summary>
    /// <param name="separator">Row separator.</param>
    /// <returns>Visible lines joined by <paramref name="separator"/>.</returns>
    public string GetVisibleText(string separator = "\n")
    {
        if (_isDisposing || IsDisposed)
        {
            return string.Empty;
        }

        return _host.GetVisibleText(separator);
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RecalculateMetricsAndResize();
    }

    /// <inheritdoc />
    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        _boldFont?.Dispose();
        _boldFont = null;
        RecalculateMetricsAndResize();
        ScheduleFullPaint();
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateMetricsAndResize();
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_isDisposing || IsDisposed)
        {
            return;
        }

        // Positive Delta scrolls toward older scrollback (negative ScrollLines).
        var lines = e.Delta > 0 ? -3 : 3;
        _host.ScrollLines(lines);
        // YDisp change remaps every viewport row — full repaint.
        ScheduleFullPaint();
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_isDisposing || IsDisposed)
        {
            return;
        }

        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var terminal = _host.Terminal;
        var buffer = terminal.Buffer;
        var palette = terminal.Colors;
        var defaultFg = ToColor(palette.Foreground);
        var defaultBg = ToColor(palette.Background);
        if (defaultBg.A == 0)
        {
            defaultBg = BackColor;
        }

        if (defaultFg.A == 0)
        {
            defaultFg = ForeColor;
        }

        var backArgb = BackColor.ToArgb();
        var rows = terminal.Rows;
        var cols = terminal.Cols;
        if (rows == 0 || cols == 0)
        {
            g.Clear(BackColor);
            LastPaintedRowCountForTests = 0;
            _pendingPaintFull = true;
            return;
        }

        // Snapshot the paint plan, then clear pending immediately so a nested Write
        // (message pump during TextRenderer/GDI) can re-dirty rows without being lost when
        // this paint finishes (PERF-UI-01 dirty-row lifecycle).
        var paintFull = _pendingPaintFull ||
                        _pendingPaintRows is null ||
                        _pendingPaintRows.Length < rows;
        bool[]? paintPlan = null;
        if (!paintFull)
        {
            var anyPending = false;
            for (var i = 0; i < rows; i++)
            {
                if (_pendingPaintRows![i])
                {
                    anyPending = true;
                    break;
                }
            }

            // External Invalidate/Refresh did not go through InvalidateDirtyRows.
            if (!anyPending)
            {
                paintFull = true;
            }
            else
            {
                if (_paintPlanScratch is null || _paintPlanScratch.Length < rows)
                {
                    _paintPlanScratch = new bool[rows];
                }

                paintPlan = _paintPlanScratch;
                Array.Copy(_pendingPaintRows!, paintPlan, rows);
            }
        }

        _pendingPaintFull = false;
        if (_pendingPaintRows is not null)
        {
            Array.Clear(_pendingPaintRows, 0, Math.Min(_pendingPaintRows.Length, rows));
        }

        // Test hook: runs after pending is cleared so nested Write can schedule a new dirty set.
        BeforePaintRowsForTests?.Invoke();

        var viewportWidth = cols * _cellWidth;
        if (paintFull)
        {
            g.Clear(BackColor);
        }

        if (_paintVersionScratch is null || _paintVersionScratch.Length < rows)
        {
            _paintVersionScratch = new long[rows];
        }

        var paintedRows = 0;
        var backBrush = GetBrush(BackColor);
        for (var row = 0; row < rows; row++)
        {
            if (!paintFull && (paintPlan is null || !paintPlan[row]))
            {
                continue;
            }

            var lineIndex = buffer.YDisp + row;
            if (lineIndex < 0 || lineIndex >= buffer.Lines.Length)
            {
                continue;
            }

            var line = buffer.Lines[lineIndex];
            if (line is null)
            {
                continue;
            }

            // Capture version before rendering so the fingerprint cannot advance past the
            // contents this paint actually draws (nested Write may bump Version mid-paint).
            var versionAtPaint = line.Version;
            _paintVersionScratch[row] = versionAtPaint;

            if (!paintFull)
            {
                // Partial invalidation must erase the row strip; default-bg cells do not fill.
                g.FillRectangle(backBrush, 0, row * _cellHeight, viewportWidth, _cellHeight);
            }

            PaintLine(g, line, row, cols, defaultFg, defaultBg, palette, terminal.ReverseVideo, backArgb);
            AfterPaintRowForTests?.Invoke(row);
            paintedRows++;

            if (line.Version != versionAtPaint)
            {
                // Row advanced while we rendered the older snapshot — keep it dirty.
                EnsurePendingPaintRows(rows);
                _pendingPaintRows![row] = true;
                if (!_pendingPaintFull)
                {
                    Invalidate(new Rectangle(0, row * _cellHeight, viewportWidth, _cellHeight));
                    LastInvalidateWasFullForTests = false;
                }
            }
        }

        var cursorVisible = false;
        var cursorRow = -1;
        var cursorCol = -1;
        if (terminal.CursorVisible && _host.HasReceivedOutput && _host.IsScrolledToBottom)
        {
            // buffer.Y is relative to YBase; convert to viewport row.
            cursorRow = buffer.Y + buffer.YBase - buffer.YDisp;
            cursorCol = buffer.X;
            if (cursorRow >= 0 && cursorRow < rows && cursorCol >= 0 && cursorCol < cols)
            {
                var cursorRect = new Rectangle(
                    cursorCol * _cellWidth,
                    cursorRow * _cellHeight,
                    Math.Max(1, _cellWidth),
                    Math.Max(1, _cellHeight));
                if (paintFull || (paintPlan is not null && paintPlan[cursorRow]))
                {
                    g.FillRectangle(GetBrush(ToColor(palette.Cursor)), cursorRect);
                    cursorVisible = true;
                }
            }
        }

        LastPaintedRowCountForTests = paintedRows;
        CapturePaintedState(
            buffer,
            rows,
            cols,
            cursorVisible,
            cursorRow,
            cursorCol,
            paintFull,
            paintPlan,
            _paintVersionScratch);
        AfterPaintPassForTests?.Invoke();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposing = true;
            BeforePaintRowsForTests = null;
            AfterPaintRowForTests = null;
            AfterPaintPassForTests = null;
            ResetPaintedState();
            _pendingPaintFull = true;
            if (_pendingPaintRows is not null)
            {
                Array.Clear(_pendingPaintRows, 0, _pendingPaintRows.Length);
            }

            _boldFont?.Dispose();
            _boldFont = null;
            DisposeGdiCaches();
            _host.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Returns a cached bold font derived from the control font.
    /// </summary>
    private Font GetBoldFont() => _boldFont ??= new Font(Font, FontStyle.Bold);

    /// <summary>
    /// Returns a cached <see cref="SolidBrush"/> for <paramref name="color"/>.
    /// </summary>
    /// <remarks>
    /// Must only be called from the UI paint path while the control is alive. Returns after
    /// <see cref="_isDisposing"/> is set are prevented by the early exit in <see cref="OnPaint"/>.
    /// </remarks>
    private SolidBrush GetBrush(Color color)
    {
        var key = color.ToArgb();
        if (_brushCache.TryGetValue(key, out var brush))
        {
            return brush;
        }

        if (_brushCache.Count >= MaxCachedGdiObjects)
        {
            DisposeBrushCacheOnly();
        }

        brush = new SolidBrush(color);
        _brushCache[key] = brush;
        return brush;
    }

    /// <summary>
    /// Returns a cached <see cref="Pen"/> for <paramref name="color"/>.
    /// </summary>
    private Pen GetPen(Color color)
    {
        var key = color.ToArgb();
        if (_penCache.TryGetValue(key, out var pen))
        {
            return pen;
        }

        if (_penCache.Count >= MaxCachedGdiObjects)
        {
            DisposePenCacheOnly();
        }

        pen = new Pen(color);
        _penCache[key] = pen;
        return pen;
    }

    /// <summary>
    /// Disposes cached GDI brushes and pens.
    /// </summary>
    private void DisposeGdiCaches()
    {
        DisposeBrushCacheOnly();
        DisposePenCacheOnly();
    }

    /// <summary>
    /// Disposes and clears only the brush cache (used for capacity flush and full dispose).
    /// </summary>
    private void DisposeBrushCacheOnly()
    {
        foreach (var brush in _brushCache.Values)
        {
            brush.Dispose();
        }

        _brushCache.Clear();
    }

    /// <summary>
    /// Disposes and clears only the pen cache (used for capacity flush and full dispose).
    /// </summary>
    private void DisposePenCacheOnly()
    {
        foreach (var pen in _penCache.Values)
        {
            pen.Dispose();
        }

        _penCache.Clear();
    }

    /// <summary>
    /// Brush cache size for tests (capacity and disposal verification).
    /// </summary>
    internal int BrushCacheCountForTests => _brushCache.Count;

    /// <summary>
    /// Pen cache size for tests (capacity and disposal verification).
    /// </summary>
    internal int PenCacheCountForTests => _penCache.Count;

    /// <summary>
    /// Number of viewport rows that executed <see cref="PaintLine"/> on the last paint (tests).
    /// </summary>
    internal int LastPaintedRowCountForTests { get; private set; }

    /// <summary>
    /// Whether the last invalidation scheduled a full-client repaint (tests).
    /// </summary>
    internal bool LastInvalidateWasFullForTests { get; private set; }

    /// <summary>
    /// Optional test callback invoked at the start of row painting, after the pending dirty
    /// mask has been snapshotted and cleared (allows deterministic nested-Write coverage).
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? BeforePaintRowsForTests { get; set; }

    /// <summary>
    /// Optional test callback invoked after each viewport row is painted (before the
    /// post-paint version check), for deterministic version-skew coverage.
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action<int>? AfterPaintRowForTests { get; set; }

    /// <summary>
    /// Optional test callback invoked at the end of <see cref="OnPaint"/> after fingerprints
    /// are updated (before nested Invalidate paints run).
    /// </summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? AfterPaintPassForTests { get; set; }

    /// <summary>
    /// Copies the current pending dirty-row mask for assertions (tests).
    /// </summary>
    /// <param name="destination">Destination length at least the terminal row count.</param>
    /// <returns>True when a partial pending mask is active; false when a full paint is pending.</returns>
    internal bool TryCopyPendingPaintRowsForTests(Span<bool> destination)
    {
        if (_pendingPaintFull || _pendingPaintRows is null)
        {
            return false;
        }

        var rows = Math.Min(destination.Length, _pendingPaintRows.Length);
        for (var i = 0; i < rows; i++)
        {
            destination[i] = _pendingPaintRows[i];
        }

        return true;
    }

    /// <summary>
    /// Painted fingerprint version for a viewport row, or -1 when unavailable (tests).
    /// </summary>
    internal long GetPaintedLineVersionForTests(int row)
    {
        if (_paintedLineVersions is null || row < 0 || row >= _paintedLineVersions.Length)
        {
            return -1;
        }

        return _paintedLineVersions[row];
    }

    /// <summary>
    /// Current buffer line version for a viewport row, or -1 when unavailable (tests).
    /// </summary>
    internal long GetBufferLineVersionForTests(int row)
    {
        if (_isDisposing || IsDisposed)
        {
            return -1;
        }

        var buffer = _host.Terminal.Buffer;
        var lineIndex = buffer.YDisp + row;
        if (lineIndex < 0 || lineIndex >= buffer.Lines.Length)
        {
            return -1;
        }

        return buffer.Lines[lineIndex]?.Version ?? -1;
    }

    /// <summary>
    /// Whether a full paint is currently pending (tests).
    /// </summary>
    internal bool PendingPaintFullForTests => _pendingPaintFull;

    /// <summary>
    /// Invalidates only viewport rows whose <c>BufferLine.Version</c> (or cursor) changed
    /// since the last paint. Falls back to a full invalidate on scroll/resize/large damage.
    /// </summary>
    private void InvalidateDirtyRows()
    {
        var terminal = _host.Terminal;
        var buffer = terminal.Buffer;
        var rows = terminal.Rows;
        var cols = terminal.Cols;

        if (rows <= 0 || cols <= 0 ||
            _paintedLineVersions is null ||
            _paintedYDisp != buffer.YDisp ||
            _paintedRows != rows ||
            _paintedCols != cols)
        {
            ScheduleFullPaint();
            return;
        }

        var dirty = _dirtyRowsScratch;
        if (dirty is null || dirty.Length < rows)
        {
            dirty = new bool[rows];
            _dirtyRowsScratch = dirty;
        }
        else
        {
            Array.Clear(dirty, 0, rows);
        }

        var dirtyCount = 0;
        for (var row = 0; row < rows; row++)
        {
            var lineIndex = buffer.YDisp + row;
            long version = 0;
            if (lineIndex >= 0 && lineIndex < buffer.Lines.Length)
            {
                var line = buffer.Lines[lineIndex];
                if (line is not null)
                {
                    version = line.Version;
                }
            }

            if (version != _paintedLineVersions[row])
            {
                dirty[row] = true;
                dirtyCount++;
            }
        }

        var cursorVisible = terminal.CursorVisible && _host.HasReceivedOutput && _host.IsScrolledToBottom;
        var cursorRow = -1;
        var cursorCol = -1;
        if (cursorVisible)
        {
            cursorRow = buffer.Y + buffer.YBase - buffer.YDisp;
            cursorCol = buffer.X;
            if (cursorRow < 0 || cursorRow >= rows || cursorCol < 0 || cursorCol >= cols)
            {
                cursorVisible = false;
                cursorRow = -1;
                cursorCol = -1;
            }
        }

        if (_paintedCursorVisible && _paintedCursorRow >= 0 && _paintedCursorRow < rows && !dirty[_paintedCursorRow])
        {
            dirty[_paintedCursorRow] = true;
            dirtyCount++;
        }

        if (cursorVisible && cursorRow >= 0 && !dirty[cursorRow])
        {
            dirty[cursorRow] = true;
            dirtyCount++;
        }

        if (dirtyCount == 0)
        {
            LastInvalidateWasFullForTests = false;
            return;
        }

        if (dirtyCount >= Math.Max(1, (int)(rows * FullInvalidateDirtyFraction)))
        {
            ScheduleFullPaint();
            return;
        }

        EnsurePendingPaintRows(rows);
        for (var row = 0; row < rows; row++)
        {
            if (dirty[row])
            {
                _pendingPaintRows![row] = true;
            }
        }

        _pendingPaintFull = false;
        LastInvalidateWasFullForTests = false;
        for (var row = 0; row < rows; row++)
        {
            if (!dirty[row])
            {
                continue;
            }

            Invalidate(new Rectangle(0, row * _cellHeight, cols * _cellWidth, _cellHeight));
        }
    }

    /// <summary>
    /// Schedules a full-viewport repaint and clears the painted fingerprint.
    /// </summary>
    private void ScheduleFullPaint()
    {
        ResetPaintedState();
        _pendingPaintFull = true;
        if (_pendingPaintRows is not null)
        {
            Array.Clear(_pendingPaintRows, 0, _pendingPaintRows.Length);
        }

        Invalidate();
        LastInvalidateWasFullForTests = true;
    }

    /// <summary>
    /// Ensures <see cref="_pendingPaintRows"/> can index <paramref name="rows"/> rows.
    /// </summary>
    private void EnsurePendingPaintRows(int rows)
    {
        if (_pendingPaintRows is null || _pendingPaintRows.Length < rows)
        {
            _pendingPaintRows = new bool[rows];
        }
    }

    /// <summary>
    /// Stores viewport line versions and cursor location for rows that this paint rendered.
    /// Fingerprints use <paramref name="versionsAtPaint"/> (captured before <see cref="PaintLine"/>),
    /// never a newer live buffer version, so nested updates cannot mark unrendered contents painted.
    /// </summary>
    private void CapturePaintedState(
        XTerm.Buffer.TerminalBuffer buffer,
        int rows,
        int cols,
        bool cursorVisible,
        int cursorRow,
        int cursorCol,
        bool paintFull,
        bool[]? paintPlan,
        long[] versionsAtPaint)
    {
        if (_paintedLineVersions is null || _paintedLineVersions.Length < rows)
        {
            var next = new long[rows];
            if (_paintedLineVersions is not null)
            {
                Array.Copy(
                    _paintedLineVersions,
                    next,
                    Math.Min(_paintedLineVersions.Length, rows));
            }

            _paintedLineVersions = next;
        }

        for (var row = 0; row < rows; row++)
        {
            if (!paintFull && (paintPlan is null || !paintPlan[row]))
            {
                continue;
            }

            _paintedLineVersions[row] = versionsAtPaint[row];
        }

        _paintedYDisp = buffer.YDisp;
        _paintedRows = rows;
        _paintedCols = cols;
        _paintedCursorVisible = cursorVisible;
        _paintedCursorRow = cursorVisible ? cursorRow : -1;
        _paintedCursorCol = cursorVisible ? cursorCol : -1;
    }

    /// <summary>
    /// Drops the last-paint fingerprint so the next update performs a full invalidate.
    /// </summary>
    private void ResetPaintedState()
    {
        _paintedLineVersions = null;
        _paintedYDisp = int.MinValue;
        _paintedRows = 0;
        _paintedCols = 0;
        _paintedCursorVisible = false;
        _paintedCursorRow = -1;
        _paintedCursorCol = -1;
    }

    /// <summary>
    /// Paints one terminal row as attribute runs.
    /// </summary>
    private void PaintLine(
        Graphics g,
        BufferLine line,
        int row,
        int cols,
        Color defaultFg,
        Color defaultBg,
        ColorPalette palette,
        bool reverseVideo,
        int backArgb)
    {
        var y = row * _cellHeight;
        var runStart = 0;
        var hasRun = false;
        Color runFg = defaultFg;
        Color runBg = defaultBg;
        var runBold = false;
        var runUnderline = false;
        _runBuilder.Clear();

        void FlushRun(int endCol)
        {
            if (!hasRun || _runBuilder.Length == 0)
            {
                return;
            }

            var x = runStart * _cellWidth;
            var width = Math.Max(_cellWidth, (endCol - runStart) * _cellWidth);
            if (runBg.ToArgb() != backArgb)
            {
                g.FillRectangle(GetBrush(runBg), x, y, width, _cellHeight);
            }

            var font = runBold ? GetBoldFont() : Font;
            // TextRenderer requires a string; one allocation per attribute run (not per cell).
            TextRenderer.DrawText(
                g,
                _runBuilder.ToString(),
                font,
                new Rectangle(x, y, width + _cellWidth, _cellHeight),
                runFg,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping);

            if (runUnderline)
            {
                var underlineY = y + _cellHeight - 2;
                g.DrawLine(GetPen(runFg), x, underlineY, x + (_runBuilder.Length * _cellWidth), underlineY);
            }

            _runBuilder.Clear();
            hasRun = false;
        }

        for (var col = 0; col < cols; col++)
        {
            BufferCell cell = col < line.Length ? line[col] : BufferCell.Empty;
            if (cell.Width == 0)
            {
                // Continuation cell of a wide glyph — skip content but keep column advance.
                continue;
            }

            ResolveColors(cell.Attributes, palette, defaultFg, defaultBg, reverseVideo, out var fg, out var bg);
            var bold = cell.Attributes.IsBold();
            var underline = cell.Attributes.IsUnderline();
            var content = cell.Content;
            var isSingleChar = string.IsNullOrEmpty(content) || content.Length == 1;

            if (!hasRun)
            {
                runStart = col;
                AppendCell(_runBuilder, content);
                runFg = fg;
                runBg = bg;
                runBold = bold;
                runUnderline = underline;
                hasRun = true;
                continue;
            }

            if (fg == runFg && bg == runBg && bold == runBold && underline == runUnderline && isSingleChar)
            {
                AppendCell(_runBuilder, content);
                continue;
            }

            FlushRun(col);
            runStart = col;
            AppendCell(_runBuilder, content);
            runFg = fg;
            runBg = bg;
            runBold = bold;
            runUnderline = underline;
            hasRun = true;
        }

        FlushRun(cols);
    }

    /// <summary>
    /// Appends a cell's display content to <paramref name="builder"/> without allocating a space string.
    /// </summary>
    private static void AppendCell(StringBuilder builder, string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            builder.Append(' ');
            return;
        }

        builder.Append(content);
    }

    /// <summary>
    /// Resolves cell foreground/background colours from XTerm attribute data.
    /// </summary>
    private static void ResolveColors(
        AttributeData attributes,
        ColorPalette palette,
        Color defaultFg,
        Color defaultBg,
        bool reverseVideo,
        out Color fg,
        out Color bg)
    {
        fg = ResolveColor(attributes.GetFgColor(), attributes.GetFgColorKind(), palette, defaultFg);
        bg = ResolveColor(attributes.GetBgColor(), attributes.GetBgColorKind(), palette, defaultBg);

        if (attributes.IsBold() && attributes.GetFgColorKind() == ColorMode.Palette16)
        {
            var index = attributes.GetFgColor();
            if (index is >= 0 and < 8)
            {
                fg = ToColor(palette[index + 8]);
            }
        }

        if (attributes.IsInverse() || reverseVideo)
        {
            (fg, bg) = (bg, fg);
        }

        if (attributes.IsInvisible())
        {
            fg = bg;
        }
    }

    /// <summary>
    /// Maps an XTerm colour id/mode to a WinForms <see cref="Color"/>.
    /// </summary>
    private static Color ResolveColor(
        int color,
        ColorMode kind,
        ColorPalette palette,
        Color fallback)
    {
        return kind switch
        {
            ColorMode.Default => fallback,
            ColorMode.RGB => Color.FromArgb(255, (color >> 16) & 0xFF, (color >> 8) & 0xFF, color & 0xFF),
            ColorMode.Palette16 or ColorMode.Palette256 =>
                color is >= 0 and <= 255 ? ToColor(palette[color]) : fallback,
            _ => fallback
        };
    }

    /// <summary>
    /// Converts an XTerm 0xAARRGGBB / 0x00RRGGBB integer to <see cref="Color"/>.
    /// </summary>
    /// <param name="argb">Packed colour from the XTerm palette.</param>
    /// <returns>Opaque WinForms colour (alpha forced to 255 when source alpha is 0).</returns>
    private static Color ToColor(int argb)
    {
        var a = (argb >> 24) & 0xFF;
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        if (a == 0)
        {
            a = 255;
        }

        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>
    /// Updates cell metrics from the current font and resizes the VT grid.
    /// </summary>
    private void RecalculateMetricsAndResize()
    {
        if (_isDisposing || IsDisposed || !IsHandleCreated)
        {
            return;
        }

        using (var g = CreateGraphics())
        {
            var proposed = TextRenderer.MeasureText(g, "W", Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            _cellWidth = Math.Max(1, proposed.Width);
            _cellHeight = Math.Max(1, Font.Height);
        }

        var cols = Math.Max(VtTerminalHost.MinColumns, ClientSize.Width / _cellWidth);
        var rows = Math.Max(VtTerminalHost.MinRows, ClientSize.Height / _cellHeight);
        var sizeChanged = cols != _host.Cols || rows != _host.Rows;
        _host.Resize(cols, rows);

        // Keep XTerm cell pixel hints in sync for any graphics sequences.
        _host.Terminal.Options.CellWidthPixels = _cellWidth;
        _host.Terminal.Options.CellHeightPixels = _cellHeight;

        if (sizeChanged)
        {
            // Drop stale fingerprints for the previous grid; ResizeRedraw / Invalidate follows.
            ScheduleFullPaint();
        }
    }
}
