// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    InteractiveLineReader.cs
//
// Purpose:
// Server-side interactive line editor for SSH CLI (echo, history, Tab,?
// help, secret/transient prompts).
//
// -----------------------------------------------------------------------------
using System.Text;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>
/// Server-side interactive line editor for SSH appliance CLI sessions.
/// </summary>
/// <remarks>
/// Echoes printable input, supports basic editing/history/tab completion, and
/// treats CR / LF / CRLF as a single line terminator.
/// Context-sensitive <c>?</c> help is intercepted and never inserted into the buffer.
/// Does not interpret TechConnect binary protocol frames — those travel on the
/// connector path; this reader only handles SSH CLI text bytes.
/// </remarks>
internal sealed class InteractiveLineReader
{
    /// <summary>Default maximum characters accepted for a single line.</summary>
    public const int DefaultMaxLineLength = 1024;

    /// <summary>Default maximum retained history entries.</summary>
    public const int DefaultMaxHistory = 64;

    /// <summary>SSH channel input stream.</summary>
    private readonly Stream _input;

    /// <summary>Callback that writes raw bytes to the SSH channel (serialized by the session).</summary>
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _writeAsync;

    /// <summary>Callback that returns the current prompt text.</summary>
    private readonly Func<string> _getPrompt;

    /// <summary>Tab-completion callback (line, cursor) → discovery result.</summary>
    private readonly Func<string, int, CliDiscoveryResult> _complete;

    /// <summary>Context-help (<c>?</c>) callback (line, cursor) → discovery result.</summary>
    private readonly Func<string, int, CliDiscoveryResult> _contextHelp;

    /// <summary>Callback that returns current terminal columns.</summary>
    private readonly Func<int> _getTerminalColumns;

    /// <summary>Maximum line length.</summary>
    private readonly int _maxLineLength;

    /// <summary>Maximum history entries.</summary>
    private readonly int _maxHistory;

    /// <summary>Bytes pushed back for re-read (escape sequences / CRLF).</summary>
    private readonly Queue<byte> _pending = new();

    /// <summary>Reusable read buffer.</summary>
    private readonly byte[] _readBuffer = new byte[256];

    /// <summary>Command history (newest at end).</summary>
    private readonly List<string> _history = new();

    /// <summary>Current history navigation index, or -1 when editing a new line.</summary>
    private int _historyIndex = -1;

    /// <summary>Draft line saved when entering history navigation.</summary>
    private string _historyDraft = string.Empty;

    /// <summary>When true, a following LF after CR is consumed without treating as a new line.</summary>
    private bool _suppressNextLf;

    /// <summary>
    /// Initializes the interactive line reader.
    /// </summary>
    /// <param name="input">SSH channel input stream.</param>
    /// <param name="writeAsync">Raw write callback.</param>
    /// <param name="getPrompt">Prompt provider.</param>
    /// <param name="complete">Tab completion provider.</param>
    /// <param name="contextHelp">Context help provider.</param>
    /// <param name="getTerminalColumns">Optional terminal width provider.</param>
    /// <param name="maxLineLength">Maximum line length (≥ 1).</param>
    /// <param name="maxHistory">Maximum history entries (≥ 1).</param>
    /// <exception cref="ArgumentNullException">A required callback or stream is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLineLength"/> or <paramref name="maxHistory"/> is less than 1.</exception>
    public InteractiveLineReader(
        Stream input,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> writeAsync,
        Func<string> getPrompt,
        Func<string, int, CliDiscoveryResult> complete,
        Func<string, int, CliDiscoveryResult> contextHelp,
        Func<int>? getTerminalColumns = null,
        int maxLineLength = DefaultMaxLineLength,
        int maxHistory = DefaultMaxHistory)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _writeAsync = writeAsync ?? throw new ArgumentNullException(nameof(writeAsync));
        _getPrompt = getPrompt ?? throw new ArgumentNullException(nameof(getPrompt));
        _complete = complete ?? throw new ArgumentNullException(nameof(complete));
        _contextHelp = contextHelp ?? throw new ArgumentNullException(nameof(contextHelp));
        _getTerminalColumns = getTerminalColumns ?? (() => CliDiscoveryFormatter.DefaultColumns);
        if (maxLineLength < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineLength));
        }

        if (maxHistory < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHistory));
        }

        _maxLineLength = maxLineLength;
        _maxHistory = maxHistory;
    }

    /// <summary>
    /// Read-only view of retained command history entries.
    /// </summary>
    public IReadOnlyList<string> History => _history;

    /// <summary>
    /// Reads a secret line (enable password, etc.): no character echo and not added to history.
    /// Supports Ctrl+C cancel, Ctrl+D exit (empty), Ctrl+U clear, and backspace.
    /// </summary>
    public Task<LineReadResult> ReadSecretLineAsync(CancellationToken cancellationToken) =>
        ReadSpecialLineAsync(echo: false, cancellationToken);

    /// <summary>
    /// Reads a one-shot confirmation line (e.g. SCP host-key yes/no): echoes printable input
    /// but is never stored in command history.
    /// </summary>
    public Task<LineReadResult> ReadTransientLineAsync(CancellationToken cancellationToken) =>
        ReadSpecialLineAsync(echo: true, cancellationToken);

    /// <summary>
    /// Shared implementation for secret and transient line reads.
    /// </summary>
    private async Task<LineReadResult> ReadSpecialLineAsync(bool echo, CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (b is null)
            {
                return LineReadResult.Exit;
            }

            switch (b.Value)
            {
                case 0x03: // Ctrl+C
                    await WriteUtf8Async("^C\r\n", cancellationToken).ConfigureAwait(false);
                    return LineReadResult.Cancelled;

                case 0x04: // Ctrl+D
                    if (buffer.Length == 0)
                    {
                        return LineReadResult.Exit;
                    }

                    continue;

                case 0x15: // Ctrl+U
                    if (echo && buffer.Length > 0)
                    {
                        await WriteUtf8Async(
                                new string('\b', buffer.Length) +
                                new string(' ', buffer.Length) +
                                new string('\b', buffer.Length),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    buffer.Clear();
                    continue;

                case 0x08: // Backspace
                case 0x7F: // DEL
                    if (buffer.Length > 0)
                    {
                        buffer.Length--;
                        if (echo)
                        {
                            await WriteUtf8Async("\b \b", cancellationToken).ConfigureAwait(false);
                        }
                    }

                    continue;

                case (byte)'\r':
                    ConsumeOptionalLf();
                    await WriteUtf8Async("\r\n", cancellationToken).ConfigureAwait(false);
                    return LineReadResult.Line(buffer.ToString());

                case (byte)'\n':
                    if (_suppressNextLf)
                    {
                        _suppressNextLf = false;
                        continue;
                    }

                    await WriteUtf8Async("\r\n", cancellationToken).ConfigureAwait(false);
                    return LineReadResult.Line(buffer.ToString());

                case 0x1B: // Escape — discard CSI without history
                    await DiscardEscapeAsync(cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    if (b.Value >= 0x20 && b.Value <= 0x7E)
                    {
                        if (buffer.Length >= _maxLineLength)
                        {
                            await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        buffer.Append((char)b.Value);
                        if (echo)
                        {
                            await WriteBytesAsync(new[] { b.Value }, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    continue;
            }
        }
    }

    /// <summary>
    /// Discards an unrecognized escape sequence from the input stream.
    /// </summary>
    private async Task DiscardEscapeAsync(CancellationToken cancellationToken)
    {
        var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (next is null || next.Value != (byte)'[')
        {
            return;
        }

        while (true)
        {
            var c = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (c is null)
            {
                return;
            }

            if (c.Value >= (byte)'0' && c.Value <= (byte)'9')
            {
                continue;
            }

            // Final CSI byte (letter or ~).
            return;
        }
    }

    /// <summary>
    /// Reads an interactive command line with echo, editing, history, Tab, and ? help.
    /// </summary>
    public async Task<LineReadResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder();
        var cursor = 0;
        _historyIndex = -1;
        _historyDraft = string.Empty;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var b = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (b is null)
            {
                return LineReadResult.Exit;
            }

            switch (b.Value)
            {
                case 0x03: // Ctrl+C
                    await WriteUtf8Async("^C\r\n", cancellationToken).ConfigureAwait(false);
                    return LineReadResult.Cancelled;

                case 0x04: // Ctrl+D
                    if (buffer.Length == 0)
                    {
                        return LineReadResult.Exit;
                    }

                    if (cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                    }

                    continue;

                case 0x15: // Ctrl+U
                    if (buffer.Length > 0)
                    {
                        buffer.Clear();
                        cursor = 0;
                        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                    }

                    continue;

                case 0x08: // Backspace
                case 0x7F: // DEL (often mapped to backspace by terminals)
                    if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                    }

                    continue;

                case 0x09: // Tab
                    cursor = await ApplyDiscoveryAsync(
                            buffer,
                            cursor,
                            _complete(buffer.ToString(), cursor),
                            cancellationToken)
                        .ConfigureAwait(false);
                    continue;

                case (byte)'?': // Context-sensitive help — never inserted or executed.
                    cursor = await ApplyContextHelpAsync(buffer, cursor, cancellationToken)
                        .ConfigureAwait(false);
                    continue;

                case (byte)'\r':
                    ConsumeOptionalLf();
                    await WriteUtf8Async("\r\n", cancellationToken).ConfigureAwait(false);
                    return FinishLine(buffer.ToString());

                case (byte)'\n':
                    if (_suppressNextLf)
                    {
                        _suppressNextLf = false;
                        continue;
                    }

                    await WriteUtf8Async("\r\n", cancellationToken).ConfigureAwait(false);
                    return FinishLine(buffer.ToString());

                case 0x1B: // Escape — CSI sequences (arrows / Delete)
                    cursor = await HandleEscapeAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                    continue;

                default:
                    if (b.Value >= 0x20 && b.Value <= 0x7E)
                    {
                        if (buffer.Length >= _maxLineLength)
                        {
                            await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        buffer.Insert(cursor, (char)b.Value);
                        cursor++;
                        if (cursor == buffer.Length)
                        {
                            await WriteBytesAsync(new[] { b.Value }, cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    continue;
            }
        }
    }

    /// <summary>
    /// Finalizes a completed line and updates history when appropriate.
    /// </summary>
    private LineReadResult FinishLine(string line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            Remember(line);
        }

        return LineReadResult.Line(line);
    }

    /// <summary>
    /// Adds a non-empty line to history (bounded by max history).
    /// </summary>
    private void Remember(string line)
    {
        // Redact URL credentials before history retention (copy https://user:pass@...).
        line = CopyUrlRedaction.SanitizeCommandLine(line);

        if (_history.Count > 0 &&
            string.Equals(_history[^1], line, StringComparison.Ordinal))
        {
            return;
        }

        _history.Add(line);
        while (_history.Count > _maxHistory)
        {
            _history.RemoveAt(0);
        }
    }

    /// <summary>
    /// Reads one raw byte for exclusive protocol modes (XMODEM/YMODEM).
    /// Must not be interleaved with <see cref="ReadLineAsync"/>.
    /// </summary>
    public Task<byte?> ReadRawByteAsync(CancellationToken cancellationToken) =>
        ReadByteAsync(cancellationToken);

    /// <summary>Writes raw bytes to the SSH terminal (protocol ACK/NAK/handshake).</summary>
    public ValueTask WriteRawBytesAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        WriteBytesAsync(data.ToArray(), cancellationToken);

    /// <summary>
    /// Applies a ? context-help discovery result without inserting into the buffer.
    /// </summary>
    private async Task<int> ApplyContextHelpAsync(
        StringBuilder buffer,
        int cursor,
        CancellationToken cancellationToken)
    {
        var result = _contextHelp(buffer.ToString(), cursor);
        if (result.Kind == CliDiscoveryKind.None || string.IsNullOrEmpty(result.DisplayText))
        {
            await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
            return cursor;
        }

        await WriteUtf8Async("\r\n" + result.DisplayText + "\r\n", cancellationToken).ConfigureAwait(false);
        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
        return cursor;
    }

    /// <summary>
    /// Applies a Tab-completion discovery result to the edit buffer.
    /// </summary>
    private async Task<int> ApplyDiscoveryAsync(
        StringBuilder buffer,
        int cursor,
        CliDiscoveryResult result,
        CancellationToken cancellationToken)
    {
        if (result.Kind == CliDiscoveryKind.None)
        {
            await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
            return cursor;
        }

        if (result.Kind == CliDiscoveryKind.UniqueCompletion &&
            !string.IsNullOrEmpty(result.CompletionText))
        {
            var start = Math.Clamp(result.ReplaceStart, 0, buffer.Length);
            var end = Math.Clamp(result.ReplaceEnd, start, buffer.Length);
            var completion = result.CompletionText;
            if (result.AppendTrailingSpace)
            {
                completion += " ";
            }

            var newLength = buffer.Length - (end - start) + completion.Length;
            if (newLength > _maxLineLength)
            {
                await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
                return cursor;
            }

            buffer.Remove(start, end - start);
            buffer.Insert(start, completion);
            cursor = start + completion.Length;
            await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
            return cursor;
        }

        if (result.Kind is CliDiscoveryKind.AmbiguousCompletion or CliDiscoveryKind.ContextHelp)
        {
            if (string.IsNullOrEmpty(result.DisplayText))
            {
                await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
                return cursor;
            }

            // Ambiguous Tab: extend the token to the longest common prefix when it grows.
            if (result.Kind == CliDiscoveryKind.AmbiguousCompletion &&
                !string.IsNullOrEmpty(result.CompletionText))
            {
                var start = Math.Clamp(result.ReplaceStart, 0, buffer.Length);
                var end = Math.Clamp(result.ReplaceEnd, start, buffer.Length);
                var current = buffer.ToString(start, end - start);
                if (result.CompletionText.Length > current.Length &&
                    result.CompletionText.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                {
                    var newLength = buffer.Length - (end - start) + result.CompletionText.Length;
                    if (newLength <= _maxLineLength)
                    {
                        buffer.Remove(start, end - start);
                        buffer.Insert(start, result.CompletionText);
                        cursor = start + result.CompletionText.Length;
                    }
                }
            }

            await WriteUtf8Async("\r\n" + result.DisplayText + "\r\n", cancellationToken)
                .ConfigureAwait(false);
            await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
            return cursor;
        }

        await WriteUtf8Async("\a", cancellationToken).ConfigureAwait(false);
        return cursor;
    }

    /// <summary>
    /// Handles ANSI/VT escape sequences for cursor and history navigation.
    /// </summary>
    private async Task<int> HandleEscapeAsync(StringBuilder buffer, int cursor, CancellationToken cancellationToken)
    {
        var next = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
        if (next is null)
        {
            return cursor;
        }

        if (next.Value != (byte)'[')
        {
            return cursor;
        }

        var param = new StringBuilder();
        while (true)
        {
            var c = await ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (c is null)
            {
                return cursor;
            }

            if (c.Value >= (byte)'0' && c.Value <= (byte)'9')
            {
                param.Append((char)c.Value);
                continue;
            }

            switch ((char)c.Value)
            {
                case 'A':
                    return await HistoryUpAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                case 'B':
                    return await HistoryDownAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                case 'C':
                    if (cursor < buffer.Length)
                    {
                        cursor++;
                        await WriteUtf8Async("\x1b[C", cancellationToken).ConfigureAwait(false);
                    }

                    return cursor;
                case 'D':
                    if (cursor > 0)
                    {
                        cursor--;
                        await WriteUtf8Async("\x1b[D", cancellationToken).ConfigureAwait(false);
                    }

                    return cursor;
                case '~':
                    if (param.ToString() == "3" && cursor < buffer.Length)
                    {
                        buffer.Remove(cursor, 1);
                        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
                    }

                    return cursor;
                default:
                    return cursor;
            }
        }
    }

    /// <summary>
    /// Moves to the previous history entry and redraws the line.
    /// </summary>
    private async Task<int> HistoryUpAsync(StringBuilder buffer, int cursor, CancellationToken cancellationToken)
    {
        if (_history.Count == 0)
        {
            return cursor;
        }

        if (_historyIndex < 0)
        {
            _historyDraft = buffer.ToString();
            _historyIndex = _history.Count - 1;
        }
        else if (_historyIndex > 0)
        {
            _historyIndex--;
        }
        else
        {
            return cursor;
        }

        buffer.Clear();
        buffer.Append(_history[_historyIndex]);
        cursor = buffer.Length;
        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
        return cursor;
    }

    /// <summary>
    /// Moves to the next history entry (or draft) and redraws the line.
    /// </summary>
    private async Task<int> HistoryDownAsync(StringBuilder buffer, int cursor, CancellationToken cancellationToken)
    {
        if (_historyIndex < 0)
        {
            return cursor;
        }

        if (_historyIndex + 1 >= _history.Count)
        {
            _historyIndex = -1;
            buffer.Clear();
            buffer.Append(_historyDraft);
        }
        else
        {
            _historyIndex++;
            buffer.Clear();
            buffer.Append(_history[_historyIndex]);
        }

        cursor = buffer.Length;
        await RedrawAsync(buffer, cursor, cancellationToken).ConfigureAwait(false);
        return cursor;
    }

    /// <summary>
    /// Clears and redraws the current prompt and edit buffer.
    /// </summary>
    private async Task RedrawAsync(StringBuilder buffer, int cursor, CancellationToken cancellationToken)
    {
        var prompt = _getPrompt();
        var text = buffer.ToString();
        var sb = new StringBuilder(prompt.Length + text.Length + 16);
        sb.Append('\r');
        sb.Append("\x1b[K");
        sb.Append(prompt);
        sb.Append(' ');
        sb.Append(text);
        var back = text.Length - cursor;
        if (back > 0)
        {
            sb.Append("\x1b[");
            sb.Append(back);
            sb.Append('D');
        }

        await WriteUtf8Async(sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Consumes a pending LF after CR so CRLF is a single terminator.
    /// </summary>
    private void ConsumeOptionalLf()
    {
        if (_pending.Count > 0 && _pending.Peek() == (byte)'\n')
        {
            _pending.Dequeue();
            _suppressNextLf = false;
            return;
        }

        _suppressNextLf = true;
    }

    /// <summary>
    /// Reads the next input byte from pending queue or the stream.
    /// </summary>
    private async Task<byte?> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count > 0)
        {
            return _pending.Dequeue();
        }

        var read = await _input.ReadAsync(_readBuffer.AsMemory(0, _readBuffer.Length), cancellationToken)
            .ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        for (var i = 1; i < read; i++)
        {
            _pending.Enqueue(_readBuffer[i]);
        }

        return _readBuffer[0];
    }

    /// <summary>
    /// Encodes text as UTF-8 and writes it to the channel.
    /// </summary>
    private ValueTask WriteUtf8Async(string text, CancellationToken cancellationToken) =>
        WriteBytesAsync(Encoding.UTF8.GetBytes(text), cancellationToken);

    /// <summary>
    /// Writes a byte array to the channel via the write callback.
    /// </summary>
    private ValueTask WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken) =>
        _writeAsync(bytes, cancellationToken);
}

/// <summary>
/// Result of an interactive, secret, or transient line read.
/// </summary>
internal readonly struct LineReadResult
{
    /// <summary>
    /// Outcome discriminating line vs exit vs cancel.
    /// </summary>
    public LineReadOutcome Outcome { get; }

    /// <summary>
    /// Line text when <see cref="Outcome"/> is <see cref="LineReadOutcome.Line"/>; otherwise empty.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// Initializes a line-read result.
    /// </summary>
    /// <param name="outcome">Read outcome.</param>
    /// <param name="text">Line text.</param>
    private LineReadResult(LineReadOutcome outcome, string text)
    {
        Outcome = outcome;
        Text = text;
    }

    /// <summary>
    /// Creates a successful line result.
    /// </summary>
    /// <param name="text">Completed line text.</param>
    /// <returns>Line outcome result.</returns>
    public static LineReadResult Line(string text) => new(LineReadOutcome.Line, text);

    /// <summary>
    /// Result indicating EOF / Ctrl+D exit.
    /// </summary>
    public static LineReadResult Exit => new(LineReadOutcome.Exit, string.Empty);

    /// <summary>
    /// Result indicating Ctrl+C cancel.
    /// </summary>
    public static LineReadResult Cancelled => new(LineReadOutcome.Cancelled, string.Empty);
}

/// <summary>
/// Discriminator for <see cref="LineReadResult"/>.
/// </summary>
internal enum LineReadOutcome
{
    /// <summary>A complete line was read.</summary>
    Line = 0,

    /// <summary>Input closed or empty Ctrl+D exit.</summary>
    Exit = 1,

    /// <summary>Operator cancelled with Ctrl+C.</summary>
    Cancelled = 2
}
