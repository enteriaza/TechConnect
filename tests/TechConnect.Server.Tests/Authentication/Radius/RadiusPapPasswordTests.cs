// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusPapPasswordTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusPapPassword behaviour.
// Fixture: RadiusPapPasswordTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Cryptography;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusPapPassword.
/// </summary>

public sealed class RadiusPapPasswordTests
{
    /// <summary>
    /// Verifies Encode Known Vector Single Block.
    /// </summary>
    [Fact]
    public void Encode_KnownVector_SingleBlock()
    {
        var encoded = RadiusPapPassword.Encode(
            "arctangent"u8,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        encoded.Should().Equal(RadiusProtocolReferenceVectors.PapArctangent);
    }
    /// <summary>
    /// Verifies Encode Known Vector Multiple Blocks Pads To32.
    /// </summary>

    [Fact]
    public void Encode_KnownVector_MultipleBlocks_PadsTo32()
    {
        var encoded = RadiusPapPassword.Encode(
            "password-is-long!!"u8,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        encoded.Length.Should().Be(32);
        encoded.Should().Equal(RadiusProtocolReferenceVectors.PapLongPassword);
    }
    /// <summary>
    /// Verifies Encode Rejects Empty Password.
    /// </summary>

    [Fact]
    public void Encode_RejectsEmptyPassword()
    {
        var act = () => RadiusPapPassword.Encode(
            ReadOnlySpan<byte>.Empty,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        act.Should().Throw<ArgumentException>();
    }
    /// <summary>
    /// Verifies Encode Rejects Oversized Password.
    /// </summary>

    [Fact]
    public void Encode_RejectsOversizedPassword()
    {
        var oversized = new byte[RadiusPapPassword.MaxPasswordLength + 1];
        var act = () => RadiusPapPassword.Encode(
            oversized,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        act.Should().Throw<ArgumentException>().WithMessage("*128*");
    }
    /// <summary>
    /// Verifies Encode Max Length Password Succeeds.
    /// </summary>

    [Fact]
    public void Encode_MaxLengthPassword_Succeeds()
    {
        var max = new byte[RadiusPapPassword.MaxPasswordLength];
        max.AsSpan().Fill(0x41);
        var encoded = RadiusPapPassword.Encode(
            max,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        encoded.Length.Should().Be(128);
    }
    /// <summary>
    /// Verifies Encode Rejects Invalid Request Authenticator Length.
    /// </summary>

    [Fact]
    public void Encode_RejectsInvalidRequestAuthenticatorLength()
    {
        var act = () => RadiusPapPassword.Encode(
            "x"u8,
            new byte[15],
            RadiusProtocolReferenceVectors.SharedSecret);

        act.Should().Throw<ArgumentException>();
    }
}
