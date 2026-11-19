// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    PrivilegeLevelTests.cs
//
// Purpose:
// Regression tests and helpers covering PrivilegeLevel behaviour.
// Fixture: PrivilegeLevelTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Tests.Authentication.Models;
/// <summary>
/// xUnit test fixture for PrivilegeLevel.
/// </summary>

public sealed class PrivilegeLevelTests
{
    /// <summary>
    /// Verifies From Int Accepts All Valid Levels.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(15)]
    public void FromInt_AcceptsAllValidLevels(int value)
    {
        var level = PrivilegeLevel.FromInt(value);
        level.Value.Should().Be(value);
        PrivilegeLevel.IsValid(value).Should().BeTrue();
    }
    /// <summary>
    /// Verifies From Int Rejects Out Of Range.
    /// </summary>

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(100)]
    public void FromInt_RejectsOutOfRange(int value)
    {
        var act = () => PrivilegeLevel.FromInt(value);
        act.Should().Throw<ArgumentOutOfRangeException>();
        PrivilegeLevel.IsValid(value).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Try Parse Accepts Canonical And Legacy.
    /// </summary>

    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("15", 15)]
    [InlineData("Administrator", 15)]
    [InlineData("Operator", 7)]
    [InlineData("ReadOnly", 1)]
    [InlineData("administrator", 15)]
    public void TryParse_AcceptsCanonicalAndLegacy(string raw, int expected)
    {
        PrivilegeLevel.TryParse(raw, out var level, out var error).Should().BeTrue();
        level.Value.Should().Be(expected);
        error.Should().BeNull();
    }
    /// <summary>
    /// Verifies Try Parse Rejects Missing Malformed And Out Of Range.
    /// </summary>

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("01")]
    [InlineData("1.0")]
    [InlineData("0")]
    [InlineData("16")]
    [InlineData("SuperUser")]
    [InlineData("admin")]
    public void TryParse_RejectsMissingMalformedAndOutOfRange(string? raw)
    {
        PrivilegeLevel.TryParse(raw, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// Verifies Boundary Constants Match Former Roles.
    /// </summary>

    [Fact]
    public void BoundaryConstants_MatchFormerRoles()
    {
        PrivilegeLevel.ReadOnlyBoundary.Value.Should().Be(1);
        PrivilegeLevel.OperatorBoundary.Value.Should().Be(7);
        PrivilegeLevel.AdministratorBoundary.Value.Should().Be(15);
        PrivilegeLevel.AdministratorBoundary.IsPrivilegedExec.Should().BeTrue();
        PrivilegeLevel.OperatorBoundary.IsPrivilegedExec.Should().BeFalse();
    }
}
