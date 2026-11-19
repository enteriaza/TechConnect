// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AuditAuthenticationIntegrationTests.cs
//
// Purpose:
// Regression tests and helpers covering AuditAuthenticationIntegration behaviour.
// Fixture: AuditAuthenticationIntegrationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authentication.Radius.Protocol;
using TechConnect.Server.Configuration;
using TechConnect.Server.Tests.Authentication.Radius.Transport;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Audit;
/// <summary>
/// xUnit test fixture for AuditAuthenticationIntegration.
/// </summary>

public sealed class AuditAuthenticationIntegrationTests
{
    /// <summary>
    /// Field <c>PrimarySecret</c> used by this implementation.
    /// </summary>
    private static readonly byte[] PrimarySecret = "primary-secret"u8.ToArray();
    /// <summary>
    /// Verifies Local Password Success And Failure Record Single Events.
    /// </summary>

    [Fact]
    public async Task LocalPassword_SuccessAndFailure_RecordSingleEvents()
    {
        var audit = CreateAudit();
        var hash = new OperatorPasswordHasher().HashPassword("admin", "secret");
        var service = CreateLocalPasswordService(audit, hash);

        (await service.AuthenticatePasswordAsync("admin", "secret", CancellationToken.None)).Should().NotBeNull();
        (await service.AuthenticatePasswordAsync("admin", "wrong", CancellationToken.None)).Should().BeNull();

        var events = audit.GetRecent(10);
        events.Should().HaveCount(2);
        events[0].EventType.Should().Be(AuditEventType.SshPasswordAuthFailure);
        events[0].RequestedUsername.Should().Be("admin");
        events[0].ActorUsername.Should().BeNull();
        events[1].EventType.Should().Be(AuditEventType.SshPasswordAuthSuccess);
        events[1].ActorUsername.Should().Be("admin");
        AssertNoSensitiveMaterial(events);
    }
    /// <summary>
    /// Verifies Public Key Success And Failure Record Events.
    /// </summary>

    [Fact]
    public void PublicKey_SuccessAndFailure_RecordEvents()
    {
        var audit = CreateAudit();
        var key = SshTestKeys.CreateEcdsaKeyPair("op");
        var other = SshTestKeys.CreateEcdsaKeyPair("other");
        var service = CreatePublicKeyService(audit, SshTestKeys.ExportPublicKeyLine(key));

        service.AuthenticatePublicKey("admin", key).Should().NotBeNull();
        service.AuthenticatePublicKey("admin", other).Should().BeNull();

        var events = audit.GetRecent(10);
        events.Should().HaveCount(2);
        events[0].EventType.Should().Be(AuditEventType.SshPublicKeyAuthFailure);
        events[1].EventType.Should().Be(AuditEventType.SshPublicKeyAuthSuccess);
        AssertNoSensitiveMaterial(events);
    }
    /// <summary>
    /// Verifies Radius Accept Records Success Once.
    /// </summary>

    [Fact]
    public async Task RadiusAccept_RecordsSuccess_Once()
    {
        var audit = CreateAudit();
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Accept(req, PrimarySecret, "shell:priv-lvl=15"));
        await using var harness = await CreateRadiusHarnessAsync(audit, primary, localFallback: false);

        var identity = await harness.Provider.AuthenticateAsync("radius-user", "pw", CancellationToken.None);
        identity.Should().NotBeNull();

        var events = audit.GetRecent(10);
        events.Should().ContainSingle();
        events[0].EventType.Should().Be(AuditEventType.RadiusAuthSuccess);
        events[0].ActorUsername.Should().Be("radius-user");
        AssertNoSensitiveMaterial(events);
    }
    /// <summary>
    /// Verifies Radius Reject Records Denied Once.
    /// </summary>

    [Fact]
    public async Task RadiusReject_RecordsDenied_Once()
    {
        var audit = CreateAudit();
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, req =>
            Reject(req, PrimarySecret));
        await using var harness = await CreateRadiusHarnessAsync(audit, primary, localFallback: true);

        (await harness.Provider.AuthenticateAsync("radius-user", "pw", CancellationToken.None)).Should().BeNull();

        var events = audit.GetRecent(10);
        events.Should().ContainSingle();
        events[0].EventType.Should().Be(AuditEventType.RadiusAuthRejected);
        events[0].Outcome.Should().Be(AuditEventOutcome.Denied);
        AssertNoSensitiveMaterial(events);
    }
    /// <summary>
    /// Verifies Radius Unavailable Without Fallback Records Unavailable Failure.
    /// </summary>

    [Fact]
    public async Task RadiusUnavailable_WithoutFallback_RecordsUnavailableFailure()
    {
        var audit = CreateAudit();
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var harness = await CreateRadiusHarnessAsync(
            audit, primary, localFallback: false, timeoutMs: 200, maxAttempts: 1);

        (await harness.Provider.AuthenticateAsync("radius-user", "pw", CancellationToken.None)).Should().BeNull();

        var events = audit.GetRecent(10);
        events.Should().ContainSingle();
        events[0].EventType.Should().Be(AuditEventType.RadiusUnavailable);
        events[0].Outcome.Should().Be(AuditEventOutcome.Failure);
    }
    /// <summary>
    /// Verifies Radius Unavailable Local Fallback Success And Failure.
    /// </summary>

    [Fact]
    public async Task RadiusUnavailable_LocalFallbackSuccessAndFailure()
    {
        var audit = CreateAudit();
        await using var primary = LoopbackRadiusServer.Start(PrimarySecret, _ => null);
        await using var harness = await CreateRadiusHarnessAsync(
            audit, primary, localFallback: true, timeoutMs: 200, maxAttempts: 1);

        (await harness.Provider.AuthenticateAsync("admin", "local-secret", CancellationToken.None))
            .Should().NotBeNull();
        (await harness.Provider.AuthenticateAsync("admin", "wrong", CancellationToken.None))
            .Should().BeNull();

        var events = audit.GetRecent(10);
        events.Select(e => e.EventType).Should().Equal(
            AuditEventType.LocalFallbackFailure,
            AuditEventType.RadiusUnavailable,
            AuditEventType.LocalFallbackSuccess,
            AuditEventType.RadiusUnavailable);
        AssertNoSensitiveMaterial(events);
    }
    /// <summary>
    /// Verifies Assert No Sensitive Material.
    /// </summary>

    private static void AssertNoSensitiveMaterial(IReadOnlyList<AuditEvent> events)
    {
        foreach (var evt in events)
        {
            var blob = evt.Description + "|" + string.Join('|', evt.Details?.Select(p => p.Key + "=" + p.Value) ?? []);
            blob.Should().NotContain("primary-secret");
            blob.Should().NotContain("local-secret");
            blob.Should().NotContain("BEGIN");
            blob.Should().NotContain("AQAAAA"); // Identity V3 hash prefix
            if (evt.Details is not null)
            {
                evt.Details.Keys.Should().NotContain("password");
                evt.Details.Keys.Should().NotContain("sharedSecret");
            }
        }
    }
    /// <summary>
    /// Verifies Create Audit.
    /// </summary>

    private static InMemoryAuditEventStore CreateAudit() =>
        new(true, 1000, TimeProvider.System, NullLogger<InMemoryAuditEventStore>.Instance);
    /// <summary>
    /// Verifies Create Local Password Service.
    /// </summary>

    private static OperatorAuthenticationService CreateLocalPasswordService(
        InMemoryAuditEventStore audit,
        string passwordHash)
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
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "15",
                            PasswordHash = passwordHash
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions { Enabled = false }
            }
        });
        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var mapper = new CiscoAvPairPrivilegeMapper();
        var radius = new RadiusPasswordAuthenticationProvider(
            options, factory, mapper, local, TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance, audit);
        return new OperatorAuthenticationService(
            local, radius, TimeProvider.System,
            NullLogger<OperatorAuthenticationService>.Instance, audit);
    }
    /// <summary>
    /// Verifies Create Public Key Service.
    /// </summary>

    private static OperatorAuthenticationService CreatePublicKeyService(
        InMemoryAuditEventStore audit,
        string keyLine)
    {
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
                            AuthorizedPublicKeys = [keyLine]
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions { Enabled = false }
            }
        });
        var hasher = new OperatorPasswordHasher();
        var local = new LocalOperatorAccountStore(options, hasher);
        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var mapper = new CiscoAvPairPrivilegeMapper();
        var radius = new RadiusPasswordAuthenticationProvider(
            options, factory, mapper, local, TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance, audit);
        return new OperatorAuthenticationService(
            local, radius, TimeProvider.System,
            NullLogger<OperatorAuthenticationService>.Instance, audit);
    }
    /// <summary>
    /// Verifies Create Radius Harness Async.
    /// </summary>

    private static async Task<RadiusHarness> CreateRadiusHarnessAsync(
        InMemoryAuditEventStore audit,
        LoopbackRadiusServer primary,
        bool localFallback,
        int timeoutMs = 1000,
        int maxAttempts = 2)
    {
        await Task.Yield();
        var hash = new OperatorPasswordHasher().HashPassword("admin", "local-secret");
        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    PasswordAuthenticationEnabled = false,
                    PublicKeyAuthenticationEnabled = false,
                    LocalUsers =
                    [
                        new LocalOperatorAccountOptions
                        {
                            Username = "admin",
                            Enabled = true,
                            PrivilegeLevel = "15",
                            PasswordHash = hash
                        }
                    ]
                },
                Radius = new RadiusAuthenticationOptions
                {
                    Enabled = true,
                    LocalFallbackEnabled = localFallback,
                    PrimaryServer = new RadiusServerOptions
                    {
                        Address = primary.EndPoint.Address.ToString(),
                        Port = primary.EndPoint.Port,
                        SharedSecret = "primary-secret"
                    },
                    TimeoutMilliseconds = timeoutMs,
                    MaxAttempts = maxAttempts,
                    NasIdentifier = "techconnect-test"
                }
            }
        });

        var factory = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var local = new LocalOperatorAccountStore(options, new OperatorPasswordHasher());
        var mapper = new CiscoAvPairPrivilegeMapper();
        var provider = new RadiusPasswordAuthenticationProvider(
            options, factory, mapper, local, TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance, audit);
        return new RadiusHarness(factory, provider);
    }
    /// <summary>
    /// Verifies Accept.
    /// </summary>

    private static byte[] Accept(byte[] request, byte[] secret, params string[] ciscoAvPairs)
    {
        var parsed = new RadiusPacket(request);
        var attrs = ciscoAvPairs
            .Select(TechConnect.Server.Tests.Authentication.Radius.CiscoAvPairPrivilegeMapperTests.CreateCiscoAvPairAttribute)
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
        public RadiusHarness(RadiusClientFactory factory, RadiusPasswordAuthenticationProvider provider)
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
        /// Dispose Async helper used by this fixture.
        /// </summary>

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }
}
