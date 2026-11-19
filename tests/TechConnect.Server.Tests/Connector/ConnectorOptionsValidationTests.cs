// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConnectorOptionsValidationTests.cs
//
// Purpose:
// Verifies TechConnectServerOptionsValidator rules for disabled vs enabled connector listener configuration.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Options validation tests for connector listener enablement and certificate requirements.
/// </summary>
public sealed class ConnectorOptionsValidationTests
{
    /// <summary>
    /// Verifies a disabled connector does not require server thumbprint or client cert settings.
    /// </summary>
    [Fact]
    public void DisabledConnector_DoesNotRequireThumbprint()
    {
        var result = new TechConnectServerOptionsValidator().Validate(null, new TechConnectServerOptions
        {
            Ssh = new SshServerOptions { Enabled = false },
            Connector = new ConnectorListenerOptions { Enabled = false }
        });

        result.Succeeded.Should().BeTrue();
    }

    /// <summary>
    /// Verifies an enabled connector fails validation without thumbprint and RequireClientCertificate.
    /// </summary>

    [Fact]
    public void EnabledConnector_RequiresThumbprintAndClientCert()
    {
        var result = new TechConnectServerOptionsValidator().Validate(null, new TechConnectServerOptions
        {
            Ssh = new SshServerOptions { Enabled = false },
            Connector = new ConnectorListenerOptions
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                Port = 9443,
                ServerCertificateThumbprint = "",
                ClientCertificateValidation = new ClientCertificateValidationOptions
                {
                    RequireClientCertificate = false
                }
            }
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(f => f.Contains("ServerCertificateThumbprint", StringComparison.Ordinal));
        result.Failures.Should().Contain(f => f.Contains("RequireClientCertificate", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies a fully populated enabled connector configuration validates successfully.
    /// </summary>

    [Fact]
    public void EnabledConnector_AcceptsValidConfiguration()
    {
        var result = new TechConnectServerOptionsValidator().Validate(null, new TechConnectServerOptions
        {
            Ssh = new SshServerOptions { Enabled = false },
            Connector = new ConnectorListenerOptions
            {
                Enabled = true,
                ListenAddress = "0.0.0.0",
                Port = 9443,
                ServerCertificateThumbprint = new string('A', 40),
                ClientCertificateValidation = new ClientCertificateValidationOptions
                {
                    RequireClientCertificate = true
                },
                MaximumConcurrentConnections = 10,
                HandshakeTimeoutSeconds = 5,
                ShutdownTimeoutSeconds = 5
            }
        });

        result.Succeeded.Should().BeTrue();
    }
}
