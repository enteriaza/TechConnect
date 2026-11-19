// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ServerCertificateProviderTests.cs
//
// Purpose:
// Regression tests and helpers covering ServerCertificateProvider behaviour.
// Fixture: ServerCertificateProviderTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Certificates;
/// <summary>
/// xUnit test fixture for ServerCertificateProvider.
/// </summary>

public sealed class ServerCertificateProviderTests
{
    /// <summary>
    /// Verifies Load Required Certificate Returns Valid Server Certificate.
    /// </summary>
    [Fact]
    public void LoadRequiredCertificate_ReturnsValidServerCertificate()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var server = TestCertificateFactory.CreateServerCertificate(ca);
        var store = new FakeCertificateStore();
        store.Add(server);

        var provider = CreateProvider(store, TestCertificateFactory.Thumbprint(server));
        using var loaded = provider.LoadRequiredCertificate();

        loaded.HasPrivateKey.Should().BeTrue();
        TestCertificateFactory.Thumbprint(loaded).Should().Be(TestCertificateFactory.Thumbprint(server));
    }
    /// <summary>
    /// Verifies Load Required Certificate Missing Throws.
    /// </summary>

    [Fact]
    public void LoadRequiredCertificate_Missing_Throws()
    {
        var store = new FakeCertificateStore();
        var provider = CreateProvider(store, new string('A', 40));

        var act = () => provider.LoadRequiredCertificate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*was not found*");
    }
    /// <summary>
    /// Verifies Load Required Certificate Ambiguous Throws.
    /// </summary>

    [Fact]
    public void LoadRequiredCertificate_Ambiguous_Throws()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var server = TestCertificateFactory.CreateServerCertificate(ca);
        var store = new FakeCertificateStore();
        store.Add(server);
        store.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(server));

        var provider = CreateProvider(store, TestCertificateFactory.Thumbprint(server));
        var act = () => provider.LoadRequiredCertificate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Multiple certificates*");
    }
    /// <summary>
    /// Verifies Load Required Certificate Without Private Key Throws.
    /// </summary>

    [Fact]
    public void LoadRequiredCertificate_WithoutPrivateKey_Throws()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var server = TestCertificateFactory.CreateServerCertificate(ca, includePrivateKey: false);
        var store = new FakeCertificateStore();
        store.Add(server);

        var provider = CreateProvider(store, TestCertificateFactory.Thumbprint(server));
        var act = () => provider.LoadRequiredCertificate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*private key*");
    }
    /// <summary>
    /// Verifies Load Required Certificate Expired Throws.
    /// </summary>

    [Fact]
    public void LoadRequiredCertificate_Expired_Throws()
    {
        using var ca = TestCertificateFactory.CreateCa(
            notBefore: DateTimeOffset.UtcNow.AddYears(-2),
            notAfter: DateTimeOffset.UtcNow.AddYears(2));
        using var server = TestCertificateFactory.CreateServerCertificate(
            ca,
            notBefore: DateTimeOffset.UtcNow.AddDays(-40),
            notAfter: DateTimeOffset.UtcNow.AddDays(-1));
        var store = new FakeCertificateStore();
        store.Add(server);

        var provider = CreateProvider(store, TestCertificateFactory.Thumbprint(server));
        var act = () => provider.LoadRequiredCertificate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*expired*");
    }
    /// <summary>
    /// Verifies Load Required Certificate Wrong Eku Throws.
    /// </summary>

    [Fact]
    public void LoadRequiredCertificate_WrongEku_Throws()
    {
        using var ca = TestCertificateFactory.CreateCa();
        using var server = TestCertificateFactory.CreateServerCertificate(ca, includeServerEku: false);
        var store = new FakeCertificateStore();
        store.Add(server);

        var provider = CreateProvider(store, TestCertificateFactory.Thumbprint(server));
        var act = () => provider.LoadRequiredCertificate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*Server Authentication EKU*");
    }
    /// <summary>
    /// Verifies Create Provider.
    /// </summary>

    private static ServerCertificateProvider CreateProvider(FakeCertificateStore store, string thumbprint)
    {
        var options = Options.Create(new TechConnectServerOptions
        {
            Connector = new ConnectorListenerOptions
            {
                Enabled = true,
                ServerCertificateThumbprint = thumbprint,
                ServerCertificateStoreName = "My",
                ServerCertificateStoreLocation = System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser
            }
        });

        return new ServerCertificateProvider(
            options,
            new FakeCertificateStoreFactory(store),
            TimeProvider.System,
            NullLogger<ServerCertificateProvider>.Instance);
    }
}
