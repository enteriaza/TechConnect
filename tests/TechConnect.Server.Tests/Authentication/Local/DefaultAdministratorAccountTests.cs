// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    DefaultAdministratorAccountTests.cs
//
// Purpose:
// Regression tests and helpers covering DefaultAdministratorAccount behaviour.
// Fixture: DefaultAdministratorAccountTests.
//
// -----------------------------------------------------------------------------

using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for DefaultAdministratorAccount.
/// </summary>

public sealed class DefaultAdministratorAccountTests
{
    /// <summary>
    /// Field <c>ServerProjectDir</c> used by this implementation.
    /// </summary>
    private static readonly string ServerProjectDir = FindServerProjectDirectory();
    /// <summary>
    /// Expected Hash Prefix test helper member.
    /// </summary>

    private static readonly string ExpectedHashPrefix = "AQAAAAI"; // Identity V3 marker in base64
    /// <summary>
    /// Verifies Base Appsettings Contains Default Administrator.
    /// </summary>

    [Fact]
    public void BaseAppsettings_ContainsDefaultAdministrator()
    {
        var user = ReadLocalUser(Path.Combine(ServerProjectDir, "appsettings.json"));
        AssertDefaultAdminShape(user);
    }
    /// <summary>
    /// Verifies Development Appsettings Contains Default Administrator.
    /// </summary>

    [Fact]
    public void DevelopmentAppsettings_ContainsDefaultAdministrator()
    {
        var user = ReadLocalUser(Path.Combine(ServerProjectDir, "appsettings.Development.json"));
        AssertDefaultAdminShape(user);
    }
    /// <summary>
    /// Verifies Default Administrator Password Hash Is Identity V3 And Not Plaintext.
    /// </summary>

    [Fact]
    public void DefaultAdministrator_PasswordHash_IsIdentityV3_AndNotPlaintext()
    {
        var baseUser = ReadLocalUser(Path.Combine(ServerProjectDir, "appsettings.json"));
        var devUser = ReadLocalUser(Path.Combine(ServerProjectDir, "appsettings.Development.json"));

        var hasher = new OperatorPasswordHasher();
        hasher.IsWellFormedHash(baseUser.PasswordHash).Should().BeTrue();
        hasher.IsWellFormedHash(devUser.PasswordHash).Should().BeTrue();
        baseUser.PasswordHash.Should().StartWith(ExpectedHashPrefix);
        baseUser.PasswordHash.Should().NotContain("admin");
        File.ReadAllText(Path.Combine(ServerProjectDir, "appsettings.json"))
            .Should().NotContain("\"Password\":");
        File.ReadAllText(Path.Combine(ServerProjectDir, "appsettings.Development.json"))
            .Should().NotContain("\"Password\":");
    }
    /// <summary>
    /// Verifies Admin Admin Authenticates As Local Password Administrator.
    /// </summary>

    [Fact]
    public void AdminAdmin_Authenticates_AsLocalPasswordAdministrator()
    {
        var options = LoadBoundOptions(Path.Combine(ServerProjectDir, "appsettings.json"));
        var store = new LocalOperatorAccountStore(Options.Create(options), new OperatorPasswordHasher());

        store.TryAuthenticatePassword("admin", "admin", out var account).Should().BeTrue();
        account!.Username.Should().Be("admin");
        account.Privilege.Should().Be(PrivilegeLevel.AdministratorBoundary);
        account.Enabled.Should().BeTrue();

        store.TryAuthenticatePassword("ADMIN", "admin", out var cased).Should().BeTrue();
        cased!.Username.Should().Be("admin");

        store.TryAuthenticatePassword("admin", "wrong", out var bad).Should().BeFalse();
        bad.Should().BeNull();
    }
    /// <summary>
    /// Verifies Disabled Default Admin Is Rejected.
    /// </summary>

    [Fact]
    public void DisabledDefaultAdmin_IsRejected()
    {
        var options = LoadBoundOptions(Path.Combine(ServerProjectDir, "appsettings.json"));
        options.Authentication.Local.LocalUsers[0].Enabled = false;
        var store = new LocalOperatorAccountStore(Options.Create(options), new OperatorPasswordHasher());
        store.TryAuthenticatePassword("admin", "admin", out var account).Should().BeFalse();
        account.Should().BeNull();
    }
    /// <summary>
    /// Verifies Default Administrator Receives Administrator Authorization By Privilege Not Username.
    /// </summary>

    [Fact]
    public void DefaultAdministrator_ReceivesAdministratorAuthorization_ByPrivilegeNotUsername()
    {
        var auth = CliTestFactory.CreateAuthorization();
        var identity = TestAuthenticatedOperator.Create(
            username: "admin",
            privilege: PrivilegeLevel.AdministratorBoundary);

        foreach (var command in Enum.GetValues<CliCommandId>())
        {
            if (!CliCommandPolicy.IsDefined(command))
            {
                continue;
            }

            auth.TryAuthorize(identity, command, out _).Should().BeTrue(
                $"Administrator privilege must allow {command}");
        }

        // Same username with ReadOnly must not inherit admin rights by name.
        var readOnlyNamedAdmin = TestAuthenticatedOperator.Create(
            username: "admin",
            privilege: PrivilegeLevel.ReadOnlyBoundary);
        auth.TryAuthorize(readOnlyNamedAdmin, CliCommandId.Connect, out _).Should().BeTrue();
        auth.TryAuthorize(readOnlyNamedAdmin, CliCommandId.Attach, out _).Should().BeTrue();
        auth.TryAuthorize(readOnlyNamedAdmin, CliCommandId.DisconnectSelf, out _).Should().BeTrue();
        auth.TryAuthorize(readOnlyNamedAdmin, CliCommandId.ShowSerial, out _).Should().BeFalse();
        auth.TryAuthorize(readOnlyNamedAdmin, CliCommandId.Disconnect, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Configuration Validation Accepts Default Administrator Accounts.
    /// </summary>

    [Fact]
    public void ConfigurationValidation_AcceptsDefaultAdministratorAccounts()
    {
        var validator = new TechConnectServerOptionsValidator();
        var production = LoadBoundOptions(Path.Combine(ServerProjectDir, "appsettings.json"));
        production.Ssh.Enabled = true;
        production.Ssh.ListenAddress = "127.0.0.1";
        validator.Validate(null, production).Succeeded.Should().BeTrue();

        var development = LoadBoundOptions(Path.Combine(ServerProjectDir, "appsettings.Development.json"));
        development.Ssh.Enabled = true;
        development.Ssh.ListenAddress = "127.0.0.1";
        validator.Validate(null, development).Succeeded.Should().BeTrue();
    }
    /// <summary>
    /// Verifies Assert Default Admin Shape.
    /// </summary>

    private static void AssertDefaultAdminShape(LocalOperatorAccountOptions user)
    {
        user.Username.Should().Be("admin");
        user.Enabled.Should().BeTrue();
        user.PrivilegeLevel.Should().Be("15");
        user.PasswordHash.Should().NotBeNullOrWhiteSpace();
        user.AuthorizedPublicKeys.Should().BeEmpty();
    }
    /// <summary>
    /// Verifies Read Local User.
    /// </summary>

    private static LocalOperatorAccountOptions ReadLocalUser(string path)
    {
        File.Exists(path).Should().BeTrue($"expected configuration file at {path}");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var users = root["TechConnectServer"]!["Authentication"]!["Local"]!["LocalUsers"]!.AsArray();
        users.Should().NotBeEmpty();
        var admin = users.Select(u => u!.Deserialize<LocalOperatorAccountOptions>()!)
            .Single(u => string.Equals(u.Username, "admin", StringComparison.OrdinalIgnoreCase));
        return admin;
    }
    /// <summary>
    /// Verifies Load Bound Options.
    /// </summary>

    private static TechConnectServerOptions LoadBoundOptions(string path)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(path, optional: false, reloadOnChange: false)
            .Build();

        var options = new TechConnectServerOptions();
        configuration.GetSection(TechConnectServerOptions.SectionName).Bind(options);
        return options;
    }
    /// <summary>
    /// Verifies Find Server Project Directory.
    /// </summary>

    private static string FindServerProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "TechConnect.Server");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate TechConnect.Server project directory.");
    }
}
