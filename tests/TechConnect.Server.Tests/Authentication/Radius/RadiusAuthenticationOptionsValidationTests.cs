// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusAuthenticationOptionsValidationTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusAuthenticationOptionsValidation behaviour.
// Fixture: RadiusAuthenticationOptionsValidationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusAuthenticationOptionsValidation.
/// </summary>

public sealed class RadiusAuthenticationOptionsValidationTests
{
    /// <summary>
    /// Field <c>_validator</c> used by this implementation.
    /// </summary>
    private readonly TechConnectServerOptionsValidator _validator = new();
    /// <summary>
    /// Verifies Radius Disabled Succeeds With Local Public Key.
    /// </summary>

    [Fact]
    public void RadiusDisabled_Succeeds_WithLocalPublicKey()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = new RadiusAuthenticationOptions { Enabled = false };
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Radius Enabled With Valid Primary Succeeds.
    /// </summary>

    [Fact]
    public void RadiusEnabled_WithValidPrimary_Succeeds()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Primary Missing Fails.
    /// </summary>

    [Fact]
    public void PrimaryMissing_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.PrimaryServer.Address = "";
        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PrimaryServer:Address", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Secondary Optional When Empty.
    /// </summary>

    [Fact]
    public void SecondaryOptional_WhenEmpty()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.SecondaryServer = new RadiusServerOptions();
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Invalid Port Fails.
    /// </summary>

    [Fact]
    public void InvalidPort_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.PrimaryServer.Port = 0;
        _validator.Validate(null, options).Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Missing Shared Secret Fails.
    /// </summary>

    [Fact]
    public void MissingSharedSecret_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.PrimaryServer.SharedSecret = "";
        _validator.Validate(null, options).Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Invalid Timeout Fails.
    /// </summary>

    [Fact]
    public void InvalidTimeout_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.TimeoutMilliseconds = 1;
        _validator.Validate(null, options).Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Invalid Retry Count Fails.
    /// </summary>

    [Fact]
    public void InvalidRetryCount_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.MaxAttempts = 0;
        _validator.Validate(null, options).Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Local Fallback Enabled Without Password Accounts Fails.
    /// </summary>

    [Fact]
    public void LocalFallbackEnabled_WithoutPasswordAccounts_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.LocalFallbackEnabled = true;
        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("LocalFallbackEnabled", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Local Fallback Enabled With Password Account Succeeds.
    /// </summary>

    [Fact]
    public void LocalFallbackEnabled_WithPasswordAccount_Succeeds()
    {
        var hash = new OperatorPasswordHasher().HashPassword("admin", "local-secret");
        var options = BaseLocalPublicKey();
        options.Authentication.Local.LocalUsers[0].PasswordHash = hash;
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.LocalFallbackEnabled = true;
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Separate Secrets Are Allowed.
    /// </summary>

    [Fact]
    public void SeparateSecrets_AreAllowed()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = ValidRadius();
        options.Authentication.Radius.SecondaryServer = new RadiusServerOptions
        {
            Address = "127.0.0.1",
            Port = 1813,
            SharedSecret = "secondary-secret"
        };
        options.Authentication.Radius.PrimaryServer.SharedSecret.Should()
            .NotBe(options.Authentication.Radius.SecondaryServer.SharedSecret);
        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Local Fallback Enabled While Radius Disabled Fails.
    /// </summary>

    [Fact]
    public void LocalFallbackEnabled_WhileRadiusDisabled_Fails()
    {
        var options = BaseLocalPublicKey();
        options.Authentication.Radius = new RadiusAuthenticationOptions
        {
            Enabled = false,
            LocalFallbackEnabled = true
        };
        _validator.Validate(null, options).Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Base Local Public Key.
    /// </summary>

    private static TechConnectServerOptions BaseLocalPublicKey()
    {
        var key = TechConnect.Server.Tests.Support.SshTestKeys.ExportPublicKeyLine(
            TechConnect.Server.Tests.Support.SshTestKeys.CreateEcdsaKeyPair("op"));
        return new TechConnectServerOptions
        {
            Ssh = new SshServerOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = 2222 },
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = false,
                    PublicKeyAuthenticationEnabled = true,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "Administrator",
                            AuthorizedPublicKeys = [key]
                        }
                    ]
                }
            }
        };
    }
    /// <summary>
    /// Verifies Valid Radius.
    /// </summary>

    private static RadiusAuthenticationOptions ValidRadius() =>
        new()
        {
            Enabled = true,
            LocalFallbackEnabled = false,
            PrimaryServer = new RadiusServerOptions
            {
                Address = "127.0.0.1",
                Port = 1812,
                SharedSecret = "primary-secret"
            },
            TimeoutMilliseconds = 3000,
            MaxAttempts = 2,
            NasIdentifier = "techconnect"
        };
}
