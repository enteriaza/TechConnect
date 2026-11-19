// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusAuthenticatorTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusAuthenticator behaviour.
// Fixture: RadiusAuthenticatorTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusAuthenticator.
/// </summary>

public sealed class RadiusAuthenticatorTests
{
    /// <summary>
    /// Verifies Response Authenticator Empty Accept Matches Independent Vector.
    /// </summary>
    [Fact]
    public void ResponseAuthenticator_EmptyAccept_MatchesIndependentVector()
    {
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.RequestAuthenticator.CopyTo(packet.AsSpan(4));

        var actual = RadiusAuthenticators.ComputeResponseAuthenticator(
            packet,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        actual.Should().Equal(RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty);
    }
    /// <summary>
    /// Verifies Response Authenticator With Filter Id Matches Independent Vector.
    /// </summary>

    [Fact]
    public void ResponseAuthenticator_WithFilterId_MatchesIndependentVector()
    {
        var attr = RadiusProtocolReferenceVectors.FilterIdAttributeWire;
        var packet = new byte[20 + attr.Length];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[2] = (byte)(packet.Length >> 8);
        packet[3] = (byte)(packet.Length & 0xFF);
        RadiusProtocolReferenceVectors.RequestAuthenticator.CopyTo(packet.AsSpan(4));
        attr.CopyTo(packet.AsSpan(20));

        var actual = RadiusAuthenticators.ComputeResponseAuthenticator(
            packet,
            RadiusProtocolReferenceVectors.RequestAuthenticator,
            RadiusProtocolReferenceVectors.SharedSecret);

        actual.Should().Equal(RadiusProtocolReferenceVectors.ResponseAuthenticatorWithFilterId);
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Succeeds For Valid Packet.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_SucceedsForValidPacket()
    {
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty.CopyTo(packet.AsSpan(4));

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeTrue();
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Fails For Wrong Secret.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_FailsForWrongSecret()
    {
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty.CopyTo(packet.AsSpan(4));

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                "wrong-secret"u8)
            .Should().BeFalse();
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Fails For Wrong Request Authenticator.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_FailsForWrongRequestAuthenticator()
    {
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty.CopyTo(packet.AsSpan(4));

        var wrongReq = new byte[16];
        wrongReq.AsSpan().Fill(0x11);

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                wrongReq,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Requires Original Request Authenticator Not ATransformed Copy.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_RequiresOriginalRequestAuthenticator_NotATransformedCopy()
    {
        // Positive: original Request Authenticator accepts the independently computed response.
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty.CopyTo(packet.AsSpan(4));

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeTrue();

        // Negative: a freshly generated / different Request Authenticator must not verify.
        var differentRequestAuth = RadiusAuthenticators.CreateAccessRequestAuthenticator();
        if (differentRequestAuth.AsSpan().SequenceEqual(RadiusProtocolReferenceVectors.RequestAuthenticator))
        {
            differentRequestAuth[0] ^= 0xFF;
        }

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                differentRequestAuth,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Fails When Attributes Tampered.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_FailsWhenAttributesTampered()
    {
        var attr = RadiusProtocolReferenceVectors.FilterIdAttributeWire;
        var packet = new byte[20 + attr.Length];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[2] = (byte)(packet.Length >> 8);
        packet[3] = (byte)(packet.Length & 0xFF);
        RadiusProtocolReferenceVectors.ResponseAuthenticatorWithFilterId.CopyTo(packet.AsSpan(4));
        attr.CopyTo(packet.AsSpan(20));
        packet[^1] ^= 0xFF;

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
    /// <summary>
    /// Verifies Verify Response Authenticator Fails When Authenticator Field Tampered.
    /// </summary>

    [Fact]
    public void VerifyResponseAuthenticator_FailsWhenAuthenticatorFieldTampered()
    {
        var packet = new byte[20];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 20;
        RadiusProtocolReferenceVectors.ResponseAuthenticatorEmpty.CopyTo(packet.AsSpan(4));
        packet[4] ^= 0x01;

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
    /// <summary>
    /// Verifies Accounting Request Authenticator Matches Independent Vector.
    /// </summary>

    [Fact]
    public void AccountingRequestAuthenticator_MatchesIndependentVector()
    {
        var user = RadiusAttribute.Create(RadiusAttributeType.UserName, "alice");
        var packet = new RadiusPacket(RadiusPacketCode.AccountingRequest, identifier: 7);
        packet.AddAttribute(user);
        packet.SetAuthenticator(RadiusProtocolReferenceVectors.SharedSecret);

        packet.Authenticator.Should().Equal(RadiusProtocolReferenceVectors.AccountingRequestAuthenticator);
        RadiusAuthenticators.VerifyAccountingRequestAuthenticator(
                packet.RawData,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeTrue();
    }
    /// <summary>
    /// Verifies Message Authenticator Matches Independent Vector.
    /// </summary>

    [Fact]
    public void MessageAuthenticator_MatchesIndependentVector()
    {
        var packet = new byte[38];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 38;
        RadiusProtocolReferenceVectors.RequestAuthenticator.CopyTo(packet.AsSpan(4));
        packet[20] = (byte)RadiusAttributeType.MessageAuthenticator;
        packet[21] = 18;

        var actual = RadiusMessageAuthenticator.Compute(
            packet,
            RadiusProtocolReferenceVectors.SharedSecret);

        actual.Should().Equal(RadiusProtocolReferenceVectors.MessageAuthenticator);
    }
    /// <summary>
    /// Verifies Message Authenticator Try Verify Succeeds And Fails Closed.
    /// </summary>

    [Fact]
    public void MessageAuthenticator_TryVerify_SucceedsAndFailsClosed()
    {
        var packet = new byte[38];
        packet[0] = (byte)RadiusPacketCode.AccessAccept;
        packet[1] = 1;
        packet[3] = 38;
        RadiusProtocolReferenceVectors.RequestAuthenticator.CopyTo(packet.AsSpan(4));
        packet[20] = (byte)RadiusAttributeType.MessageAuthenticator;
        packet[21] = 18;
        RadiusProtocolReferenceVectors.MessageAuthenticator.CopyTo(packet.AsSpan(22));

        RadiusMessageAuthenticator.TryVerify(packet, RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeTrue();

        packet[22] ^= 0x01;
        RadiusMessageAuthenticator.TryVerify(packet, RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
}
