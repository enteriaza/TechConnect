// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FakeTimeProvider.cs
//
// Purpose:
// Regression tests and helpers covering FakeTimeProvider behaviour.
// Fixture: FakeTimeProvider.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Tests.Authentication.Radius.Transport;

/// <summary>Minimal handwritten TimeProvider for deterministic quarantine tests.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    /// <summary>
    /// utc Now test helper member.
    /// </summary>
    private DateTimeOffset _utcNow;
    /// <summary>
    /// Creates a FakeTimeProvider test helper.
    /// </summary>

    public FakeTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }
    /// <summary>
    /// Verifies Get Utc Now.
    /// </summary>

    public override DateTimeOffset GetUtcNow() => _utcNow;
    /// <summary>
    /// Verifies Advance.
    /// </summary>

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
