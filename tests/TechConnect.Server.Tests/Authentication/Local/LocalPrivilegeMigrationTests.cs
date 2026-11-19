// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    LocalPrivilegeMigrationTests.cs
//
// Purpose:
// Regression tests and helpers covering LocalPrivilegeMigration behaviour.
// Fixture: LocalPrivilegeMigrationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for LocalPrivilegeMigration.
/// </summary>

public sealed class LocalPrivilegeMigrationTests
{
    /// <summary>
    /// Verifies Store Persists Privilege From Canonical Or Legacy Config.
    /// </summary>
    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("15", 15)]
    [InlineData("ReadOnly", 1)]
    [InlineData("Operator", 7)]
    [InlineData("Administrator", 15)]
    public void Store_PersistsPrivilege_FromCanonicalOrLegacyConfig(string configured, int expected)
    {
        var hash = new OperatorPasswordHasher().HashPassword("user", "secret");
        var store = CreateStore(configured, hash);

        store.TryAuthenticatePassword("user", "secret", out var account).Should().BeTrue();
        account!.Privilege.Value.Should().Be(expected);
        account.PasswordHash.Should().Be(hash);
    }
    /// <summary>
    /// Verifies Store Fails Closed On Missing Or Invalid Privilege.
    /// </summary>

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("16")]
    [InlineData("01")]
    [InlineData("SuperUser")]
    public void Store_FailsClosed_OnMissingOrInvalidPrivilege(string? configured)
    {
        var hash = new OperatorPasswordHasher().HashPassword("user", "secret");
        var act = () => CreateStore(configured ?? string.Empty, hash);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PrivilegeLevel*");
    }
    /// <summary>
    /// Verifies Validation Rejects Out Of Range Numeric Privilege.
    /// </summary>

    [Fact]
    public void Validation_RejectsOutOfRangeNumericPrivilege()
    {
        var validator = new TechConnectServerOptionsValidator();
        var hash = new OperatorPasswordHasher().HashPassword("admin", "secret");
        var options = new TechConnectServerOptions
        {
            Ssh = new SshServerOptions { Enabled = true, ListenAddress = "127.0.0.1", Port = 2222 },
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = true,
                    PublicKeyAuthenticationEnabled = false,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "16",
                            PasswordHash = hash
                        }
                    ]
                }
            }
        };

        var result = validator.Validate(null, options);
        result.Succeeded.Should().BeFalse();
        result.Failures.Should().Contain(f => f.Contains("PrivilegeLevel", StringComparison.Ordinal));
    }
    /// <summary>
    /// Verifies Create Store.
    /// </summary>

    private static LocalOperatorAccountStore CreateStore(string privilegeLevel, string hash)
    {
        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = true,
                    PublicKeyAuthenticationEnabled = false,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "user",
                            Enabled = true,
                            PrivilegeLevel = privilegeLevel,
                            PasswordHash = hash
                        }
                    ]
                }
            }
        });
        return new LocalOperatorAccountStore(options, new OperatorPasswordHasher());
    }
}
