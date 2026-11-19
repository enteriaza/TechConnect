// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusPacketTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusPacket behaviour.
// Fixture: RadiusPacketTests.
//
// -----------------------------------------------------------------------------

using System.Net;
using FluentAssertions;
using TechConnect.Server.Authentication.Radius.Cryptography;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusPacket.
/// </summary>

public sealed class RadiusPacketTests
{
    /// <summary>
    /// Verifies Construct Access Request Writes Header.
    /// </summary>
    [Fact]
    public void Construct_AccessRequest_WritesHeader()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 42);
        packet.Valid.Should().BeTrue();
        packet.RawData.Length.Should().Be(20);
        packet.RawData[0].Should().Be((byte)RadiusPacketCode.AccessRequest);
        packet.RawData[1].Should().Be(42);
        packet.RawData[2].Should().Be(0);
        packet.RawData[3].Should().Be(20);
    }
    /// <summary>
    /// Verifies Round Trip Multiple Attributes Preserves Values.
    /// </summary>

    [Fact]
    public void RoundTrip_MultipleAttributes_PreservesValues()
    {
        var outbound = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 9);
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.UserName, "chris"));
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.NasIdentifier, "TechConnect"));
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.NasIpAddress, IPAddress.Parse("192.0.2.20")));
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ServiceType, 7)); // NAS-Prompt
        outbound.SetAccessRequestAuthenticatorForTests(
            RadiusProtocolReferenceVectors.FixedRequestAuthenticator);

        var inbound = new RadiusPacket(outbound.RawData);
        inbound.Valid.Should().BeTrue();
        inbound.PacketType.Should().Be(RadiusPacketCode.AccessRequest);
        inbound.Identifier.Should().Be(9);
        inbound.Attributes.Should().HaveCount(4);
        inbound.TryGetAttribute(RadiusAttributeType.UserName, out var user).Should().BeTrue();
        user!.GetStringValue().Should().Be("chris");
        inbound.TryGetAttribute(RadiusAttributeType.FilterId, out _).Should().BeFalse();
        inbound.Authenticator.Should().Equal(RadiusProtocolReferenceVectors.FixedRequestAuthenticator);
    }
    /// <summary>
    /// Verifies Parse Unknown Attribute Is Preserved.
    /// </summary>

    [Fact]
    public void Parse_UnknownAttribute_IsPreserved()
    {
        var outbound = new RadiusPacket(RadiusPacketCode.AccessAccept, identifier: 1);
        outbound.AddAttribute(RadiusAttribute.Create((RadiusAttributeType)200, "opaque"u8.ToArray()));
        outbound.SetAuthenticator(
            RadiusProtocolReferenceVectors.SharedSecret,
            RadiusProtocolReferenceVectors.RequestAuthenticator);

        var inbound = new RadiusPacket(outbound.RawData);
        inbound.Valid.Should().BeTrue();
        inbound.Attributes.Should().ContainSingle(a => a.Type == (RadiusAttributeType)200);
        inbound.Attributes[0].GetStringValue().Should().Be("opaque");
    }
    /// <summary>
    /// Verifies Parse Truncated Header Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_TruncatedHeader_IsInvalid()
    {
        var inbound = new RadiusPacket(new byte[19]);
        inbound.Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Declared Length Below Minimum Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_DeclaredLengthBelowMinimum_IsInvalid()
    {
        var data = new byte[20];
        data[3] = 19;
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Declared Length Exceeds Buffer Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_DeclaredLengthExceedsBuffer_IsInvalid()
    {
        var data = new byte[20];
        data[2] = 0;
        data[3] = 40;
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Declared Length Above4096 Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_DeclaredLengthAbove4096_IsInvalid()
    {
        var data = new byte[50];
        data[2] = 0x10; // 4096
        data[3] = 0x00;
        // buffer shorter than 4096 and length field claims 4096
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Packet Claiming Over4096 Rejected Even If Buffer Huge.
    /// </summary>

    [Fact]
    public void Parse_PacketClaimingOver4096_RejectedEvenIfBufferHuge()
    {
        var data = new byte[4100];
        data[2] = 0x10;
        data[3] = 0x01; // 4097
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Truncated Attribute Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_TruncatedAttribute_IsInvalid()
    {
        var data = new byte[22];
        data[0] = (byte)RadiusPacketCode.AccessAccept;
        data[1] = 1;
        data[3] = 22;
        data[20] = (byte)RadiusAttributeType.UserName;
        data[21] = 10; // claims 10 bytes but only 2 remain
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Attribute Length Below Two Is Invalid.
    /// </summary>

    [Fact]
    public void Parse_AttributeLengthBelowTwo_IsInvalid()
    {
        var data = new byte[22];
        data[0] = (byte)RadiusPacketCode.AccessAccept;
        data[1] = 1;
        data[3] = 22;
        data[20] = (byte)RadiusAttributeType.UserName;
        data[21] = 1;
        new RadiusPacket(data).Valid.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Parse Trailing Udp Padding Is Ignored.
    /// </summary>

    [Fact]
    public void Parse_TrailingUdpPadding_IsIgnored()
    {
        var outbound = new RadiusPacket(RadiusPacketCode.AccessReject, identifier: 3);
        outbound.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, "no"));
        outbound.SetAuthenticator(
            RadiusProtocolReferenceVectors.SharedSecret,
            RadiusProtocolReferenceVectors.RequestAuthenticator);

        var padded = new byte[outbound.RawData.Length + 8];
        outbound.RawData.CopyTo(padded.AsSpan());
        padded.AsSpan(outbound.RawData.Length).Fill(0xAA);

        var inbound = new RadiusPacket(padded);
        inbound.Valid.Should().BeTrue();
        inbound.RawData.Length.Should().Be(outbound.RawData.Length);
        inbound.PacketType.Should().Be(RadiusPacketCode.AccessReject);
    }
    /// <summary>
    /// Verifies Add Attribute Exceeding4096 Throws.
    /// </summary>

    [Fact]
    public void AddAttribute_Exceeding4096_Throws()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 1);
        // Fill close to the limit with large string attributes (253 value bytes each).
        var payload = new string('x', 253);
        while (packet.RawData.Length + 255 <= RadiusPacket.MaximumPacketLength)
        {
            packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, payload));
        }

        var act = () => packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, payload));
        act.Should().Throw<InvalidOperationException>().WithMessage("*4096*");
    }
    /// <summary>
    /// Verifies Empty Attribute Value Is Allowed.
    /// </summary>

    [Fact]
    public void EmptyAttributeValue_IsAllowed()
    {
        var attr = RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, ReadOnlySpan<byte>.Empty);
        attr.Length.Should().Be(2);
        attr.Value.Length.Should().Be(0);

        var packet = new RadiusPacket(RadiusPacketCode.AccessAccept, identifier: 1);
        packet.AddAttribute(attr);
        packet.SetAuthenticator(
            RadiusProtocolReferenceVectors.SharedSecret,
            RadiusProtocolReferenceVectors.RequestAuthenticator);

        var inbound = new RadiusPacket(packet.RawData);
        inbound.Valid.Should().BeTrue();
        inbound.Attributes.Should().ContainSingle();
        inbound.Attributes[0].Value.IsEmpty.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Big Endian Length Is Encoded Correctly When Above255.
    /// </summary>

    [Fact]
    public void BigEndianLength_IsEncodedCorrectly_WhenAbove255()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessRequest, identifier: 1);
        var payload = new string('y', 200);
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, payload));
        // length = 20 + 202 = 222
        packet.RawData[2].Should().Be(0);
        packet.RawData[3].Should().Be(222);

        // Push over 255
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.ReplyMessage, payload));
        var expected = 20 + 202 + 202;
        packet.RawData[2].Should().Be((byte)(expected >> 8));
        packet.RawData[3].Should().Be((byte)(expected & 0xFF));
        expected.Should().BeGreaterThan(255);
    }
    /// <summary>
    /// Verifies Verify Response Requires Valid Authenticator Before Trusting Code.
    /// </summary>

    [Fact]
    public void VerifyResponse_RequiresValidAuthenticator_BeforeTrustingCode()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessAccept, identifier: 1);
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.FilterId, "techconnect:Operator"));
        packet.SetAuthenticator(
            RadiusProtocolReferenceVectors.SharedSecret,
            RadiusProtocolReferenceVectors.RequestAuthenticator);

        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet.RawData,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeTrue();

        // Tamper after signing — must not verify.
        packet.RawData[^1] ^= 0x5A;
        RadiusAuthenticators.VerifyResponseAuthenticator(
                packet.RawData,
                RadiusProtocolReferenceVectors.RequestAuthenticator,
                RadiusProtocolReferenceVectors.SharedSecret)
            .Should().BeFalse();
    }
}
