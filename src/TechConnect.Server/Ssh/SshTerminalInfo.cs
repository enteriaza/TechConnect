// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshTerminalInfo.cs
//
// Purpose:
// Holds live PTY term/columns/rows for an SSH shell channel (no OS PTY allocated)
// and applies safe dimension updates from pty-req and window-change.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Ssh;

/// <summary>
/// Live PTY parameters for an SSH shell channel (no OS terminal allocated).
/// </summary>
/// <remarks>
/// Updated by pty-req and window-change; invalid resize values are ignored on
/// update (constructor may fall back to defaults). Dimensions are clamped to 1–1000.
/// Used for CLI discovery column layout and <c>show terminal</c> presence data —
/// not for allocating a real OS PTY.
/// </remarks>
internal sealed class SshTerminalInfo
{
    /// <summary>
    /// Default terminal column count when none is supplied or values are invalid at construction.
    /// </summary>
    public const int DefaultColumns = 80;

    /// <summary>
    /// Default terminal row count when none is supplied or values are invalid at construction.
    /// </summary>
    public const int DefaultRows = 24;

    /// <summary>
    /// Initializes terminal info from an SSH pty-req (or synthetic defaults).
    /// </summary>
    /// <param name="term">TERM type string from the client, or <see langword="null"/>.</param>
    /// <param name="columns">Requested columns; non-positive values fall back to <see cref="DefaultColumns"/>.</param>
    /// <param name="rows">Requested rows; non-positive values fall back to <see cref="DefaultRows"/>.</param>
    public SshTerminalInfo(string? term, int columns, int rows)
    {
        Term = term;
        SetDimensions(columns, rows, allowDefaultFallback: true);
    }

    /// <summary>
    /// TERM type advertised by the SSH client (immutable after construction).
    /// </summary>
    public string? Term { get; }

    /// <summary>
    /// Current column count (clamped).
    /// </summary>
    public int Columns { get; private set; } = DefaultColumns;

    /// <summary>
    /// Current row count (clamped).
    /// </summary>
    public int Rows { get; private set; } = DefaultRows;

    /// <summary>
    /// Attempts to apply a window-change resize.
    /// </summary>
    /// <param name="columns">New column count.</param>
    /// <param name="rows">New row count.</param>
    /// <returns>
    /// <see langword="true"/> when dimensions were updated;
    /// <see langword="false"/> when either value is non-positive (existing dimensions kept).
    /// </returns>
    public bool TryUpdateDimensions(int columns, int rows) =>
        SetDimensions(columns, rows, allowDefaultFallback: false);

    /// <summary>
    /// Applies dimension changes with optional default fallback for construction.
    /// </summary>
    /// <param name="columns">Requested columns.</param>
    /// <param name="rows">Requested rows.</param>
    /// <param name="allowDefaultFallback">
    /// When <see langword="true"/>, non-positive values become defaults;
    /// when <see langword="false"/>, non-positive values leave state unchanged.
    /// </param>
    /// <returns><see langword="true"/> when state changed or defaults applied; otherwise <see langword="false"/>.</returns>
    private bool SetDimensions(int columns, int rows, bool allowDefaultFallback)
    {
        if (columns <= 0 || rows <= 0)
        {
            if (allowDefaultFallback)
            {
                Columns = DefaultColumns;
                Rows = DefaultRows;
                return true;
            }

            return false;
        }

        // Guard absurd values from malformed clients.
        Columns = Math.Clamp(columns, 1, 1000);
        Rows = Math.Clamp(rows, 1, 1000);
        return true;
    }
}
