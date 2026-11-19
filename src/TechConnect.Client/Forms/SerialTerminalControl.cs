// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialTerminalControl.cs
//
// Purpose:
// WinForms control that paints VtTerminalHost screen state as a monospaced
// VT100/ANSI serial console (output display only).
//
// -----------------------------------------------------------------------------

using System.Drawing.Text;
using XTerm.Buffer;
using XTerm.Common;

namespace TechConnect.Client.Forms;

/// <summary>
/// Output-only WinForms terminal view backed by <see cref="VtTerminalHost"/>.
/// </summary>
/// <remarks>
/// Does not send keyboard input to the serial port. Scrollback is available via
/// the mouse wheel. Terminal grid size tracks the client area and font metrics.
/// </remarks>
internal sealed class SerialTerminalControl : Control
{
    /// <summary>Default Consolas face used for serial console rendering.</summary>
    private static readonly Font DefaultTerminalFont = new("Consolas", 10f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Headless VT session whose buffer is painted by this control.</summary>
    private readonly VtTerminalHost _host;

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

        Invalidate();
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
        Invalidate();
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
        Invalidate();
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
        Invalidate();
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
        g.Clear(BackColor);
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

        var rows = terminal.Rows;
        var cols = terminal.Cols;

        for (var row = 0; row < rows; row++)
        {
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

            PaintLine(g, line, row, cols, defaultFg, defaultBg, palette, terminal.ReverseVideo);
        }

        if (terminal.CursorVisible && _host.HasReceivedOutput && _host.IsScrolledToBottom)
        {
            // buffer.Y is relative to YBase; convert to viewport row.
            var cursorRow = buffer.Y + buffer.YBase - buffer.YDisp;
            var cursorCol = buffer.X;
            if (cursorRow >= 0 && cursorRow < rows && cursorCol >= 0 && cursorCol < cols)
            {
                var cursorRect = new Rectangle(
                    cursorCol * _cellWidth,
                    cursorRow * _cellHeight,
                    Math.Max(1, _cellWidth),
                    Math.Max(1, _cellHeight));
                using var brush = new SolidBrush(ToColor(palette.Cursor));
                g.FillRectangle(brush, cursorRect);
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isDisposing = true;
            _boldFont?.Dispose();
            _boldFont = null;
            _host.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Returns a cached bold font derived from the control font.
    /// </summary>
    private Font GetBoldFont() => _boldFont ??= new Font(Font, FontStyle.Bold);

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
        bool reverseVideo)
    {
        var y = row * _cellHeight;
        var runStart = 0;
        string? runText = null;
        Color runFg = defaultFg;
        Color runBg = defaultBg;
        var runBold = false;
        var runUnderline = false;

        void FlushRun(int endCol)
        {
            if (runText is null || runText.Length == 0)
            {
                return;
            }

            var x = runStart * _cellWidth;
            var width = Math.Max(_cellWidth, (endCol - runStart) * _cellWidth);
            if (runBg.ToArgb() != BackColor.ToArgb())
            {
                using var bgBrush = new SolidBrush(runBg);
                g.FillRectangle(bgBrush, x, y, width, _cellHeight);
            }

            var font = runBold ? GetBoldFont() : Font;
            TextRenderer.DrawText(
                g,
                runText,
                font,
                new Rectangle(x, y, width + _cellWidth, _cellHeight),
                runFg,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoClipping);

            if (runUnderline)
            {
                using var pen = new Pen(runFg);
                var underlineY = y + _cellHeight - 2;
                g.DrawLine(pen, x, underlineY, x + (runText.Length * _cellWidth), underlineY);
            }
        }

        for (var col = 0; col < cols; col++)
        {
            BufferCell cell = col < line.Length ? line[col] : BufferCell.Empty;
            if (cell.Width == 0)
            {
                // Continuation cell of a wide glyph — skip content but keep column advance.
                continue;
            }

            var ch = string.IsNullOrEmpty(cell.Content) ? " " : cell.Content;
            ResolveColors(cell.Attributes, palette, defaultFg, defaultBg, reverseVideo, out var fg, out var bg);
            var bold = cell.Attributes.IsBold();
            var underline = cell.Attributes.IsUnderline();

            if (runText is null)
            {
                runStart = col;
                runText = ch;
                runFg = fg;
                runBg = bg;
                runBold = bold;
                runUnderline = underline;
                continue;
            }

            if (fg == runFg && bg == runBg && bold == runBold && underline == runUnderline && ch.Length == 1)
            {
                runText += ch;
                continue;
            }

            FlushRun(col);
            runStart = col;
            runText = ch;
            runFg = fg;
            runBg = bg;
            runBold = bold;
            runUnderline = underline;
        }

        FlushRun(cols);
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
        _host.Resize(cols, rows);

        // Keep XTerm cell pixel hints in sync for any graphics sequences.
        _host.Terminal.Options.CellWidthPixels = _cellWidth;
        _host.Terminal.Options.CellHeightPixels = _cellHeight;
    }
}
