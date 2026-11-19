// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SharedRandomJitterProvider.cs
//
// Purpose:
// Supplies [0.0, 1.0) doubles for reconnect backoff jitter via Random.Shared.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Default <see cref="IJitterProvider"/> backed by <see cref="Random.Shared"/>.
/// </summary>
/// <remarks>
/// Thread-safe. Used by <see cref="ReconnectCoordinator"/> to apply symmetric
/// jitter around the current backoff delay. Does not itself compute delays.
/// </remarks>
internal sealed class SharedRandomJitterProvider : IJitterProvider
{
    /// <inheritdoc />
    public double NextDouble() => Random.Shared.NextDouble();
}
