// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    LocalAuthenticationOptionsValidationTests.cs
//
// Purpose:
// Regression tests and helpers covering LocalAuthenticationOptionsValidation behaviour.
// Fixture: LocalAuthenticationOptionsValidationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for LocalAuthenticationOptionsValidation.
/// </summary>

public sealed class LocalAuthenticationOptionsValidationTests
{
    /// <summary>
    /// Field <c>_validator</c> used by this implementation.
    /// </summary>
    private readonly TechConnectServerOptionsValidator _validator = new();
    /// <summary>
    /// Verifies Valid Public Key Only Account Succeeds.
    /// </summary>

    [Fact]
    public void Valid_PublicKeyOnly_Account_Succeeds()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "Admin",
                Enabled = true,
                PrivilegeLevel = "Administrator",
                AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"))]
            }
        ]);

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Valid Password Only Account Succeeds.
    /// </summary>

    [Fact]
    public void Valid_PasswordOnly_Account_Succeeds()
    {
        var hash = new OperatorPasswordHasher().HashPassword("admin", "correct-horse");
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: false, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                PasswordHash = hash
            }
        ]);

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Invalid Username Fails.
    /// </summary>

    [Fact]
    public void InvalidUsername_Fails()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "bad name!",
                Enabled = true,
                PrivilegeLevel = "Operator",
                AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"))]
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("Username is invalid", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Duplicate Username Different Casing Fails.
    /// </summary>

    [Fact]
    public void DuplicateUsername_DifferentCasing_Fails()
    {
        var keyA = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"));
        var keyB = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("b"));
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "Admin",
                Enabled = true,
                PrivilegeLevel = "Administrator",
                AuthorizedPublicKeys = [keyA]
            },
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                AuthorizedPublicKeys = [keyB]
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("duplicates", StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies Invalid Privilege Fails.
    /// </summary>

    [Fact]
    public void InvalidPrivilege_Fails()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "SuperUser",
                AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"))]
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PrivilegeLevel", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Malformed Password Hash Fails.
    /// </summary>

    [Fact]
    public void MalformedPasswordHash_Fails()
    {
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: false, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                PasswordHash = "not-a-hash"
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PasswordHash is malformed", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Malformed Public Key Fails.
    /// </summary>

    [Fact]
    public void MalformedPublicKey_Fails()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                AuthorizedPublicKeys = ["not-a-valid-key"]
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("invalid public-key", StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies Enabled Account With No Auth Material Fails.
    /// </summary>

    [Fact]
    public void EnabledAccount_WithNoAuthMaterial_Fails()
    {
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator"
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("neither a PasswordHash nor AuthorizedPublicKeys", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Disabled Account Without Material Is Allowed When Other Usable Account Exists.
    /// </summary>

    [Fact]
    public void DisabledAccount_WithoutMaterial_IsAllowed_WhenOtherUsableAccountExists()
    {
        var hash = new OperatorPasswordHasher().HashPassword("live", "secret-value");
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: false, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "disabled",
                Enabled = false,
                PrivilegeLevel = "ReadOnly"
            },
            new LocalOperatorAccountOptions
            {
                Username = "live",
                Enabled = true,
                PrivilegeLevel = "Operator",
                PasswordHash = hash
            }
        ]);

        _validator.Validate(null, options).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Empty Account List Fails When Ssh Enabled.
    /// </summary>

    [Fact]
    public void EmptyAccountList_Fails_WhenSshEnabled()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users: []);
        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("LocalUsers must contain at least one", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Password Enabled Without Password Capable Accounts Fails.
    /// </summary>

    [Fact]
    public void PasswordEnabled_WithoutPasswordCapableAccounts_Fails()
    {
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"))]
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PasswordAuthenticationEnabled is true", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Public Key Enabled Without Key Capable Accounts Fails.
    /// </summary>

    [Fact]
    public void PublicKeyEnabled_WithoutKeyCapableAccounts_Fails()
    {
        var hash = new OperatorPasswordHasher().HashPassword("admin", "secret-value");
        var options = CreateOptions(passwordEnabled: true, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Operator",
                PasswordHash = hash
            }
        ]);

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PublicKeyAuthenticationEnabled is true", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Legacy Global Authorized Public Keys Fail Startup.
    /// </summary>

    [Fact]
    public void LegacyGlobalAuthorizedPublicKeys_FailStartup()
    {
        var options = CreateOptions(passwordEnabled: false, publicKeyEnabled: true, users:
        [
            new LocalOperatorAccountOptions
            {
                Username = "admin",
                Enabled = true,
                PrivilegeLevel = "Administrator",
                AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("a"))]
            }
        ]);
        options.Ssh.AuthorizedPublicKeys =
        [
            SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("legacy"))
        ];

        var result = _validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("AuthorizedPublicKeys is no longer supported", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Create Options.
    /// </summary>

    private static TechConnectServerOptions CreateOptions(
        bool passwordEnabled,
        bool publicKeyEnabled,
        List<LocalOperatorAccountOptions> users) =>
        new()
        {
            Ssh = new SshServerOptions
            {
                Enabled = true,
                ListenAddress = "127.0.0.1",
                Port = 2222
            },
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = passwordEnabled,
                    PublicKeyAuthenticationEnabled = publicKeyEnabled,
                    LocalUsers = users
                }
            }
        };
}
