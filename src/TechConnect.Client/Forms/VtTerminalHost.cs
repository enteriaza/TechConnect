// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    VtTerminalHost.cs
//
// Purpose:
// Headless VT100/ANSI terminal session state backed by XTerm.NET, kept separate
// from WinForms rendering and from the serial transport.
//
// -----------------------------------------------------------------------------

using XTerm;
using XTerm.Common;
using XTerm.Options;

namespace TechConnect.Client.Forms;

/// <summary>
/// Owns an <see cref="Terminal"/> instance that interprets VT100/ANSI sequences from serial RX.
/// </summary>
/// <remarks>
/// Parsing and screen state live here; WinForms painting is handled by
/// <see cref="SerialTerminalControl"/>. Serial transport remains outside this type.
/// </remarks>
internal sealed class VtTerminalHost : IDisposable
{
    /// <summary>Default scrollback lines retained above the visible viewport.</summary>
    public const int DefaultScrollbackLines = 5_000;

    /// <summary>Minimum terminal columns.</summary>
    public const int MinColumns = 1;

    /// <summary>Minimum terminal rows.</summary>
    public const int MinRows = 1;

    /// <summary>Scrollback depth configured at construction (and after clear recreation).</summary>
    private readonly int _scrollbackLines;

    /// <summary>Active XTerm.NET terminal; replaced on <see cref="Clear"/>.</summary>
    private Terminal _terminal;

    /// <summary>True after the first non-empty write since construction or clear.</summary>
    private bool _hasReceivedOutput;

    /// <summary>True after <see cref="Dispose"/>.</summary>
    private bool _disposed;

    /// <summary>
    /// Creates a blank terminal host with the given dimensions and scrollback.
    /// </summary>
    /// <param name="cols">Initial column count (clamped to at least 1).</param>
    /// <param name="rows">Initial row count (clamped to at least 1).</param>
    /// <param name="scrollbackLines">Scrollback line budget (clamped to at least 0).</param>
    public VtTerminalHost(int cols = 80, int rows = 24, int scrollbackLines = DefaultScrollbackLines)
    {
        _scrollbackLines = Math.Max(0, scrollbackLines);
        _terminal = CreateTerminal(Math.Max(MinColumns, cols), Math.Max(MinRows, rows));
    }

    /// <summary>
    /// The live XTerm.NET terminal used for parsing and buffer access.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    public Terminal Terminal
    {
        get
        {
            ThrowIfDisposed();
            return _terminal;
        }
    }

    /// <summary>
    /// Whether any octets have been written since construction or the last <see cref="Clear"/>.
    /// </summary>
    public bool HasReceivedOutput => _hasReceivedOutput;

    /// <summary>
    /// Current column count.
    /// </summary>
    public int Cols => _disposed ? 0 : _terminal.Cols;

    /// <summary>
    /// Current row count.
    /// </summary>
    public int Rows => _disposed ? 0 : _terminal.Rows;

    /// <summary>
    /// Feeds raw serial octets into the VT parser (stateful across calls).
    /// </summary>
    /// <param name="data">UTF-8 / binary serial payload that may contain partial escape sequences.</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        if (data.IsEmpty)
        {
            return;
        }

        _terminal.Write(data);

        if (!_hasReceivedOutput)
        {
            _hasReceivedOutput = true;
            // Show the cursor once real device output has arrived; keep it blank before then.
            _terminal.CursorVisible = true;
        }
    }

    /// <summary>
    /// Resizes the terminal grid to match the display area.
    /// </summary>
    /// <param name="cols">New column count.</param>
    /// <param name="rows">New row count.</param>
    public void Resize(int cols, int rows)
    {
        ThrowIfDisposed();
        cols = Math.Max(MinColumns, cols);
        rows = Math.Max(MinRows, rows);
        if (cols == _terminal.Cols && rows == _terminal.Rows)
        {
            return;
        }

        _terminal.Resize(cols, rows);
    }

    /// <summary>
    /// Scrolls the viewport by the given number of lines (negative = toward older scrollback).
    /// </summary>
    /// <param name="lines">Line delta passed to <see cref="Terminal.ScrollLines"/>.</param>
    public void ScrollLines(int lines)
    {
        ThrowIfDisposed();
        if (lines == 0)
        {
            return;
        }

        _terminal.ScrollLines(lines);
    }

    /// <summary>
    /// Scrolls the viewport to the bottom (live cursor row).
    /// </summary>
    public void ScrollToBottom()
    {
        ThrowIfDisposed();
        _terminal.ScrollToBottom();
    }

    /// <summary>
    /// Returns whether the viewport is pinned to the bottom (live output).
    /// </summary>
    public bool IsScrolledToBottom
    {
        get
        {
            ThrowIfDisposed();
            return _terminal.Buffer.YDisp >= _terminal.Buffer.YBase;
        }
    }

    /// <summary>
    /// Clears screen/scrollback by recreating the terminal at the current size.
    /// </summary>
    public void Clear()
    {
        ThrowIfDisposed();
        var cols = _terminal.Cols;
        var rows = _terminal.Rows;
        _terminal.Dispose();
        _terminal = CreateTerminal(cols, rows);
        _hasReceivedOutput = false;
    }

    /// <summary>
    /// Returns the visible viewport lines joined by <paramref name="separator"/>.
    /// </summary>
    /// <param name="separator">Separator between rows.</param>
    /// <returns>Visible text snapshot for tests and diagnostics.</returns>
    public string GetVisibleText(string separator = "\n")
    {
        ThrowIfDisposed();
        return string.Join(separator, _terminal.GetVisibleLines());
    }

    /// <summary>
    /// Returns a single buffer line translated to a string (including scrollback when indexed).
    /// </summary>
    /// <param name="line">Absolute buffer line index.</param>
    /// <returns>Line text, or empty when out of range.</returns>
    public string GetLine(int line)
    {
        ThrowIfDisposed();
        return _terminal.GetLine(line) ?? string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _terminal.Dispose();
    }

    /// <summary>
    /// Creates a blank XTerm.NET terminal with TechConnect display defaults.
    /// </summary>
    /// <param name="cols">Column count.</param>
    /// <param name="rows">Row count.</param>
    /// <returns>Configured terminal with a hidden cursor and empty screen.</returns>
    private Terminal CreateTerminal(int cols, int rows)
    {
        var terminal = new Terminal(new TerminalOptions
        {
            Cols = cols,
            Rows = rows,
            Scrollback = _scrollbackLines,
            CursorBlink = false,
            CursorStyle = CursorStyle.Block,
            TermName = "vt100",
            ConvertEol = false,
            DrawBoldTextInBrightColors = true,
            Wraparound = true
        });

        // Blank until device output arrives — no placeholder, no visible cursor.
        terminal.CursorVisible = false;
        return terminal;
    }

    /// <summary>
    /// Throws when this host has been disposed.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
