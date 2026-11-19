// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteEndpointAndOptionsTests.cs
//
// Purpose:
// Verifies RemoteEndpoint URI derivation and RemoteService options validation.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Configuration;

/// <summary>
/// Unit tests for RemoteEndpoint URI derivation and RemoteService options validation.
/// </summary>
public sealed class RemoteEndpointAndOptionsTests
{
    /// <summary>
    /// Scenario: https BaseUrl with explicit port and path.
    /// Expected: wss URI with same host/port/path.
    /// </summary>
    [Fact]
    public void CreateWebSocketUri_HttpsMapsToWss()
    {
        var options = new RemoteServiceOptions
        {
            BaseUrl = "https://remote.contoso.test:8443",
            WebSocketPath = "/console/v1/session"
        };

        var uri = RemoteEndpoint.CreateWebSocketUri(options);
        uri.Scheme.Should().Be("wss");
        uri.Host.Should().Be("remote.contoso.test");
        uri.Port.Should().Be(8443);
        uri.AbsolutePath.Should().Be("/console/v1/session");
    }

    /// <summary>
    /// Scenario: https BaseUrl on default port 443.
    /// Expected: wss URI omits an explicit port.
    /// </summary>
    [Fact]
    public void CreateWebSocketUri_DefaultHttpsPort_OmitsPort()
    {
        var options = new RemoteServiceOptions
        {
            BaseUrl = "https://remote.contoso.test",
            WebSocketPath = "/ws"
        };

        var uri = RemoteEndpoint.CreateWebSocketUri(options);
        uri.IsDefaultPort.Should().BeTrue();
        uri.ToString().Should().Be("wss://remote.contoso.test/ws");
    }

    /// <summary>
    /// Scenario: non-https BaseUrl values.
    /// Expected: CreateWebSocketUri throws.
    /// </summary>
    [Theory]
    [InlineData("http://remote.contoso.test")]
    [InlineData("ws://remote.contoso.test")]
    [InlineData("wss://remote.contoso.test")]
    public void CreateWebSocketUri_RejectsNonHttps(string baseUrl)
    {
        var options = new RemoteServiceOptions { BaseUrl = baseUrl };
        var act = () => RemoteEndpoint.CreateWebSocketUri(options);
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*HTTPS*");
    }

    /// <summary>
    /// Scenario: placeholder host BaseUrls.
    /// Expected: CreateWebSocketUri throws.
    /// </summary>
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("https://foo.example.org")]
    [InlineData("https://example.org/path")]
    public void CreateWebSocketUri_RejectsPlaceholderHosts(string baseUrl)
    {
        var options = new RemoteServiceOptions { BaseUrl = baseUrl };
        var act = () => RemoteEndpoint.CreateWebSocketUri(options);
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*placeholder*");
    }

    /// <summary>
    /// Scenario: empty BaseUrl.
    /// Expected: CreateWebSocketUri throws.
    /// </summary>
    [Fact]
    public void CreateWebSocketUri_EmptyBaseUrl_Throws()
    {
        var act = () => RemoteEndpoint.CreateWebSocketUri(new RemoteServiceOptions());
        act.Should().Throw<RemoteTransportConfigurationException>()
            .WithMessage("*not configured*");
    }

    /// <summary>
    /// Scenario: IsConfigured checks for https non-placeholder BaseUrl.
    /// Expected: true only for valid https endpoints.
    /// </summary>
    [Fact]
    public void IsConfigured_RequiresHttpsNonPlaceholder()
    {
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions()).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { BaseUrl = "https://example.com" }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { BaseUrl = "http://real.contoso.test" }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { BaseUrl = "https://real.contoso.test" }).Should().BeTrue();
    }

    /// <summary>
    /// Scenario: TCP Host/Port configuration without placeholders.
    /// Expected: IsConfigured reflects host/port validity.
    /// </summary>
    [Fact]
    public void IsConfigured_RequiresHostPortNonPlaceholder()
    {
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "example.com", Port = 9443 }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "127.0.0.1", Port = 0 }).Should().BeFalse();
        RemoteEndpoint.IsConfigured(new RemoteServiceOptions { Host = "127.0.0.1", Port = 9443 }).Should().BeTrue();
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
            HeartbeatIntervalSeconds = 15,
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
    /// Scenario: http BaseUrl.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsHttpBaseUrl()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            BaseUrl = "http://remote.contoso.test"
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("HTTPS", StringComparison.Ordinal));
    }

    /// <summary>
    /// Scenario: placeholder BaseUrl.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsPlaceholderBaseUrl()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            BaseUrl = "https://api.example.com"
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("placeholder", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scenario: invalid WebSocket path.
    /// Expected: validation failure.
    /// </summary>
    [Fact]
    public void Validator_RejectsBadWebSocketPath()
    {
        var result = new RemoteServiceOptionsValidator().Validate(null, new RemoteServiceOptions
        {
            WebSocketPath = "console"
        });
        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("WebSocketPath", StringComparison.Ordinal));
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
