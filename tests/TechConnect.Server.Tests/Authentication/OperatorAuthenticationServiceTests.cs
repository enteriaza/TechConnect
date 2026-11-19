// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    OperatorAuthenticationServiceTests.cs
//
// Purpose:
// Regression tests and helpers covering OperatorAuthenticationService behaviour.
// Fixture: OperatorAuthenticationServiceTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication;
/// <summary>
/// xUnit test fixture for OperatorAuthenticationService.
/// </summary>

public sealed class OperatorAuthenticationServiceTests
{
    /// <summary>
    /// Verifies Public Key Does Not Require Radius And Uses Local Provider.
    /// </summary>
    [Fact]
    public void PublicKey_DoesNotRequireRadius_AndUsesLocalProvider()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var options = Options.Create(CreateOptions(keyLine: SshTestKeys.ExportPublicKeyLine(key), radiusEnabled: true));
        var service = CreateService(options);

        var identity = service.AuthenticatePublicKey("admin", key);
        identity.Should().NotBeNull();
        identity!.Provider.Should().Be(AuthenticationProviderKind.Local);
        identity.Method.Should().Be(AuthenticationMethodKind.PublicKey);
    }
    /// <summary>
    /// Verifies Local Password When Radius Disabled.
    /// </summary>

    [Fact]
    public async Task LocalPassword_WhenRadiusDisabled()
    {
        var hash = new OperatorPasswordHasher().HashPassword("admin", "pw");
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var options = Options.Create(CreateOptions(
            keyLine: SshTestKeys.ExportPublicKeyLine(key),
            radiusEnabled: false,
            passwordHash: hash,
            localPasswordEnabled: true));
        var service = CreateService(options);

        var identity = await service.AuthenticatePasswordAsync("admin", "pw", CancellationToken.None);
        identity.Should().NotBeNull();
        identity!.Provider.Should().Be(AuthenticationProviderKind.Local);
        identity.Method.Should().Be(AuthenticationMethodKind.Password);
    }
    /// <summary>
    /// Verifies Radius Claims Round Trip Without Local Account Id.
    /// </summary>

    [Fact]
    public void RadiusClaims_RoundTrip_WithoutLocalAccountId()
    {
        var identity = new AuthenticatedOperator(
            Username: "radius-user",
            Provider: AuthenticationProviderKind.Radius,
            Method: AuthenticationMethodKind.Password,
            Privilege: PrivilegeLevel.ReadOnlyBoundary,
            AuthenticatedAtUtc: DateTimeOffset.Parse("2026-11-18T12:00:00Z"),
            OperatorSessionId: "session123",
            LocalAccountId: null,
            PublicKeyFingerprint: null);

        var principal = AuthenticatedOperatorClaims.CreatePrincipal(identity);
        AuthenticatedOperatorClaims.TryCreateOperator(principal, out var restored).Should().BeTrue();
        restored.Should().BeEquivalentTo(identity);
    }
    /// <summary>
    /// Verifies Create Service.
    /// </summary>

    private static OperatorAuthenticationService CreateService(IOptions<TechConnectServerOptions> options)
    {
        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var mapper = new CiscoAvPairPrivilegeMapper();
        var radius = new RadiusPasswordAuthenticationProvider(
            options,
            factory,
            mapper,
            local,
            TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance);
        return new OperatorAuthenticationService(
            local,
            radius,
            TimeProvider.System,
            NullLogger<OperatorAuthenticationService>.Instance);
    }
    /// <summary>
    /// Verifies Create Options.
    /// </summary>

    private static TechConnectServerOptions CreateOptions(
        string keyLine,
        bool radiusEnabled,
        string? passwordHash = null,
        bool localPasswordEnabled = false) =>
        new()
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = localPasswordEnabled,
                    PublicKeyAuthenticationEnabled = true,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "15",
                            PasswordHash = passwordHash ?? string.Empty,
                            AuthorizedPublicKeys = [keyLine]
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions
                {
                    Enabled = radiusEnabled,
                    PrimaryServer = new RadiusServerOptions
                    {
                        Address = "127.0.0.1",
                        Port = 1812,
                        SharedSecret = "unused-when-disabled-or-not-queried"
                    },
                    NasIdentifier = "techconnect"
                }
            }
        };
}
