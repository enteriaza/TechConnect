// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusAccessRequestFactoryTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusAccessRequestFactory behaviour.
// Fixture: RadiusAccessRequestFactoryTests.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Authentication.Radius;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusAccessRequestFactory.
/// </summary>

public sealed class RadiusAccessRequestFactoryTests
{
    /// <summary>
    /// Field <c>Secret</c> used by this implementation.
    /// </summary>
    private static readonly byte[] Secret = "test-shared-secret"u8.ToArray();
    /// <summary>
    /// Verifies Creates Access Request With Expected Attributes.
    /// </summary>

    [Fact]
    public void Creates_AccessRequest_WithExpectedAttributes()
    {
        var options = new RadiusAuthenticationOptions
        {
            NasIdentifier = "techconnect-lab",
            NasIpAddress = "127.0.0.1"
        };

        var (packetBytes, requestAuthenticator) = RadiusAccessRequestFactory.Create(
            "ops",
            "secret-password",
            options,
            Secret);

        requestAuthenticator.Should().HaveCount(16);
        var packet = new RadiusPacket(packetBytes);
        packet.Valid.Should().BeTrue();
        packet.PacketType.Should().Be(RadiusPacketCode.AccessRequest);
        packet.Authenticator.Should().Equal(requestAuthenticator);

        packet.TryGetAttribute(RadiusAttributeType.UserName, out var user).Should().BeTrue();
        user!.GetStringValue().Should().Be("ops");

        packet.TryGetAttribute(RadiusAttributeType.NasIdentifier, out var nas).Should().BeTrue();
        nas!.GetStringValue().Should().Be("techconnect-lab");

        packet.TryGetAttribute(RadiusAttributeType.NasIpAddress, out _).Should().BeTrue();
        packet.TryGetAttribute(RadiusAttributeType.ServiceType, out _).Should().BeTrue();
        packet.TryGetAttribute(RadiusAttributeType.UserPassword, out var pap).Should().BeTrue();
        pap!.Value.Length.Should().BeGreaterThan(0);
        packet.TryGetAttribute(RadiusAttributeType.MessageAuthenticator, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Pap Encoding Matches Radius Pap Password.
    /// </summary>

    [Fact]
    public void PapEncoding_Matches_RadiusPapPassword()
    {
        var previous = RadiusAuthenticators.SecureRandom;
        try
        {
            var fixedRa = new byte[16];
            RandomNumberGenerator.Fill(fixedRa);
            RadiusAuthenticators.SecureRandom = new FixedRadiusSecureRandom(fixedRa);

            var options = new RadiusAuthenticationOptions { NasIdentifier = "techconnect" };
            var (packetBytes, ra) = RadiusAccessRequestFactory.Create("u", "pw", options, Secret);
            ra.Should().Equal(fixedRa);

            var expected = RadiusPapPassword.Encode(Encoding.UTF8.GetBytes("pw"), ra, Secret);
            var packet = new RadiusPacket(packetBytes);
            packet.TryGetAttribute(RadiusAttributeType.UserPassword, out var pap).Should().BeTrue();
            pap!.Value.ToArray().Should().Equal(expected);
        }
        finally
        {
            RadiusAuthenticators.SecureRandom = previous;
        }
    }
    /// <summary>
    /// Verifies Fresh Request Authenticator Per Attempt.
    /// </summary>

    [Fact]
    public void FreshRequestAuthenticator_PerAttempt()
    {
        var options = new RadiusAuthenticationOptions { NasIdentifier = "techconnect" };
        var (_, a) = RadiusAccessRequestFactory.Create("u", "pw", options, Secret);
        var (_, b) = RadiusAccessRequestFactory.Create("u", "pw", options, Secret);
        a.Should().NotEqual(b);
    }
}
