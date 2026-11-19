// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    PrivilegeLevel.cs
//
// Purpose:
// Defines the Cisco-style CLI privilege level (1–15) used as the single
// authoritative representation for operator authorization decisions.
//
// -----------------------------------------------------------------------------

using System.Globalization;

namespace TechConnect.Server.Authentication.Models;

/// <summary>
/// Cisco-style CLI privilege level (1–15). Single authoritative representation for authorization.
/// </summary>
/// <remarks>
/// Values outside <see cref="Minimum"/>..<see cref="Maximum"/> are rejected by
/// <see cref="FromInt"/> and <see cref="TryParse"/>. Comparison operators compare
/// the numeric <see cref="Value"/>. The type is immutable and thread-safe.
/// </remarks>
internal readonly struct PrivilegeLevel : IEquatable<PrivilegeLevel>, IComparable<PrivilegeLevel>
{
    /// <summary>
    /// Lowest valid privilege level (inspection / read-only boundary).
    /// </summary>
    public const int Minimum = 1;

    /// <summary>
    /// Highest valid privilege level (privileged EXEC / administrator boundary).
    /// </summary>
    public const int Maximum = 15;

    /// <summary>
    /// Former ReadOnly equivalent — inspection commands only (level 1).
    /// </summary>
    public static PrivilegeLevel ReadOnlyBoundary { get; } = new(1);

    /// <summary>
    /// Former Operator equivalent — connect / attach / show serial / admin disconnect (level 7).
    /// </summary>
    public static PrivilegeLevel OperatorBoundary { get; } = new(7);

    /// <summary>
    /// Former Administrator equivalent — full privilege including disconnect (level 15).
    /// </summary>
    public static PrivilegeLevel AdministratorBoundary { get; } = new(15);

    /// <summary>
    /// Creates a level with a pre-validated numeric value.
    /// </summary>
    /// <param name="value">Privilege integer already known to be in range.</param>
    private PrivilegeLevel(int value) => Value = value;

    /// <summary>
    /// Numeric privilege in the inclusive range <see cref="Minimum"/>..<see cref="Maximum"/>.
    /// </summary>
    public int Value { get; }

    /// <summary>
    /// <see langword="true"/> when <see cref="Value"/> equals <see cref="Maximum"/> (privileged EXEC).
    /// </summary>
    public bool IsPrivilegedExec => Value == Maximum;

    /// <summary>
    /// Returns whether <paramref name="value"/> is in the inclusive valid range.
    /// </summary>
    /// <param name="value">Candidate privilege integer.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is between <see cref="Minimum"/> and <see cref="Maximum"/> inclusive.</returns>
    public static bool IsValid(int value) => value is >= Minimum and <= Maximum;

    /// <summary>
    /// Creates a <see cref="PrivilegeLevel"/> from a validated integer.
    /// </summary>
    /// <param name="value">Privilege integer to wrap.</param>
    /// <returns>A level with <see cref="Value"/> equal to <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="value"/> is outside <see cref="Minimum"/>..<see cref="Maximum"/>.
    /// </exception>
    public static PrivilegeLevel FromInt(int value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                $"Privilege level must be an integer from {Minimum} through {Maximum}.");
        }

        return new PrivilegeLevel(value);
    }

    /// <summary>
    /// Parses a config or RADIUS privilege token.
    /// </summary>
    /// <param name="raw">
    /// Token to parse. Accepts canonical decimal text <c>"1"</c>–<c>"15"</c>
    /// (padded forms such as <c>"01"</c> are rejected) and legacy role names
    /// Administrator / Operator / ReadOnly for migration only.
    /// </param>
    /// <param name="level">Parsed level when the method returns <see langword="true"/>; otherwise <c>default</c>.</param>
    /// <param name="error">Human-readable failure reason when the method returns <see langword="false"/>; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when parsing succeeded; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(string? raw, out PrivilegeLevel level, out string? error)
    {
        level = default;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Privilege level is missing.";
            return false;
        }

        var trimmed = raw.Trim();

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric))
        {
            // Reject padded forms such as "01" — require canonical decimal text.
            if (!string.Equals(trimmed, numeric.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                error = $"Privilege level '{trimmed}' is malformed.";
                return false;
            }

            if (!IsValid(numeric))
            {
                error = $"Privilege level {numeric} is out of range ({Minimum}-{Maximum}).";
                return false;
            }

            level = new PrivilegeLevel(numeric);
            return true;
        }

        if (string.Equals(trimmed, "Administrator", StringComparison.OrdinalIgnoreCase))
        {
            level = AdministratorBoundary;
            return true;
        }

        if (string.Equals(trimmed, "Operator", StringComparison.OrdinalIgnoreCase))
        {
            level = OperatorBoundary;
            return true;
        }

        if (string.Equals(trimmed, "ReadOnly", StringComparison.OrdinalIgnoreCase))
        {
            level = ReadOnlyBoundary;
            return true;
        }

        error =
            $"Privilege level '{trimmed}' is invalid. Expected an integer {Minimum}-{Maximum}, " +
            "or legacy Administrator / Operator / ReadOnly.";
        return false;
    }

    /// <summary>
    /// Parses a privilege token or throws on failure.
    /// </summary>
    /// <param name="raw">Token accepted by <see cref="TryParse"/>.</param>
    /// <returns>The parsed privilege level.</returns>
    /// <exception cref="FormatException">
    /// <paramref name="raw"/> could not be parsed; message is the <see cref="TryParse"/> error text.
    /// </exception>
    public static PrivilegeLevel Parse(string? raw)
    {
        if (!TryParse(raw, out var level, out var error))
        {
            throw new FormatException(error ?? "Invalid privilege level.");
        }

        return level;
    }

    /// <summary>
    /// Compares this level to another by numeric <see cref="Value"/>.
    /// </summary>
    /// <param name="other">Level to compare against.</param>
    /// <returns>A signed comparison result as defined by <see cref="IComparable{T}"/>.</returns>
    public int CompareTo(PrivilegeLevel other) => Value.CompareTo(other.Value);

    /// <summary>
    /// Returns whether two levels have the same <see cref="Value"/>.
    /// </summary>
    /// <param name="other">Level to compare.</param>
    /// <returns><see langword="true"/> when <see cref="Value"/> equals <paramref name="other"/>'s value.</returns>
    public bool Equals(PrivilegeLevel other) => Value == other.Value;

    /// <summary>
    /// Returns whether <paramref name="obj"/> is a <see cref="PrivilegeLevel"/> with the same <see cref="Value"/>.
    /// </summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is an equal privilege level.</returns>
    public override bool Equals(object? obj) => obj is PrivilegeLevel other && Equals(other);

    /// <summary>
    /// Returns a hash code derived from <see cref="Value"/>.
    /// </summary>
    /// <returns>Hash code for this level.</returns>
    public override int GetHashCode() => Value;

    /// <summary>
    /// Returns the invariant-culture decimal text of <see cref="Value"/>.
    /// </summary>
    /// <returns>Canonical privilege string (for example <c>"15"</c>).</returns>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Equality operator comparing <see cref="Value"/>.
    /// </summary>
    public static bool operator ==(PrivilegeLevel left, PrivilegeLevel right) => left.Equals(right);

    /// <summary>
    /// Inequality operator comparing <see cref="Value"/>.
    /// </summary>
    public static bool operator !=(PrivilegeLevel left, PrivilegeLevel right) => !left.Equals(right);

    /// <summary>
    /// Greater-than-or-equal comparison on <see cref="Value"/>.
    /// </summary>
    public static bool operator >=(PrivilegeLevel left, PrivilegeLevel right) => left.Value >= right.Value;

    /// <summary>
    /// Less-than-or-equal comparison on <see cref="Value"/>.
    /// </summary>
    public static bool operator <=(PrivilegeLevel left, PrivilegeLevel right) => left.Value <= right.Value;

    /// <summary>
    /// Greater-than comparison on <see cref="Value"/>.
    /// </summary>
    public static bool operator >(PrivilegeLevel left, PrivilegeLevel right) => left.Value > right.Value;

    /// <summary>
    /// Less-than comparison on <see cref="Value"/>.
    /// </summary>
    public static bool operator <(PrivilegeLevel left, PrivilegeLevel right) => left.Value < right.Value;
}
