// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialConsoleBuffer.cs
//
// Purpose:
// Batches local serial RX into UTF-8 console text for the WinForms display
// without affecting the remote-forwarded serial byte stream.
//
// -----------------------------------------------------------------------------

using System.Text;
using TechConnect.Client.Models;

namespace TechConnect.Client.Forms;

/// <summary>
/// Batches serial console display as a continuous terminal stream.
/// </summary>
/// <remarks>
/// Does not affect the underlying serial byte stream used for remote forwarding.
/// Diagnostic prefixes/timestamps are never injected into console text.
/// Only <see cref="SerialDataDirection.Rx"/> chunks are enqueued for display.
/// </remarks>
internal sealed class SerialConsoleBuffer
{
    /// <summary>Lock protecting pending text, decoder state, and displayed-character accounting.</summary>
    private readonly object _gate = new();

    /// <summary>Decoded characters waiting to be drained into the UI text box.</summary>
    private readonly StringBuilder _pending = new();

    /// <summary>Persistent UTF-8 decoder that keeps incomplete multibyte sequences across chunks.</summary>
    private readonly Decoder _utf8Decoder;

    /// <summary>Soft cap on characters retained in the on-screen console.</summary>
    private readonly int _maxCharacters;

    /// <summary>Approximate characters currently shown after drains and trims.</summary>
    private int _displayedCharacters;

    /// <summary>Scratch buffer reused under <see cref="_gate"/> to limit per-chunk allocations.</summary>
    private char[] _decodeChars = new char[1024];

    /// <summary>
    /// Initializes a buffer with the specified on-screen character budget.
    /// </summary>
    /// <param name="maxCharacters">
    /// Soft maximum displayed characters; must be at least 4096. When exceeded,
    /// <see cref="TryDrain"/> asks the UI to trim toward three-quarters of this limit.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxCharacters"/> is below 4096.</exception>
    public SerialConsoleBuffer(int maxCharacters = 200_000)
    {
        if (maxCharacters < 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        _maxCharacters = maxCharacters;

        // Persistent UTF-8 decoder keeps incomplete multibyte sequences across reads.
        var encoding = (Encoding)Encoding.UTF8.Clone();
        encoding.DecoderFallback = new DecoderReplacementFallback("·");
        _utf8Decoder = encoding.GetDecoder();
    }

    /// <summary>
    /// Enqueues a serial chunk for display when it is RX data.
    /// </summary>
    /// <param name="chunk">Transferred serial chunk; TX is ignored for console text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="chunk"/> is <see langword="null"/>.</exception>
    public void Enqueue(SerialDataChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        // Local console shows device RX only. TX remains available via stats / Serilog.
        if (chunk.Direction != SerialDataDirection.Rx || chunk.Data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            AppendDecoded(chunk.Data.Span);
        }
    }

    /// <summary>
    /// Clears pending decoded text and resets the stream decoder.
    /// </summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            _pending.Clear();
            _utf8Decoder.Reset();
        }
    }

    /// <summary>
    /// Resets displayed-character accounting after the UI text box has been cleared.
    /// </summary>
    public void NotifyDisplayCleared()
    {
        lock (_gate)
        {
            _displayedCharacters = 0;
        }
    }

    /// <summary>
    /// Drains pending text for appending to the UI, optionally requesting a trim count.
    /// </summary>
    /// <param name="text">Decoded text to append, or empty when nothing is pending.</param>
    /// <param name="charactersToTrim">
    /// Suggested number of leading characters for the UI to remove when over budget; otherwise 0.
    /// </param>
    /// <returns><see langword="true"/> when <paramref name="text"/> contains drained content.</returns>
    public bool TryDrain(out string text, out int charactersToTrim)
    {
        lock (_gate)
        {
            if (_pending.Length == 0)
            {
                text = string.Empty;
                charactersToTrim = 0;
                return false;
            }

            text = _pending.ToString();
            _pending.Clear();
            _displayedCharacters += text.Length;

            charactersToTrim = 0;
            if (_displayedCharacters > _maxCharacters)
            {
                charactersToTrim = _displayedCharacters - (_maxCharacters * 3 / 4);
                _displayedCharacters = Math.Max(0, _displayedCharacters - charactersToTrim);
            }

            return true;
        }
    }

    /// <summary>
    /// Decodes a raw RX span for tests / inspection.
    /// </summary>
    /// <remarks>Uses a throwaway decoder (not the stream state held by this instance).</remarks>
    /// <param name="data">Raw UTF-8 bytes.</param>
    /// <returns>Sanitized display string.</returns>
    internal static string DecodeForDisplay(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var encoding = (Encoding)Encoding.UTF8.Clone();
        encoding.DecoderFallback = new DecoderReplacementFallback("·");
        var charCount = encoding.GetCharCount(data);
        var chars = charCount == 0 ? Array.Empty<char>() : new char[charCount];
        if (charCount > 0)
        {
            encoding.GetChars(data, chars.AsSpan());
        }

        return SanitizeControlCharacters(chars);
    }

    /// <summary>
    /// Decodes <paramref name="data"/> into pending text using the persistent stream decoder.
    /// </summary>
    /// <param name="data">Raw RX bytes for one enqueue.</param>
    private void AppendDecoded(ReadOnlySpan<byte> data)
    {
        var maxChars = Encoding.UTF8.GetMaxCharCount(data.Length);
        if (_decodeChars.Length < maxChars)
        {
            _decodeChars = new char[Math.Max(maxChars, _decodeChars.Length * 2)];
        }

        _utf8Decoder.Convert(
            data,
            _decodeChars.AsSpan(),
            flush: false,
            out _,
            out var charsUsed,
            out _);

        if (charsUsed == 0)
        {
            return;
        }

        var sanitized = SanitizeControlCharacters(_decodeChars.AsSpan(0, charsUsed));
        if (sanitized.Length > 0)
        {
            _pending.Append(sanitized);
        }
    }

    /// <summary>
    /// Preserves CR/LF/TAB and printable text; replaces other C0 controls with a midpoint.
    /// </summary>
    /// <remarks>Does not invent newlines.</remarks>
    /// <param name="chars">Decoded characters to sanitize.</param>
    /// <returns>Display-safe string.</returns>
    private static string SanitizeControlCharacters(ReadOnlySpan<char> chars)
    {
        if (chars.IsEmpty)
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var replacement = MapControl(c);
            if (replacement is null)
            {
                if (builder is not null)
                {
                    builder.Append(c);
                }

                continue;
            }

            builder ??= new StringBuilder(chars.Length).Append(chars[..i]);
            builder.Append(replacement.Value);
        }

        return builder?.ToString() ?? new string(chars);
    }

    /// <summary>
    /// Maps a control character to a replacement, or <see langword="null"/> to keep the original.
    /// </summary>
    /// <param name="c">Character to evaluate.</param>
    /// <returns>Replacement midpoint for disallowed controls; otherwise <see langword="null"/>.</returns>
    private static char? MapControl(char c)
    {
        if (c is '\r' or '\n' or '\t')
        {
            return null;
        }

        if (c < 0x20 || c == 0x7F)
        {
            return '·';
        }

        return null;
    }
}
