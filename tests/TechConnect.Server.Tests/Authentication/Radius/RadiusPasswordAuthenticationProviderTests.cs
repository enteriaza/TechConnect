// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusPasswordAuthenticationProviderTests.cs
//
// Purpose:
// Regression tests and helpers covering RadiusPasswordAuthenticationProvider behaviour.
// Fixture: RadiusPasswordAuthenticationProviderTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Authentication.Radius.Transport;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Radius;
/// <summary>
/// xUnit test fixture for RadiusPasswordAuthenticationProvider.
/// </summary>

public sealed class RadiusPasswordAuthenticationProviderTests
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
    /// Verifies Primary Accept Valid Cisco Av Pair Succeeds Without Secondary.
    /// </summary>

    [Fact]
    public async Task PrimaryAccept_ValidCiscoAvPair_Succeeds_WithoutSecondary()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, "shell:priv-lvl=15"));
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: false);
        var identity = await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None);

        identity.Should().NotBeNull();
        identity!.Provider.Should().Be(AuthenticationProviderKind.Radius);
        identity.Method.Should().Be(AuthenticationMethodKind.Password);
        identity.Privilege.Should().Be(PrivilegeLevel.AdministratorBoundary);
        identity.LocalAccountId.Should().BeNull();
        identity.PublicKeyFingerprint.Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Accept Representative Privilege Levels Succeed.
    /// </summary>

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    public async Task PrimaryAccept_RepresentativePrivilegeLevels_Succeed(int level)
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, $"shell:priv-lvl={level}"));
        await using var harness = await Harness.CreateAsync(primary, secondary: null, localFallback: false);

        var identity = await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None);
        identity.Should().NotBeNull();
        identity!.Privilege.Value.Should().Be(level);
    }
    /// <summary>
    /// Verifies Primary Accept Missing Cisco Av Pair Rejects Without Secondary Or Fallback.
    /// </summary>

    [Fact]
    public async Task PrimaryAccept_MissingCiscoAvPair_Rejects_WithoutSecondaryOrFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret));
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: true);
        var identity = await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None);

        identity.Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Accept Filter Id Alone Does Not Grant Privilege Or Fallback.
    /// </summary>

    [Fact]
    public async Task PrimaryAccept_FilterIdAlone_DoesNotGrantPrivilege_OrFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
        {
            var parsed = new RadiusPacket(req);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                parsed.Identifier,
                parsed.Authenticator,
                PrimarySecret,
                RadiusAttribute.Create(RadiusAttributeType.FilterId, "techconnect:Administrator"));
        });
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: true);
        (await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None)).Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Accept Malformed Or Out Of Range Priv Lvl Rejects Without Fallback.
    /// </summary>

    [Theory]
    [InlineData("shell:priv-lvl=0")]
    [InlineData("shell:priv-lvl=16")]
    [InlineData("shell:priv-lvl=abc")]
    [InlineData("shell:priv-lvl=")]
    public async Task PrimaryAccept_MalformedOrOutOfRangePrivLvl_Rejects_WithoutFallback(string avPair)
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, avPair));
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: true);
        (await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None)).Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Accept Conflicting Priv Lvl Rejects.
    /// </summary>

    [Fact]
    public async Task PrimaryAccept_ConflictingPrivLvl_Rejects()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, "shell:priv-lvl=7", "shell:priv-lvl=15"));
        await using var harness = await Harness.CreateAsync(primary, secondary: null, localFallback: true);
        (await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None)).Should().BeNull();
    }
    /// <summary>
    /// Verifies Primary Reject Does Not Query Secondary Or Fallback.
    /// </summary>

    [Fact]
    public async Task PrimaryReject_DoesNotQuerySecondary_OrFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Reject(req, PrimarySecret));
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: true);
        (await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None)).Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Invalid Response Does Not Query Secondary Or Fallback.
    /// </summary>

    [Fact]
    public async Task PrimaryInvalidResponse_DoesNotQuerySecondary_OrFallback()
    {
        // Full-length Access-Accept signed with the wrong secret → InvalidResponse (not timeout).
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
        {
            var parsed = new RadiusPacket(req);
            return LoopbackRadiusServer.BuildAccessResponse(
                RadiusPacketCode.AccessAccept,
                parsed.Identifier,
                parsed.Authenticator,
                "wrong-secret"u8.ToArray(),
                CiscoAvPairPrivilegeMapperTests.CreateCiscoAvPairAttribute("shell:priv-lvl=7"));
        });
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ =>
            throw new InvalidOperationException("Secondary must not be queried."));

        await using var harness = await Harness.CreateAsync(primary, secondary, localFallback: true);
        (await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None)).Should().BeNull();
        secondary.DatagramsReceived.Should().Be(0);
    }
    /// <summary>
    /// Verifies Primary Unavailable Queries Secondary Accept.
    /// </summary>

    [Fact]
    public async Task PrimaryUnavailable_QueriesSecondary_Accept()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, req =>
            Accept(req, SecondarySecret, "shell:priv-lvl=7"));

        await using var harness = await Harness.CreateAsync(
            primary,
            secondary,
            localFallback: false,
            timeoutMs: 200,
            maxAttempts: 1);

        var identity = await harness.Provider.AuthenticateAsync("ops", "pw", CancellationToken.None);
        identity.Should().NotBeNull();
        identity!.Privilege.Should().Be(PrivilegeLevel.OperatorBoundary);
        identity.Provider.Should().Be(AuthenticationProviderKind.Radius);
        secondary.DatagramsReceived.Should().BeGreaterThan(0);
    }
    /// <summary>
    /// Verifies Secondary Reject No Fallback.
    /// </summary>

    [Fact]
    public async Task SecondaryReject_NoFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, req =>
            Reject(req, SecondarySecret));

        await using var harness = await Harness.CreateAsync(
            primary,
            secondary,
            localFallback: true,
            timeoutMs: 200,
            maxAttempts: 1);

        (await harness.Provider.AuthenticateAsync("admin", "local-secret", CancellationToken.None))
            .Should().BeNull();
    }
    /// <summary>
    /// Verifies Both Unavailable Fallback Enabled Local Password Accepted.
    /// </summary>

    [Fact]
    public async Task BothUnavailable_FallbackEnabled_LocalPasswordAccepted()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var secondary = LoopbackRadiusServer.Start(SecondarySecret, _ => null);

        await using var harness = await Harness.CreateAsync(
            primary,
            secondary,
            localFallback: true,
            timeoutMs: 200,
            maxAttempts: 1);

        var identity = await harness.Provider.AuthenticateAsync("admin", "local-secret", CancellationToken.None);
        identity.Should().NotBeNull();
        identity!.Provider.Should().Be(AuthenticationProviderKind.Local);
        identity.Method.Should().Be(AuthenticationMethodKind.Password);
        identity.Privilege.Should().Be(PrivilegeLevel.AdministratorBoundary);
        identity.LocalAccountId.Should().Be("admin");
    }
    /// <summary>
    /// Verifies Both Unavailable Fallback Enabled Wrong Password Rejected.
    /// </summary>

    [Fact]
    public async Task BothUnavailable_FallbackEnabled_WrongPasswordRejected()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var harness = await Harness.CreateAsync(
            primary,
            secondary: null,
            localFallback: true,
            timeoutMs: 200,
            maxAttempts: 1);

        (await harness.Provider.AuthenticateAsync("admin", "wrong", CancellationToken.None)).Should().BeNull();
    }
    /// <summary>
    /// Verifies Both Unavailable Fallback Disabled Rejects.
    /// </summary>

    [Fact]
    public async Task BothUnavailable_FallbackDisabled_Rejects()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var harness = await Harness.CreateAsync(
            primary,
            secondary: null,
            localFallback: false,
            timeoutMs: 200,
            maxAttempts: 1);

        (await harness.Provider.AuthenticateAsync("admin", "local-secret", CancellationToken.None))
            .Should().BeNull();
    }
    /// <summary>
    /// Verifies Cancellation Does Not Fallback.
    /// </summary>

    [Fact]
    public async Task Cancellation_DoesNotFallback()
    {
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var harness = await Harness.CreateAsync(
            primary,
            secondary: null,
            localFallback: true,
            timeoutMs: 5_000,
            maxAttempts: 1);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        (await harness.Provider.AuthenticateAsync("admin", "local-secret", cts.Token)).Should().BeNull();
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
    /// Test helper type Harness.
    /// </summary>

    private sealed class Harness : IAsyncDisposable
    {
        /// <summary>
        /// Creates a Harness test helper.
        /// </summary>
        private Harness(
            RadiusClientFactory factory,
            RadiusPasswordAuthenticationProvider provider)
        {
            Factory = factory;
            Provider = provider;
        }
        /// <summary>
        /// get test helper member.
        /// </summary>

        public RadiusClientFactory Factory { get; }
        /// <summary>
        /// get test helper member.
        /// </summary>

        public RadiusPasswordAuthenticationProvider Provider { get; }
        /// <summary>
        /// Create Async helper used by this fixture.
        /// </summary>

        public static async Task<Harness> CreateAsync(
            LoopbackRadiusServer primary,
            LoopbackRadiusServer? secondary,
            bool localFallback,
            int timeoutMs = 1000,
            int maxAttempts = 2)
        {
            await Task.Yield();
            var hash = new OperatorPasswordHasher().HashPassword("admin", "local-secret");
            var key = SshTestKeys.ExportPublicKeyLine(SshTestKeys.CreateEcdsaKeyPair("op"));

            var radius = new RadiusAuthenticationOptions
            {
                Enabled = true,
                LocalFallbackEnabled = localFallback,
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
                NasIdentifier = "techconnect-test"
            };

            var options = Options.Create(new TechConnectServerOptions
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
                                Username = "admin",
                                Enabled = true,
                                PrivilegeLevel = "15",
                                PasswordHash = hash,
                                AuthorizedPublicKeys = [key]
                            }
                        ]
                    },
                    Radius = radius
                }
            });

            var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
            var local = new LocalOperatorAccountStore(options, new OperatorPasswordHasher());
            var mapper = new CiscoAvPairPrivilegeMapper();
            var provider = new RadiusPasswordAuthenticationProvider(
                options,
                factory,
                mapper,
                local,
                TimeProvider.System,
                NullLogger<RadiusPasswordAuthenticationProvider>.Instance);

            return new Harness(factory, provider);
        }
        /// <summary>
        /// Dispose Async helper used by this fixture.
        /// </summary>

        public async ValueTask DisposeAsync() => await Factory.DisposeAsync();
    }
}
