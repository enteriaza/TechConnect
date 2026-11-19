// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteEndpointAndOptionsTests.cs
//
// Purpose:
// Verifies RemoteEndpoint Host/Port resolution and RemoteService options validation.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Configuration;

/// <summary>
/// Unit tests for RemoteEndpoint Host/Port helpers and RemoteService options validation.
/// </summary>
public sealed class RemoteEndpointAndOptionsTests
{
    /// <summary>
    /// Scenario: IsConfigured checks Host/Port non-placeholder.
    /// Expected: true only for valid host and port.
    /// </summary>
    [Fact]
    public void IsConfigured_RequiresHostPortNonPlaceholder()
    {
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions()).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "example.com", Port = 9443 }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "127.0.0.1", Port = 0 }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "127.0.0.1", Port = 9443 }).Should().BeTrue();
    }

    /// <summary>
    /// Scenario: ResolveTlsHostName prefers TlsHostName then Host.
    /// Expected: trimmed values; missing host throws.
    /// </summary>
    [Fact]
    public void ResolveTlsHostName_UsesTlsHostOrHost()
    {
        RemoteEndpoint.ResolveTlsHostName(new RemoteServiceOptions
        {
            Host = "127.0.0.1",
            TlsHostName = "localhost"
        }).Should().Be("localhost");

        RemoteEndpoint.ResolveTlsHostName(new RemoteServiceOptions
        {
            Host = "remote.contoso.test"
        }).Should().Be("remote.contoso.test");

        var act = () => RemoteEndpoint.ResolveTlsHostName(new RemoteServiceOptions());
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*not configured*");
    }

    /// <summary>
    /// Scenario: fully valid RemoteServiceOptions.
    /// Expected: validator succeeds.
    /// </summary>
    [Fact]
    public void Validator_AcceptsValidOptions()
    {
        var validator = new RemoteServiceOptionsValidator();
        var result = validator.Validate(null, new RemoteServiceOptions
        {
            Host = "remote.contoso.test",
            Port = 9443,
            TlsHostName = "remote.contoso.test",
            ConnectTimeoutSeconds = 30,
            ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = new string('A', 40),
                StoreName = "My",
                StoreLocation = System.Security.Cryptography.X509Certificates.StoreLocation.CurrentUser
            }
        });

        result.Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Scenario: placeholder Host.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsPlaceholderHost()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            Host = "api.example.com",
            Port = 9443
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: invalid reconnect/buffer numeric options.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsBadReconnectAndBuffer()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            Reconnect = new ReconnectOptions
            {
                InitialDelaySeconds = -1,
                MaxDelaySeconds = 0,
                BackoffMultiplier = 0.5,
                JitterFactor = 2
            },
            Buffer = new RemoteBufferOptions { MaximumQueuedBytes = 100 }
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("InitialDelaySeconds", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("MaxDelaySeconds", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("BackoffMultiplier", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("JitterFactor", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("MaximumQueuedBytes", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: malformed thumbprint or store settings.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsInvalidThumbprintAndStore()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            ClientCertificate = new ClientCertificateOptions
            {
                Thumbprint = "not-a-thumbprint",
                StoreName = "NotARealStore",
                StoreLocation = (System.Security.Cryptography.X509Certificates.StoreLocation)999
            }
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("Thumbprint", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("StoreName", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("StoreLocation", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: thumbprint with spaces/colons/lowercase.
    /// Expected: normalized uppercase hex without separators.
    /// </summary>
    [Fact]
    public void NormalizeThumbprint_StripsSeparatorsAndUppercases()
    {
        RemoteServiceOptionsValidator.NormalizeThumbprint(" ab:cd ef ")
            .Should().Be("ABCDEF");
    }

    /// <summary>
    /// Scenario: remote marked unconfigured/unavailable.
    /// Expected: transport ConnectAsync is never attempted.
    /// </summary>
    [Fact]
    public async Task UnavailableTransport_NeverConnects()
    {
        var transport = new UnavailableRemoteConsoleTransport();
        transport.IsConnected.Should().BeFalse();
        var act = async () => await transport.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();
    }
}
