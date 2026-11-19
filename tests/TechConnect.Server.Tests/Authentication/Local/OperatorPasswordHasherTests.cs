// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    OperatorPasswordHasherTests.cs
//
// Purpose:
// Regression tests and helpers covering OperatorPasswordHasher behaviour.
// Fixture: OperatorPasswordHasherTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Local;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for OperatorPasswordHasher.
/// </summary>

public sealed class OperatorPasswordHasherTests
{
    /// <summary>
    /// Field <c>_hasher</c> used by this implementation.
    /// </summary>
    private readonly OperatorPasswordHasher _hasher = new();
    /// <summary>
    /// Verifies Hash And Verify Succeeds.
    /// </summary>

    [Fact]
    public void HashAndVerify_Succeeds()
    {
        var hash = _hasher.HashPassword("admin", "correct-horse-battery");
        _hasher.VerifyPassword("admin", hash, "correct-horse-battery").Should().BeTrue();
    }
    /// <summary>
    /// Verifies Incorrect Password Is Rejected.
    /// </summary>

    [Fact]
    public void IncorrectPassword_IsRejected()
    {
        var hash = _hasher.HashPassword("admin", "correct-horse-battery");
        _hasher.VerifyPassword("admin", hash, "wrong-password").Should().BeFalse();
    }
    /// <summary>
    /// Verifies Different Salts Produce Different Hashes.
    /// </summary>

    [Fact]
    public void DifferentSalts_ProduceDifferentHashes()
    {
        var a = _hasher.HashPassword("admin", "same-password");
        var b = _hasher.HashPassword("admin", "same-password");
        a.Should().NotBe(b);
        _hasher.VerifyPassword("admin", a, "same-password").Should().BeTrue();
        _hasher.VerifyPassword("admin", b, "same-password").Should().BeTrue();
    }
    /// <summary>
    /// Verifies Malformed Hash Is Rejected Safely.
    /// </summary>

    [Fact]
    public void MalformedHash_IsRejectedSafely()
    {
        _hasher.VerifyPassword("admin", "not-a-hash", "anything").Should().BeFalse();
        _hasher.IsWellFormedHash("not-a-hash").Should().BeFalse();
    }
    /// <summary>
    /// Verifies Generated Hash Is Well Formed Identity V3.
    /// </summary>

    [Fact]
    public void GeneratedHash_IsWellFormed_IdentityV3()
    {
        var hash = _hasher.HashPassword("admin", "correct-horse-battery");
        _hasher.IsWellFormedHash(hash).Should().BeTrue();
        var bytes = Convert.FromBase64String(hash);
        bytes[0].Should().Be(0x01); // Identity V3 marker
    }
}
