// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshLocalAuthenticationIntegrationTests.cs
//
// Purpose:
// Regression tests and helpers covering SshLocalAuthenticationIntegration behaviour.
// Fixture: SshLocalAuthenticationIntegrationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for SshLocalAuthenticationIntegration.
/// </summary>

public sealed class SshLocalAuthenticationIntegrationTests
{
    /// <summary>
    /// Verifies Password Authentication Success.
    /// </summary>
    [Fact]
    public async Task PasswordAuthentication_Success()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            authorizedOperatorKeys: [],
            passwordHash: hash,
            passwordAuthenticationEnabled: true,
            publicKeyAuthenticationEnabled: false);

        var (ok, output) = await SshTestHost.ConnectWithPasswordAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            "operator-secret",
            ["help"]);

        ok.Should().BeTrue();
        output.Should().Contain("Available commands");
    }
    /// <summary>
    /// Verifies Password Authentication Failure.
    /// </summary>

    [Fact]
    public async Task PasswordAuthentication_Failure()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            authorizedOperatorKeys: [],
            passwordHash: hash,
            passwordAuthenticationEnabled: true,
            publicKeyAuthenticationEnabled: false);

        var (ok, output) = await SshTestHost.ConnectWithPasswordAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            "wrong-password",
            []);

        ok.Should().BeFalse();
        output.Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Public Key Authentication Success.
    /// </summary>

    [Fact]
    public async Task PublicKeyAuthentication_Success()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var (ok, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["help"]);

        ok.Should().BeTrue();
        output.Should().Match(o => o.Contains("TechConnect#") || o.Contains("TechConnect>"));
    }
    /// <summary>
    /// Verifies Public Key Authentication Wrong Username Fails.
    /// </summary>

    [Fact]
    public async Task PublicKeyAuthentication_WrongUsername_Fails()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey], username: "alice");

        var (ok, _) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "bob",
            operatorKey,
            []);

        ok.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Password Authentication Disabled Rejects Password Clients.
    /// </summary>

    [Fact]
    public async Task PasswordAuthentication_Disabled_RejectsPasswordClients()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            [operatorKey],
            passwordHash: hash,
            passwordAuthenticationEnabled: false,
            publicKeyAuthenticationEnabled: true);

        var (ok, _) = await SshTestHost.ConnectWithPasswordAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            "operator-secret",
            []);

        ok.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Public Key Authentication Disabled Rejects Key Clients.
    /// </summary>

    [Fact]
    public async Task PublicKeyAuthentication_Disabled_RejectsKeyClients()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            [operatorKey],
            passwordHash: hash,
            passwordAuthenticationEnabled: true,
            publicKeyAuthenticationEnabled: false);

        var (ok, _) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            []);

        ok.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Authentication Unknown Username Is Generic Failure.
    /// </summary>

    [Fact]
    public async Task Authentication_UnknownUsername_IsGenericFailure()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            authorizedOperatorKeys: [],
            passwordHash: hash,
            passwordAuthenticationEnabled: true,
            publicKeyAuthenticationEnabled: false);

        var (unknownUser, _) = await SshTestHost.ConnectWithPasswordAndRunAsync(
            host.Port,
            hostKey,
            "missing",
            "operator-secret",
            []);
        var (badPassword, _) = await SshTestHost.ConnectWithPasswordAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            "wrong",
            []);

        unknownUser.Should().BeFalse();
        badPassword.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Max Client Authentication Attempts Remains Three.
    /// </summary>

    [Fact]
    public void MaxClientAuthenticationAttempts_RemainsThree()
    {
        TechConnect.Server.Ssh.SshServerHostedService.MaxClientAuthenticationAttempts.Should().Be(3);
    }
    /// <summary>
    /// Verifies Repeated Password Failures Do Not Authenticate.
    /// </summary>

    [Fact]
    public async Task RepeatedPasswordFailures_DoNotAuthenticate()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var hash = new OperatorPasswordHasher().HashPassword("noc", "operator-secret");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            authorizedOperatorKeys: [],
            passwordHash: hash,
            passwordAuthenticationEnabled: true,
            publicKeyAuthenticationEnabled: false);

        for (var i = 0; i < 3; i++)
        {
            var (ok, output) = await SshTestHost.ConnectWithPasswordAndRunAsync(
                host.Port,
                hostKey,
                "noc",
                "wrong-password",
                []);
            ok.Should().BeFalse();
            output.Should().BeEmpty();
        }
    }
    /// <summary>
    /// Verifies Authenticated Shell Requires Resolved Local Account Preserves Configured Username Casing.
    /// </summary>

    [Fact]
    public async Task AuthenticatedShell_RequiresResolvedLocalAccount_PreservesConfiguredUsernameCasing()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(
            hostKey,
            [operatorKey],
            username: "OpsUser",
            privilegeLevel: "ReadOnly");

        host.AccountStore.TryGet("opsuser", out var account).Should().BeTrue();
        account!.Username.Should().Be("OpsUser");
        account.Privilege.Should().Be(PrivilegeLevel.ReadOnlyBoundary);

        var (ok, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "OpsUser",
            operatorKey,
            ["help"]);

        // Shell authorization requires AuthenticatedOperatorClaims on the server principal.
        ok.Should().BeTrue();
        output.Should().Contain("Available commands");
    }
}
