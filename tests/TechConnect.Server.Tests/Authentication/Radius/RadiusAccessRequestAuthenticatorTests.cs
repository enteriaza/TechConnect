// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusAccessRequestAuthenticatorTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusAccessRequestAuthenticator behaviour.
// Fixture: RadiusAccessRequestAuthenticatorTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusAccessRequestAuthenticator.
/// </summary>

public sealed class RadiusAccessRequestAuthenticatorTests
{
    /// <summary>
    /// Verifies Create Access Request Authenticator Is Exactly16 Bytes And Uses Secure Random.
    /// </summary>
    [Fact]
    public void CreateAccessRequestAuthenticator_IsExactly16Bytes_AndUsesSecureRandom()
    {
        var previous = RadiusAuthenticators.SecureRandom;
        var fixedRandom = new FixedRadiusSecureRandom(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
        try
        {
            RadiusAuthenticators.SecureRandom = fixedRandom;

            var auth = RadiusAuthenticators.CreateAccessRequestAuthenticator();

            auth.Should().HaveCount(16);
            auth.Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
            fixedRandom.CallCount.Should().Be(1);
        }
        finally
        {
            RadiusAuthenticators.SecureRandom = previous;
        }
    }
    /// <summary>
    /// Verifies Create Access Request Authenticator Is Not Derived From Shared Secret.
    /// </summary>

    [Fact]
    public void CreateAccessRequestAuthenticator_IsNotDerivedFromSharedSecret()
    {
        var previous = RadiusAuthenticators.SecureRandom;
        var fixedRandom = new FixedRadiusSecureRandom(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
        try
        {
            RadiusAuthenticators.SecureRandom = fixedRandom;

            var auth = RadiusAuthenticators.CreateAccessRequestAuthenticator();

            // RFC 2865 §4.1: authenticator equals CSPRNG output, not MD5(random||secret).
            var hashedWithSecret = System.Security.Cryptography.MD5.HashData(
                RadiusProtocolReferenceVectors.FixedRequestAuthenticator
                    .Concat(RadiusProtocolReferenceVectors.SharedSecret)
                    .ToArray());

            auth.Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
            auth.Should().NotEqual(hashedWithSecret);
            auth.Should().NotEqual(RadiusProtocolReferenceVectors.SharedSecret.AsSpan().ToArray());
        }
        finally
        {
            RadiusAuthenticators.SecureRandom = previous;
        }
    }
    /// <summary>
    /// Verifies Set Authenticator Writes Distinct Authenticators For Separate Requests.
    /// </summary>

    [Fact]
    public void SetAuthenticator_WritesDistinctAuthenticators_ForSeparateRequests()
    {
        var first = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 1);
        first.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, "a"));
        first.SetAuthenticator(RadiusProtocolReferenceVectors.SharedSecret);

        var second = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 2);
        second.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, "b"));
        second.SetAuthenticator(RadiusProtocolReferenceVectors.SharedSecret);

        first.Authenticator.Should().HaveCount(16);
        second.Authenticator.Should().HaveCount(16);
        // Production path uses SystemRadiusSecureRandom; authenticators must not be deliberately reused.
        first.Authenticator.Should().NotEqual(second.Authenticator);
    }
    /// <summary>
    /// Verifies Access Request Retains Authenticator Unchanged Through Encode And Parse.
    /// </summary>

    [Fact]
    public void AccessRequest_RetainsAuthenticator_UnchangedThroughEncodeAndParse()
    {
        var outbound = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 9);
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, "chris"));
        outbound.SetAccessRequestAuthenticatorForTests(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);

        outbound.RawData.AsSpan(4, 16).ToArray()
            .Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
        outbound.Authenticator.Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);

        var inbound = new RadiusPacket(outbound.RawData);
        inbound.Valid.Should().BeTrue();
        inbound.Authenticator.Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
    }
    /// <summary>
    /// Verifies Pap Encoding Uses Exact Request Authenticator From Access Request.
    /// </summary>

    [Fact]
    public void PapEncoding_UsesExactRequestAuthenticator_FromAccessRequest()
    {
        var request = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 1);
        request.SetAccessRequestAuthenticatorForTests(RadiusProtocolReferenceVectors.RequestAuthenticator);

        var encoded = RadiusPapPassword.Encode(
            "arctangent"u8,
            request.Authenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        encoded.Should().Equal(RadiusProtocolReferenceVectors.PapArctangent);
    }
    /// <summary>
    /// Verifies Default Secure Random Is System Cryptographic Provider.
    /// </summary>

    [Fact]
    public void DefaultSecureRandom_IsSystemCryptographicProvider()
    {
        RadiusAuthenticators.SecureRandom.Should().BeSameAs(SystemRadiusSecureRandom.Instance);
    }
}
