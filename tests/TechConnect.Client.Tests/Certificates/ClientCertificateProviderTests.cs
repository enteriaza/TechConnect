// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    ClientCertificateProviderTests.cs
//
// Purpose:
// Verifies ClientCertificateProvider store lookup, subject/issuer checks, and failure modes against fake stores.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TechConnect.Client.Configuration;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Certificates;

/// <summary>
/// Unit tests for ClientCertificateProvider lookup and validation against fake stores.
/// </summary>
public sealed class ClientCertificateProviderTests
{
    /// <summary>
    /// Creates a provider bound to a fake store and options thumbprint.
    /// </summary>
    private static (
        ClientCertificateProvider Provider,
        FakeCertificateStore Store,
        FakeTimeProvider Time,
        CollectingLogger<ClientCertificateProvider> Logger)
        Create(string? thumbprint, FakeTimeProvider? time = null, StoreLocation location = StoreLocation.CurrentUser)
    {
        var store = new FakeCertificateStore();
        var timeProvider = time ?? new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = new RemoteServiceOptions
        {
            ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = thumbprint ?? string.Empty,
                StoreName = "My",
                StoreLocation = location
            }
        };

        var logger = new CollectingLogger<ClientCertificateProvider>();
        var provider = new ClientCertificateProvider(
            new OptionsMonitorStub<RemoteServiceOptions>(options),
            timeProvider,
            logger,
            new FakeCertificateStoreFactory(store));

        return (provider, store, timeProvider, logger);
    }

    /// <summary>
    /// Scenario: options have empty thumbprint.
    /// Expected: IsConfigured false and GetClientCertificate returns null.
    /// </summary>
    [Fact]
    public void GetClientCertificate_NoThumbprint_ReturnsNull()
    {
        var (provider, _, _, _) = Create(null);
        provider.IsConfigured.Should().BeFalse();
        provider.GetClientCertificate().Should().BeNull();
    }

    /// <summary>
    /// Scenario: store contains a matching cert.
    /// Expected: returned instance is a distinct owned copy of the stored cert.
    /// </summary>
    [Fact]
    public void GetClientCertificate_Found_ReturnsOwnedDistinctInstance()
    {
        var cert = TestCertificateFactory.CreateClientCertificate();
        var (provider, store, _, logger) = Create(cert.Thumbprint);
        store.Add(cert);

        using var result = provider.GetClientCertificate();
        result.Should().NotBeNull();
        result!.Thumbprint.Should().Be(cert.Thumbprint);
        ReferenceEquals(result, cert).Should().BeFalse(because: "provider must return a distinct owned instance");
        result.HasPrivateKey.Should().BeTrue();

        var expectedPrefix = RemoteServiceOptionsValidator.FormatThumbprintPrefix(cert.Thumbprint);
        var lookup = logger.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("Looking up client certificate", StringComparison.Ordinal)).Subject;
        lookup.Message.Should().Contain($"by thumbprint {expectedPrefix}");
        lookup.Message.Should().NotContain("\uFFFD", because: "replacement characters must not appear");
        lookup.Message.Should().NotContain("\u2026", because: "Unicode ellipsis must not be used in thumbprint logs");
        lookup.Message.Should().MatchRegex($"by thumbprint [0-9A-F]{{{expectedPrefix.Length}}}$");
    }

    /// <summary>
    /// Scenario: thumbprint configured but store has no match.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_Missing_Throws()
    {
        var (provider, _, _, _) = Create(new string('A', 40));
        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*not found*");
    }

    /// <summary>
    /// Scenario: malformed thumbprint strings (too short, non-hex, wrong length).
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Theory]
    [InlineData("short")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // 40 chars but non-hex Z is hex... Z is not hex
    [InlineData("0123456789ABCDEFFEDCBA98765432100123456789ABCDEF")] // 48 chars
    public void GetClientCertificate_InvalidThumbprint_Throws(string thumbprint)
    {
        // Ensure non-hex for the Z case — Uri.IsHexDigit / IsAsciiHexDigit reject G-Z.
        var (provider, _, _, _) = Create(thumbprint);
        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*invalid*");
    }

    /// <summary>
    /// Scenario: thumbprint with spaces/colons matches store entry.
    /// Expected: certificate is found after normalization.
    /// </summary>
    [Fact]
    public void GetClientCertificate_NormalizesSpacedColonThumbprint()
    {
        var cert = TestCertificateFactory.CreateClientCertificate();
        var spaced = string.Join(":", Enumerable.Range(0, cert.Thumbprint.Length / 2)
            .Select(i => cert.Thumbprint.Substring(i * 2, 2)));
        var (provider, store, _, logger) = Create(spaced.ToLowerInvariant());
        store.Add(cert);

        using var result = provider.GetClientCertificate();
        result.Should().NotBeNull();
        result!.Thumbprint.Should().BeEquivalentTo(cert.Thumbprint);

        var expectedPrefix = RemoteServiceOptionsValidator.FormatThumbprintPrefix(cert.Thumbprint);
        logger.Entries.Should().Contain(e =>
            e.Message.Contains($"by thumbprint {expectedPrefix}", StringComparison.Ordinal) &&
            e.Message.All(c => c != '\uFFFD' && c != '\u2026'));
    }

    /// <summary>
    /// Scenario: matching cert is expired relative to FakeTimeProvider.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_Expired_Throws()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        var cert = TestCertificateFactory.CreateClientCertificate(
            notBefore: time.GetUtcNow().AddDays(-60),
            notAfter: time.GetUtcNow().AddDays(-1));
        var (provider, store, _, _) = Create(cert.Thumbprint, time);
        store.Add(cert);

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*expired*");
    }

    /// <summary>
    /// Scenario: matching cert NotBefore is in the future.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_NotYetValid_Throws()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));
        var cert = TestCertificateFactory.CreateClientCertificate(
            notBefore: time.GetUtcNow().AddDays(1),
            notAfter: time.GetUtcNow().AddDays(30));
        var (provider, store, _, _) = Create(cert.Thumbprint, time);
        store.Add(cert);

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*not valid yet*");
    }

    /// <summary>
    /// Scenario: matching cert has no private key.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_NoPrivateKey_Throws()
    {
        var cert = TestCertificateFactory.CreateClientCertificate(includePrivateKey: false);
        var (provider, store, _, _) = Create(cert.Thumbprint);
        store.Add(cert);

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*private key*");
    }

    /// <summary>
    /// Scenario: matching cert lacks client Auth EKU.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_WrongEku_Throws()
    {
        var cert = TestCertificateFactory.CreateClientCertificate(includeClientEku: false);
        var (provider, store, _, _) = Create(cert.Thumbprint);
        store.Add(cert);

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*client authentication*");
    }

    /// <summary>
    /// Scenario: store returns multiple thumbprint matches.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_MultipleMatches_Throws()
    {
        var cert = TestCertificateFactory.CreateClientCertificate();
        var (provider, store, _, _) = Create(cert.Thumbprint);
        store.Add(cert);
        store.Add(cert); // same thumbprint twice — store reports multiple matches

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*Multiple*");
    }

    /// <summary>
    /// Scenario: store Open throws.
    /// Expected: GetClientCertificate throws.
    /// </summary>
    [Fact]
    public void GetClientCertificate_InaccessibleStore_Throws()
    {
        var cert = TestCertificateFactory.CreateClientCertificate();
        var (provider, store, _, _) = Create(cert.Thumbprint);
        store.SetOpenException(new System.Security.Cryptography.CryptographicException("access denied"));

        var act = () => provider.GetClientCertificate();
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*Unable to open*");
    }
}
