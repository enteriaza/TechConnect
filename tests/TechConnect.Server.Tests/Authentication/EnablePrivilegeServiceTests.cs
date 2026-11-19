// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    EnablePrivilegeServiceTests.cs
//
// Purpose:
// Regression tests and helpers covering EnablePrivilegeService behaviour.
// Fixture: EnablePrivilegeServiceTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Authentication.Radius;
using TechConnect.Server.Tests.Authentication.Radius.Transport;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication;
/// <summary>
/// xUnit test fixture for EnablePrivilegeService.
/// </summary>

public sealed class EnablePrivilegeServiceTests
{
    /// <summary>
    /// Field <c>PrimarySecret</c> used by this implementation.
    /// </summary>
    private static readonly byte[] PrimarySecret = "primary-secret"u8.ToArray();
    /// <summary>
    /// Field <c>SecondarySecret</c> used by this implementation.
    /// </summary>
    private static readonly byte[] SecondarySecret = "secondary-secret"u8.ToArray();
    /// <summary>
    /// Verifies Local Correct Secret Grants Requested Level Above Original.
    /// </summary>

    [Fact]
    public async Task Local_CorrectSecret_GrantsRequestedLevel_AboveOriginal()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("enable-secret");
        var svc = CreateLocalService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));

        var result = await svc.AuthenticateEnableAsync(
            identity,
            "enable-secret",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Privilege.Should().Be(PrivilegeLevel.FromInt(15));
    }
    /// <summary>
    /// Verifies Local Wrong Secret Fails.
    /// </summary>

    [Fact]
    public async Task Local_WrongSecret_Fails()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("enable-secret");
        var svc = CreateLocalService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));

        var result = await svc.AuthenticateEnableAsync(
            identity,
            "wrong",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(EnableAuthFailureKind.Failed);
    }
    /// <summary>
    /// Verifies Local Missing Secret Config Unavailable.
    /// </summary>

    [Fact]
    public async Task Local_MissingSecretConfig_Unavailable()
    {
        var svc = CreateLocalService(enableSecretHash: "");
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));

        var result = await svc.AuthenticateEnableAsync(
            identity,
            "anything",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(EnableAuthFailureKind.Unavailable);
    }
    /// <summary>
    /// Verifies Local Login Password Is Not Implicit Enable Secret.
    /// </summary>

    [Fact]
    public async Task Local_LoginPassword_IsNotImplicitEnableSecret()
    {
        var enableHash = new OperatorPasswordHasher().HashEnableSecret("enable-only");
        var loginHash = new OperatorPasswordHasher().HashPassword("op", "login-password");
        var svc = CreateLocalService(enableHash, loginPasswordHash: loginHash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));

        var result = await svc.AuthenticateEnableAsync(
            identity,
            "login-password",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Radius Accept Priv15 Allows Enable15.
    /// </summary>

    [Fact]
    public async Task Radius_AcceptPriv15_AllowsEnable15()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, "shell:priv-lvl=15"));
        await using var harness = await CreateRadiusHarnessAsync(primary, secondary: null);

        var identity = RadiusUser(PrivilegeLevel.FromInt(1));
        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "pw",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Privilege.Should().Be(PrivilegeLevel.FromInt(15));
        result.AuthorizedPrivilege.Should().Be(PrivilegeLevel.FromInt(15));
    }
    /// <summary>
    /// Verifies Radius Requested Exceeds Authorized Denies.
    /// </summary>

    [Theory]
    [InlineData(15, 7)]
    [InlineData(11, 7)]
    [InlineData(7, 5)]
    public async Task Radius_RequestedExceedsAuthorized_Denies(int requested, int authorized)
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, $"shell:priv-lvl={authorized}"));
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried for insufficient privilege."));
        await using var harness = await CreateRadiusHarnessAsync(primary, secondary);

        var identity = RadiusUser(PrivilegeLevel.FromInt(1));
        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "pw",
            PrivilegeLevel.FromInt(requested),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().Be(EnableAuthFailureKind.InsufficientPrivilege);
        result.Description.Should().Contain($"requested privilege level {requested}");
        result.Description.Should().Contain($"RADIUS-authorized level {authorized}");
        result.AuthorizedPrivilege.Should().Be(PrivilegeLevel.FromInt(authorized));
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Radius Requested At Or Below Authorized Allows.
    /// </summary>

    [Theory]
    [InlineData(7, 7)]
    [InlineData(5, 7)]
    [InlineData(3, 3)]
    public async Task Radius_RequestedAtOrBelowAuthorized_Allows(int requested, int authorized)
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, $"shell:priv-lvl={authorized}"));
        await using var harness = await CreateRadiusHarnessAsync(primary, secondary: null);

        var identity = RadiusUser(PrivilegeLevel.FromInt(1));
        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "pw",
            PrivilegeLevel.FromInt(requested),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Privilege.Should().Be(PrivilegeLevel.FromInt(requested));
        result.AuthorizedPrivilege.Should().Be(PrivilegeLevel.FromInt(authorized));
    }
    /// <summary>
    /// Verifies Local Level15 Secret Allows Lower Requested Level.
    /// </summary>

    [Fact]
    public async Task Local_Level15Secret_AllowsLowerRequestedLevel()
    {
        var hash = new OperatorPasswordHasher().HashEnableSecret("enable-secret");
        var svc = CreateLocalService(hash);
        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));

        var result = await svc.AuthenticateEnableAsync(
            identity,
            "enable-secret",
            PrivilegeLevel.FromInt(7),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Privilege.Should().Be(PrivilegeLevel.FromInt(7));
        result.AuthorizedPrivilege.Should().Be(PrivilegeLevel.FromInt(15));
    }
    /// <summary>
    /// Verifies Local Per Level Secret Does Not Authorize Higher Request.
    /// </summary>

    [Fact]
    public async Task Local_PerLevelSecret_DoesNotAuthorizeHigherRequest()
    {
        var hash7 = new OperatorPasswordHasher().HashEnableSecret("secret-7");
        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = true,
                    EnableSecrets =
                    [
                        new LocalEnableSecretOptions
                        {
                            PrivilegeLevel = "7",
                            SecretHash = hash7
                        }
                    ],
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "op",
                            Enabled = true,
                            PrivilegeLevel = "1",
                            PasswordHash = new OperatorPasswordHasher().HashPassword("op", "login")
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions { Enabled = false }
            }
        });

        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var radius = new RadiusPasswordAuthenticationProvider(
            options,
            factory,
            new CiscoAvPairPrivilegeMapper(),
            local,
            TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance);
        var svc = new EnablePrivilegeService(
            options,
            hasher,
            radius,
            NullLogger<EnablePrivilegeService>.Instance);

        var identity = TestAuthenticatedOperator.Create("op", PrivilegeLevel.FromInt(1));
        var denied = await svc.AuthenticateEnableAsync(
            identity,
            "secret-7",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);
        denied.Succeeded.Should().BeFalse();
        denied.FailureKind.Should().Be(EnableAuthFailureKind.InsufficientPrivilege);

        var allowed = await svc.AuthenticateEnableAsync(
            identity,
            "secret-7",
            PrivilegeLevel.FromInt(7),
            CancellationToken.None);
        allowed.Succeeded.Should().BeTrue();
        allowed.Privilege.Should().Be(PrivilegeLevel.FromInt(7));
    }
    /// <summary>
    /// Verifies Radius User.
    /// </summary>

    private static AuthenticatedOperator RadiusUser(PrivilegeLevel loginPrivilege) =>
        new(
            "radius-user",
            AuthenticationProviderKind.Radius,
            AuthenticationMethodKind.Password,
            loginPrivilege,
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            null,
            null);
    /// <summary>
    /// Verifies Radius Reject Fails No Local Fallback.
    /// </summary>

    [Fact]
    public async Task Radius_Reject_Fails_NoLocalFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Reject(req, PrimarySecret));
        await using var harness = await CreateRadiusHarnessAsync(primary, secondary: null, localEnableSecret: "local-enable");

        var identity = new AuthenticatedOperator(
            "radius-user",
            AuthenticationProviderKind.Radius,
            AuthenticationMethodKind.Password,
            PrivilegeLevel.FromInt(1),
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            null,
            null);

        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "local-enable",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureKind.Should().BeOneOf(EnableAuthFailureKind.Rejected, EnableAuthFailureKind.Failed);
    }
    /// <summary>
    /// Verifies Radius Missing Cisco Av Pair Fails.
    /// </summary>

    [Fact]
    public async Task Radius_MissingCiscoAvPair_Fails()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret));
        await using var harness = await CreateRadiusHarnessAsync(primary, secondary: null);

        var identity = new AuthenticatedOperator(
            "radius-user",
            AuthenticationProviderKind.Radius,
            AuthenticationMethodKind.Password,
            PrivilegeLevel.FromInt(1),
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            null,
            null);

        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "pw",
            PrivilegeLevel.FromInt(15),
            CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }
    /// <summary>
    /// Verifies Radius Unavailable Primary Uses Secondary.
    /// </summary>

    [Fact]
    public async Task Radius_UnavailablePrimary_UsesSecondary()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, req =>
            Accept(req, SecondarySecret, "shell:priv-lvl=11"));
        await using var harness = await CreateRadiusHarnessAsync(
            primary,
            secondary,
            timeoutMs: 200,
            maxAttempts: 1);

        var identity = new AuthenticatedOperator(
            "radius-user",
            AuthenticationProviderKind.Radius,
            AuthenticationMethodKind.Password,
            PrivilegeLevel.FromInt(1),
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"),
            null,
            null);

        var result = await harness.Service.AuthenticateEnableAsync(
            identity,
            "pw",
            PrivilegeLevel.FromInt(11),
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Privilege.Should().Be(PrivilegeLevel.FromInt(11));
        result.AuthorizedPrivilege.Should().Be(PrivilegeLevel.FromInt(11));
        secondary.DatagramsReceived.Should().BeGreaterThan(0);
    }
    /// <summary>
    /// Verifies Create Local Service.
    /// </summary>

    private static EnablePrivilegeService CreateLocalService(
        string enableSecretHash,
        string? loginPasswordHash = null)
    {
        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = true,
                    EnableSecretHash = enableSecretHash,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "op",
                            Enabled = true,
                            PrivilegeLevel = "1",
                            PasswordHash = loginPasswordHash ??
                                           new OperatorPasswordHasher().HashPassword("op", "login-password")
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions { Enabled = false }
            }
        });

        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var radius = new RadiusPasswordAuthenticationProvider(
            options,
            factory,
            new CiscoAvPairPrivilegeMapper(),
            local,
            TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance);

        return new EnablePrivilegeService(
            options,
            hasher,
            radius,
            NullLogger<EnablePrivilegeService>.Instance);
    }
    /// <summary>
    /// Verifies Create Radius Harness Async.
    /// </summary>

    private static async Task<RadiusHarness> CreateRadiusHarnessAsync(
        LoopbackRadiusServer primary,
        LoopbackRadiusServer? secondary,
        string? localEnableSecret = null,
        int timeoutMs = 1000,
        int maxAttempts = 2)
    {
        await Task.Yield();
        var enableHash = string.IsNullOrEmpty(localEnableSecret)
            ? string.Empty
            : new OperatorPasswordHasher().HashEnableSecret(localEnableSecret);

        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = false,
                    EnableSecretHash = enableHash,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "15",
                            PasswordHash = new OperatorPasswordHasher().HashPassword("admin", "unused")
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions
                {
                    Enabled = true,
                    LocalFallbackEnabled = false,
                    PrimaryServer = new RadiusServerOptions
                    {
                        Address = primary.EndPoint.Address.ToString(),
                        Port = primary.EndPoint.Port,
                        SharedSecret = "primary-secret"
                    },
                    SecondaryServer = secondary is null
                        ? null
                        : new RadiusServerOptions
                        {
                            Address = secondary.EndPoint.Address.ToString(),
                            Port = secondary.EndPoint.Port,
                            SharedSecret = "secondary-secret"
                        },
                    TimeoutMilliseconds = timeoutMs,
                    MaxAttempts = maxAttempts,
                    NasIdentifier = "techconnect-enable-test"
                }
            }
        });

        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var radius = new RadiusPasswordAuthenticationProvider(
            options,
            factory,
            new CiscoAvPairPrivilegeMapper(),
            local,
            TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance);
        var service = new EnablePrivilegeService(
            options,
            hasher,
            radius,
            NullLogger<EnablePrivilegeService>.Instance);

        return new RadiusHarness(factory, service);
    }
    /// <summary>
    /// Verifies Accept.
    /// </summary>

    private static byte[] Accept(byte[] request, byte[] secret, params string[] ciscoAvPairs)
    {
        var parsed = new RadiusPacket(request);
        var attrs = ciscoAvPairs
            .Select(CiscoAvPairPrivilegeMapperTests.CreateCiscoAvPairAttribute)
            .ToArray();
        return LoopbackRadiusServer.BuildAccessResponse(
            RadiusPacketCode.AccessAccept,
            parsed.Identifier,
            parsed.Authenticator,
            secret,
            attrs);
    }
    /// <summary>
    /// Verifies Reject.
    /// </summary>

    private static byte[] Reject(byte[] request, byte[] secret)
    {
        var parsed = new RadiusPacket(request);
        return LoopbackRadiusServer.BuildAccessResponse(
            RadiusPacketCode.AccessReject,
            parsed.Identifier,
            parsed.Authenticator,
            secret);
    }
    /// <summary>
    /// Test helper type RadiusHarness.
    /// </summary>

    private sealed class RadiusHarness : IAsyncDisposable
    {
        /// <summary>
        /// Creates a RadiusHarness test helper.
        /// </summary>
        public RadiusHarness(RadiusClientFactory factory, EnablePrivilegeService service)
        {
            Factory = factory;
            Service = service;
        }
        /// <summary>
        /// get test helper member.
        /// </summary>

        public RadiusClientFactory Factory { get; }
        /// <summary>
        /// get test helper member.
        /// </summary>

        public EnablePrivilegeService Service { get; }
        /// <summary>
        /// Dispose Async helper used by this fixture.
        /// </summary>

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }
}
