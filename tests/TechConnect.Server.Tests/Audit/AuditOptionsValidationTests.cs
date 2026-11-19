// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AuditOptionsValidationTests.cs
//
// Purpose:
// Regression tests and helpers covering AuditOptionsValidation behaviour.
// Fixture: AuditOptionsValidationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Audit;
/// <summary>
/// xUnit test fixture for AuditOptionsValidation.
/// </summary>

public sealed class AuditOptionsValidationTests
{
    /// <summary>
    /// Verifies Valid Defaults Succeed.
    /// </summary>
    [Fact]
    public void ValidDefaults_Succeed()
    {
        var result = new TechConnectServerOptionsValidator().Validate(
            null,
            new TechConnectServerOptions
            {
                Ssh = new SshServerOptions { Enabled = false },
                Connector = new ConnectorListenerOptions { Enabled = false },
                Audit = new AuditOptions()
            });

        result.Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Invalid Maximum Events Fails.
    /// </summary>

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void InvalidMaximumEvents_Fails(int maximumEvents)
    {
        var result = new TechConnectServerOptionsValidator().Validate(
            null,
            new TechConnectServerOptions
            {
                Ssh = new SshServerOptions { Enabled = false },
                Connector = new ConnectorListenerOptions { Enabled = false },
                Audit = new AuditOptions { Enabled = true, MaximumEvents = maximumEvents }
            });

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Audit:MaximumEvents", StringComparison.Ordinal));
    }
}
