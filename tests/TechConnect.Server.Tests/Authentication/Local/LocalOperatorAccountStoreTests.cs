// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    LocalOperatorAccountStoreTests.cs
//
// Purpose:
// Regression tests and helpers covering LocalOperatorAccountStore behaviour.
// Fixture: LocalOperatorAccountStoreTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for LocalOperatorAccountStore.
/// </summary>

public sealed class LocalOperatorAccountStoreTests
{
    /// <summary>
    /// Verifies Password Correct Is Accepted.
    /// </summary>
    [Fact]
    public void Password_Correct_IsAccepted()
    {
        var store = CreateStore(password: "secret-value", includeKey: false);
        store.TryAuthenticatePassword("Admin", "secret-value", out var account).Should().BeTrue();
        account!.Username.Should().Be("Admin");
        account.Privilege.Should().Be(PrivilegeLevel.AdministratorBoundary);
    }
    /// <summary>
    /// Verifies Password Incorrect Is Rejected.
    /// </summary>

    [Fact]
    public void Password_Incorrect_IsRejected()
    {
        var store = CreateStore(password: "secret-value", includeKey: false);
        store.TryAuthenticatePassword("Admin", "wrong", out var account).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Password Unknown Username Is Rejected.
    /// </summary>

    [Fact]
    public void Password_UnknownUsername_IsRejected()
    {
        var store = CreateStore(password: "secret-value", includeKey: false);
        store.TryAuthenticatePassword("nobody", "secret-value", out var account).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Password Disabled Account Is Rejected.
    /// </summary>

    [Fact]
    public void Password_DisabledAccount_IsRejected()
    {
        var store = CreateStore(password: "secret-value", includeKey: false, enabled: false, passwordAuthEnabled: true, publicKeyAuthEnabled: false);
        store.TryAuthenticatePassword("Admin", "secret-value", out var account).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Configuration Does Not Retain Plaintext Password.
    /// </summary>

    [Fact]
    public void Configuration_DoesNotRetainPlaintextPassword()
    {
        var options = BuildOptions(password: "secret-value", includeKey: false, enabled: true, passwordAuth: true, publicKeyAuth: false);
        options.Authentication.Local.LocalUsers[0].PasswordHash.Should().NotContain("secret-value");
        options.Authentication.Local.LocalUsers[0].PasswordHash.Should().NotBeNullOrWhiteSpace();
    }
    /// <summary>
    /// Verifies Public Key Correct User Is Accepted.
    /// </summary>

    [Fact]
    public void PublicKey_CorrectUser_IsAccepted()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var store = CreateStore(
            password: null,
            includeKey: true,
            key: key,
            passwordAuthEnabled: false,
            publicKeyAuthEnabled: true);
        store.TryAuthenticatePublicKey("Admin", key, out var account, out var fingerprint).Should().BeTrue();
        account!.Username.Should().Be("Admin");
        fingerprint.Should().StartWith("SHA256:");
    }
    /// <summary>
    /// Verifies Public Key Wrong Username Is Rejected.
    /// </summary>

    [Fact]
    public void PublicKey_WrongUsername_IsRejected()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var other = SshTestKeys.CreateEcdsaKeyPair("other");
        var hasher = new OperatorPasswordHasher();
        var options = new TechConnectServerOptions
        {
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
                            Username = "alice",
                            Enabled = true,
                            PrivilegeLevel = "Operator",
                            AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(key, "alice-comment")]
                        },
                        new LocalOperatorAccountOptions
                        {
                            Username = "bob",
                            Enabled = true,
                            PrivilegeLevel = "Operator",
                            AuthorizedPublicKeys = [SshTestKeys.ExportPublicKeyLine(other, "bob-comment")]
                        }
                    ]
                }
            }
        };

        var store = new LocalOperatorAccountStore(Options.Create(options), hasher);
        store.TryAuthenticatePublicKey("bob", key, out var account, out _).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Public Key Unknown Key Is Rejected.
    /// </summary>

    [Fact]
    public void PublicKey_UnknownKey_IsRejected()
    {
        var authorized = SshTestKeys.CreateEcdsaKeyPair("auth");
        var unknown = SshTestKeys.CreateEcdsaKeyPair("unknown");
        var store = CreateStore(
            password: null,
            includeKey: true,
            key: authorized,
            passwordAuthEnabled: false,
            publicKeyAuthEnabled: true);
        store.TryAuthenticatePublicKey("Admin", unknown, out var account, out _).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Public Key Disabled Account Is Rejected.
    /// </summary>

    [Fact]
    public void PublicKey_DisabledAccount_IsRejected()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var store = CreateStore(password: null, includeKey: true, key: key, enabled: false, passwordAuthEnabled: false, publicKeyAuthEnabled: true);
        store.TryAuthenticatePublicKey("Admin", key, out var account, out _).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Public Key Comment Does Not Affect Identity.
    /// </summary>

    [Fact]
    public void PublicKey_Comment_DoesNotAffectIdentity()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var line = SshTestKeys.ExportPublicKeyLine(key, "this-is-not-the-username");
        var hasher = new OperatorPasswordHasher();
        var options = new TechConnectServerOptions
        {
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
                            Username = "real-user",
                            Enabled = true,
                            PrivilegeLevel = "ReadOnly",
                            AuthorizedPublicKeys = [line]
                        }
                    ]
                }
            }
        };

        var store = new LocalOperatorAccountStore(Options.Create(options), hasher);
        store.TryAuthenticatePublicKey("real-user", key, out var account, out _).Should().BeTrue();
        account!.Username.Should().Be("real-user");
        account.Privilege.Should().Be(PrivilegeLevel.ReadOnlyBoundary);
    }
    /// <summary>
    /// Verifies Username Lookup Is Case Insensitive Preserves Configured Casing.
    /// </summary>

    [Fact]
    public void UsernameLookup_IsCaseInsensitive_PreservesConfiguredCasing()
    {
        var store = CreateStore(password: "secret-value", includeKey: false);
        store.TryGet("admin", out var account).Should().BeTrue();
        account!.Username.Should().Be("Admin");
    }
    /// <summary>
    /// Verifies Create Store.
    /// </summary>

    private static LocalOperatorAccountStore CreateStore(
        string? password,
        bool includeKey,
        Microsoft.DevTunnels.Ssh.Algorithms.IKeyPair? key = null,
        bool enabled = true,
        bool passwordAuthEnabled = true,
        bool publicKeyAuthEnabled = false)
    {
        var hasher = new OperatorPasswordHasher();
        var options = BuildOptions(password, includeKey, enabled, passwordAuthEnabled, publicKeyAuthEnabled, key);
        return new LocalOperatorAccountStore(Options.Create(options), hasher);
    }
    /// <summary>
    /// Verifies Build Options.
    /// </summary>

    private static TechConnectServerOptions BuildOptions(
        string? password,
        bool includeKey,
        bool enabled,
        bool passwordAuth,
        bool publicKeyAuth,
        Microsoft.DevTunnels.Ssh.Algorithms.IKeyPair? key = null)
    {
        var hasher = new OperatorPasswordHasher();
        var user = new LocalOperatorAccountOptions
        {
            Username = "Admin",
            Enabled = enabled,
            PrivilegeLevel = "Administrator",
            PasswordHash = password is null ? string.Empty : hasher.HashPassword("Admin", password),
            AuthorizedPublicKeys = includeKey
                ? [SshTestKeys.ExportPublicKeyLine(key ?? SshTestKeys.CreateEcdsaKeyPair("op"))]
                : []
        };

        return new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = passwordAuth,
                    PublicKeyAuthenticationEnabled = publicKeyAuth,
                    LocalUsers = [user]
                }
            }
        };
    }
}
