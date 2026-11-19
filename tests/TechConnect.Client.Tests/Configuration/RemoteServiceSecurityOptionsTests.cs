// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteServiceSecurityOptionsTests.cs
//
// Purpose:
// Verifies MaximumMessageBytes bounds and CertificateRevocationMode validation
// for RemoteService options (SEC-01 / SEC-02).
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;
using TechConnect.Protocol;

namespace TechConnect.Client.Tests.Configuration;

/// <summary>
/// Configuration validation and mapping tests for message-size and revocation options.
/// </summary>
public sealed class RemoteServiceSecurityOptionsTests
{
    /// <summary>
    /// Scenario: default RemoteServiceOptions.
    /// Expected: 64 KiB maximum message bytes and NoCheck revocation (field default).
    /// </summary>
    [Fact]
    public void Defaults_AreDocumentedFieldSafeValues()
    {
        var options = new RemoteServiceOptions();
        options.MaximumMessageBytes.Should().Be(RemoteServiceOptions.DefaultMaximumMessageBytes);
        options.MaximumMessageBytes.Should().Be(ProtocolLimits.DefaultMaxFramePayloadBytes);
        options.CertificateRevocationMode.Should().Be(X509RevocationMode.NoCheck);
    }

    /// <summary>
    /// Scenario: MaximumMessageBytes at minimum and absolute maximum.
    /// Expected: validator accepts both.
    /// </summary>
    [Fact]
    public void Validator_AcceptsMinimumAndAbsoluteMaximumMessageBytes()
    {
        var validator = new RemoteServiceOptionsValidator();

        validator.Validate(null, new RemoteServiceOptions
        {
            MaximumMessageBytes = RemoteServiceOptions.MinimumMaximumMessageBytes
        }).Succeeded.Should().BeTrue();

        validator.Validate(null, new RemoteServiceOptions
        {
            MaximumMessageBytes = RemoteServiceOptions.AbsoluteMaximumMessageBytes
        }).Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: MaximumMessageBytes below minimum or above absolute maximum.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsMessageBytesOutsideBounds()
    {
        var validator = new RemoteServiceOptionsValidator();

        var below = validator.Validate(null, new RemoteServiceOptions
        {
            MaximumMessageBytes = RemoteServiceOptions.MinimumMaximumMessageBytes - 1
        });
        below.Failed.Should().BeTrue();
        below.Failures.Should().Contain(f => f.Contains("MaximumMessageBytes", StringComparison.Ordinal));

        var above = validator.Validate(null, new RemoteServiceOptions
        {
            MaximumMessageBytes = RemoteServiceOptions.AbsoluteMaximumMessageBytes + 1
        });
        above.Failed.Should().BeTrue();
        above.Failures.Should().Contain(f => f.Contains("MaximumMessageBytes", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: overflow-like MaximumMessageBytes values.
    /// Expected: rejected by the absolute maximum check.
    /// </summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_048_576)]
    [InlineData(RemoteServiceOptions.AbsoluteMaximumMessageBytes + 1)]
    public void Validator_RejectsOverflowLikeMessageBytes(int value)
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            MaximumMessageBytes = value
        });
        result.Failed.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: defined revocation modes.
    /// Expected: accepted.
    /// </summary>
    [Theory]
    [InlineData(X509RevocationMode.NoCheck)]
    [InlineData(X509RevocationMode.Offline)]
    [InlineData(X509RevocationMode.Online)]
    public void Validator_AcceptsDefinedRevocationModes(X509RevocationMode mode)
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            CertificateRevocationMode = mode
        });
        result.Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: undefined CertificateRevocationMode enum value.
    /// Expected: validation failure (no silent fallback).
    /// </summary>
    [Fact]
    public void Validator_RejectsUndefinedRevocationMode()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            CertificateRevocationMode = (X509RevocationMode)999
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f =>
            f.Contains("CertificateRevocationMode", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: build SSL client options for each revocation mode.
    /// Expected: CertificateRevocationCheckMode matches configuration exactly.
    /// </summary>
    [Theory]
    [InlineData(X509RevocationMode.NoCheck)]
    [InlineData(X509RevocationMode.Offline)]
    [InlineData(X509RevocationMode.Online)]
    public void CreateSslClientAuthenticationOptions_AppliesConfiguredRevocationMode(
        X509RevocationMode mode)
    {
        var sslOptions = TcpMtlsRemoteConsoleTransport.CreateSslClientAuthenticationOptions(
            "localhost",
            [],
            mode);

        sslOptions.CertificateRevocationCheckMode.Should().Be(mode);
        sslOptions.TargetHost.Should().Be("localhost");
    }

    /// <summary>
    /// Scenario: map MaximumMessageBytes onto ProtocolLimits.
    /// Expected: frame and serial ceilings equal the configured maximum (no silent raise).
    /// </summary>
    [Theory]
    [InlineData(RemoteServiceOptions.MinimumMaximumMessageBytes)]
    [InlineData(8_192)]
    [InlineData(RemoteServiceOptions.AbsoluteMaximumMessageBytes)]
    public void CreateProtocolLimits_UsesConfiguredMaximumExactly(int maximumMessageBytes)
    {
        var limits = TcpMtlsRemoteConsoleTransport.CreateProtocolLimits(new RemoteServiceOptions
        {
            MaximumMessageBytes = maximumMessageBytes
        });

        limits.MaxFramePayloadBytes.Should().Be(maximumMessageBytes);
        limits.MaxSerialDataPayloadBytes.Should().Be(maximumMessageBytes);
        limits.MaxControlPayloadBytes.Should().Be(
            Math.Min(ProtocolLimits.DefaultMaxControlPayloadBytes, maximumMessageBytes));
        limits.GetMaxPayloadBytes(MessageType.SerialData).Should().Be(maximumMessageBytes);
    }
}
