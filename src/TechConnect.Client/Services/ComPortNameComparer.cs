// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    ComPortNameComparer.cs
//
// Purpose:
// Natural COM port name ordering so COM2 sorts before COM10.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Services;

/// <summary>
/// Natural COM port ordering so COM2 precedes COM10.
/// </summary>
/// <remarks>
/// When both names parse as <c>COMn</c>, comparison is by numeric <c>n</c>;
/// otherwise ordinal ignore-case string comparison is used. Null sorts before
/// non-null. Thread-safe; the singleton <see cref="Instance"/> may be shared.
/// </remarks>
internal sealed class ComPortNameComparer : IComparer<string>
{
    /// <summary>
    /// Shared comparer instance for discovery sorting.
    /// </summary>
    public static ComPortNameComparer Instance { get; } = new();

    /// <inheritdoc />
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (TryParseComNumber(x, out var xNumber) && TryParseComNumber(y, out var yNumber))
        {
            var numberComparison = xNumber.CompareTo(yNumber);
            if (numberComparison != 0)
            {
                return numberComparison;
            }
        }

        return StringComparer.OrdinalIgnoreCase.Compare(x, y);
    }

    /// <summary>
    /// Attempts to parse a trailing integer from a <c>COMn</c>-style name.
    /// </summary>
    /// <param name="portName">Candidate port name.</param>
    /// <param name="number">Parsed numeric suffix when the method returns <see langword="true"/>.</param>
    /// <returns>
    /// <see langword="true"/> when <paramref name="portName"/> starts with <c>COM</c>
    /// (ignore case) and the remainder parses as an integer; otherwise <see langword="false"/>.
    /// </returns>
    internal static bool TryParseComNumber(string portName, out int number)
    {
        number = 0;
        if (portName.Length < 4 ||
            !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(portName.AsSpan(3), out number);
    }
}
