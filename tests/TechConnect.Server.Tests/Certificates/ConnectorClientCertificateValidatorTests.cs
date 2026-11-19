// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorClientCertificateValidatorTests.cs
//
// Purpose:
// Regression tests and helpers covering ConnectorClientCertificateValidator behaviour.
// Fixture: ConnectorClientCertificateValidatorTests.
//
// -----------------------------------------------------------------------------

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Certificates;
/// <summary>
/// xUnit test fixture for ConnectorClientCertificateValidator.
/// </summary>

public sealed class ConnectorClientCertificateValidatorTests
{
    /// <summary>
    /// Verifies Valid Trusted Client Certificate Accepted.
    /// </summary>
    [Fact]
    public void ValidTrustedClientCertificate_Accepted()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var client = TestCertificateFactory.CreateClientCertificate(ca);
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(
            client,
            presentedChain: null,
            SslPolicyErrors.None,
            out var identity,
            out var error);

        ok.Should().BeTrue(error);
        identity.Should().NotBeNull();
        identity!.Thumbprint.Should().Be(TestCertificateFactory.Thumbprint(client));
    }
    /// <summary>
    /// Verifies Missing Client Certificate Rejected.
    /// </summary>

    [Fact]
    public void MissingClientCertificate_Rejected()
    {
        using var ca = TestCertificateFactory.CreateCa();
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(null, null, SslPolicyErrors.RemoteCertificateNotAvailable, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("not provided");
    }
    /// <summary>
    /// Verifies Untrusted Client Certificate Rejected.
    /// </summary>

    [Fact]
    public void UntrustedClientCertificate_Rejected()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var otherCa = TestCertificateFactory.CreateCa("Other CA");
        using var client = TestCertificateFactory.CreateClientCertificate(otherCa);
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(client, null, SslPolicyErrors.RemoteCertificateChainErrors, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// Verifies Expired Client Certificate Rejected.
    /// </summary>

    [Fact]
    public void ExpiredClientCertificate_Rejected()
    {
        using var ca = TestCertificateFactory.CreateCa(
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddYears(2));
        using var client = TestCertificateFactory.CreateClientCertificate(
            ca,
            notBefore: DateTimeOffset.UtcNow.AddDays(-40),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(client, null, SslPolicyErrors.None, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("expired");
    }
    /// <summary>
    /// Verifies Wrong Eku Rejected.
    /// </summary>

    [Fact]
    public void WrongEku_Rejected()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var client = TestCertificateFactory.CreateClientCertificate(ca, includeClientEku: false);
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(client, null, SslPolicyErrors.None, out _, out var error);

        ok.Should().BeFalse();
        error.Should().Contain("client authentication");
    }
    /// <summary>
    /// Verifies Invalid Chain Rejected.
    /// </summary>

    [Fact]
    public void InvalidChain_Rejected()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var selfSigned = TestCertificateFactory.CreateClientCertificate(ca: null);
        var validator = CreateValidator(ca);

        var ok = validator.TryValidate(selfSigned, null, SslPolicyErrors.RemoteCertificateChainErrors, out _, out var error);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// Verifies Create Validator.
    /// </summary>

    private static ConnectorClientCertificateValidator CreateValidator(X509Certificate2 ca)
    {
        var options = Options.Create(new TechConnectServerOptions
        {
            Connector = new ConnectorListenerOptions
            {
                ClientCertificateValidation = new ClientCertificateValidationOptions
                {
                    RequireClientCertificate = true,
                    AllowedCertificateAuthorities = [TestCertificateFactory.Thumbprint(ca)]
                }
            }
        });

        return new ConnectorClientCertificateValidator(
            options,
            new FakeTrustAnchorProvider(ca),
            TimeProvider.System,
            NullLogger<ConnectorClientCertificateValidator>.Instance);
    }
}
