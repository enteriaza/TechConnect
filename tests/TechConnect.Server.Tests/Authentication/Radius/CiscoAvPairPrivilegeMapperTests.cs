// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CiscoAvPairPrivilegeMapperTests.cs
//
// Purpose:
// Regression tests and helpers covering CiscoAvPairPrivilegeMapper behaviour.
// Fixture: CiscoAvPairPrivilegeMapperTests.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Text;
using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for CiscoAvPairPrivilegeMapper.
/// </summary>

public sealed class CiscoAvPairPrivilegeMapperTests
{
    /// <summary>
    /// Field <c>_mapper</c> used by this implementation.
    /// </summary>
    private readonly CiscoAvPairPrivilegeMapper _mapper = new();
    /// <summary>
    /// Verifies Maps Shell Priv Lvl.
    /// </summary>

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    public void Maps_ShellPrivLvl(int level)
    {
        var packet = AcceptWithAvPair($"shell:priv-lvl={level}");
        _mapper.TryMap(packet, out var privilege, out var reason).Should().BeTrue();
        privilege.Value.Should().Be(level);
        reason.Should().BeNull();
    }
    /// <summary>
    /// Verifies Missing Vsa Fails.
    /// </summary>

    [Fact]
    public void MissingVsa_Fails()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessAccept);
        _mapper.TryMap(packet, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("missing");
    }
    /// <summary>
    /// Verifies Filter Id Alone Does Not Grant Privilege.
    /// </summary>

    [Fact]
    public void FilterIdAlone_DoesNotGrantPrivilege()
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessAccept);
        packet.AddAttribute(RadiusAttribute.Create(RadiusAttributeType.FilterId, "techconnect:Administrator"));
        _mapper.TryMap(packet, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("missing");
    }
    /// <summary>
    /// Verifies Missing Shell Priv Lvl Fails.
    /// </summary>

    [Fact]
    public void MissingShellPrivLvl_Fails()
    {
        var packet = AcceptWithAvPair("shell:roles=admin");
        _mapper.TryMap(packet, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("shell:priv-lvl");
    }
    /// <summary>
    /// Verifies Malformed Or Out Of Range Fails.
    /// </summary>

    [Theory]
    [InlineData("shell:priv-lvl=")]
    [InlineData("shell:priv-lvl=abc")]
    [InlineData("shell:priv-lvl=01")]
    [InlineData("shell:priv-lvl=0")]
    [InlineData("shell:priv-lvl=16")]
    public void MalformedOrOutOfRange_Fails(string avPair)
    {
        var packet = AcceptWithAvPair(avPair);
        _mapper.TryMap(packet, out _, out var reason).Should().BeFalse();
        reason.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// Verifies Conflicting Priv Lvl Fails.
    /// </summary>

    [Fact]
    public void ConflictingPrivLvl_Fails()
    {
        var packet = AcceptWithAvPairs("shell:priv-lvl=7", "shell:priv-lvl=15");
        _mapper.TryMap(packet, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("conflicting");
    }
    /// <summary>
    /// Verifies Duplicate Identical Priv Lvl Succeeds.
    /// </summary>

    [Fact]
    public void DuplicateIdenticalPrivLvl_Succeeds()
    {
        var packet = AcceptWithAvPairs("shell:priv-lvl=1", "shell:priv-lvl=1");
        _mapper.TryMap(packet, out var privilege, out _).Should().BeTrue();
        privilege.Should().Be(PrivilegeLevel.ReadOnlyBoundary);
    }
    /// <summary>
    /// Verifies Accept With Av Pair.
    /// </summary>

    private static RadiusPacket AcceptWithAvPair(string avPair) => AcceptWithAvPairs(avPair);
    /// <summary>
    /// Verifies Accept With Av Pairs.
    /// </summary>

    private static RadiusPacket AcceptWithAvPairs(params string[] avPairs)
    {
        var packet = new RadiusPacket(RadiusPacketCode.AccessAccept);
        foreach (var avPair in avPairs)
        {
            packet.AddAttribute(CreateCiscoAvPairAttribute(avPair));
        }

        return packet;
    }
    /// <summary>
    /// Verifies Create Cisco Av Pair Attribute.
    /// </summary>

    internal static RadiusAttribute CreateCiscoAvPairAttribute(string avPair)
    {
        var valueBytes = Encoding.UTF8.GetBytes(avPair);
        var vendorValue = new byte[4 + 2 + valueBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(vendorValue.AsSpan(0, 4), CiscoAvPairPrivilegeMapper.CiscoVendorId);
        vendorValue[4] = CiscoAvPairPrivilegeMapper.CiscoAvPairVendorType;
        vendorValue[5] = (byte)(2 + valueBytes.Length);
        valueBytes.CopyTo(vendorValue.AsSpan(6));
        return RadiusAttribute.Create(RadiusAttributeType.VendorSpecific, vendorValue);
    }
}
