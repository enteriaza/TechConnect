// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    IJitterProvider.cs
//
// Purpose:
// Supplies unit-interval random values for reconnect backoff jitter so tests
// can inject deterministic sequences.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Abstractions;

/// <summary>
/// Supplies unit-interval random values for reconnect jitter.
/// </summary>
/// <remarks>
/// Production uses <see cref="Random.Shared"/>. Implementations must be safe for
/// concurrent calls from a single reconnect loop (and preferably from any thread).
/// </remarks>
internal interface IJitterProvider
{
    /// <summary>
    /// Returns a value in the half-open interval [0, 1).
    /// </summary>
    /// <returns>A pseudo-random double greater than or equal to 0.0 and less than 1.0.</returns>
    double NextDouble();
}
