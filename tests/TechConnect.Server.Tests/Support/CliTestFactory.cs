// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliTestFactory.cs
//
// Purpose:
// Builds in-process CLI dispatcher graphs for unit tests with RADIUS disabled
// and NullLogger defaults (no sockets, no real SSH listener).
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authorization;
using TechConnect.Server.Configuration;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.Connector;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Inspection;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Factory helpers for constructing CLI dispatch/authorization test graphs.
/// </summary>
/// <remarks>
/// Limitations: RADIUS is disabled; ICMP/DNS/routing use whatever the caller
/// injects (defaults inside <see cref="TechConnectCliDispatcher"/> when omitted).
/// Does not start an SSH listener or allocate real network sockets for RADIUS.
/// </remarks>
internal static class CliTestFactory
{
    /// <summary>
    /// Creates an in-memory audit store for tests.
    /// </summary>
    /// <param name="enabled">Whether audit recording is enabled.</param>
    /// <param name="maximumEvents">Retention limit.</param>
    /// <returns>New <see cref="InMemoryAuditEventStore"/>.</returns>
    public static InMemoryAuditEventStore CreateAudit(bool enabled = true, int maximumEvents = 10_000) =>
        new(enabled, maximumEvents, TimeProvider.System, NullLogger<InMemoryAuditEventStore>.Instance);

    /// <summary>
    /// Creates an <see cref="OperatorAuthorizationService"/> with a null logger.
    /// </summary>
    /// <param name="audit">Optional audit writer.</param>
    /// <returns>Authorization service bound to real <c>CliCommandPolicy</c>.</returns>
    public static OperatorAuthorizationService CreateAuthorization(IAuditEventWriter? audit = null) =>
        new(NullLogger<OperatorAuthorizationService>.Instance, audit);

    /// <summary>
    /// Creates a <see cref="ConnectorDisconnectService"/> for administrative disconnect tests.
    /// </summary>
    /// <param name="registry">Session registry.</param>
    /// <param name="tracker">Connection tracker.</param>
    /// <param name="authorization">Authorization service.</param>
    /// <param name="audit">Audit writer.</param>
    /// <returns>Disconnect service instance.</returns>
    public static ConnectorDisconnectService CreateDisconnectService(
        ConnectorSessionRegistry registry,
        ConnectorConnectionTracker tracker,
        OperatorAuthorizationService authorization,
        IAuditEventWriter audit) =>
        new(
            registry,
            tracker,
            authorization,
            audit,
            TimeProvider.System,
            NullLogger<ConnectorDisconnectService>.Instance);

    /// <summary>
    /// Builds <see cref="EnablePrivilegeService"/> with RADIUS disabled.
    /// </summary>
    /// <remarks>
    /// Pass a hash from <see cref="OperatorPasswordHasher.HashEnableSecret"/> for local enable.
    /// The RADIUS client factory is constructed but unused because RADIUS is disabled.
    /// </remarks>
    /// <param name="enableSecretHash">Optional Identity V3 enable secret hash (level 15 convenience).</param>
    /// <param name="audit">Optional audit writer.</param>
    /// <returns>Enable privilege service.</returns>
    public static EnablePrivilegeService CreateEnableService(
        string? enableSecretHash = null,
        IAuditEventWriter? audit = null)
    {
        var options = Options.Create(new TechConnectServerOptions
        {
            Authentication = new AuthenticationOptions
            {
                Local = new LocalAuthenticationOptions
                {
                    EnableSecretHash = enableSecretHash ?? string.Empty
                },
                Radius = new RadiusAuthenticationOptions { Enabled = false }
            }
        });
        var hasher = new OperatorPasswordHasher();
        var accounts = new LocalOperatorAccountStore(options, hasher);
        // RADIUS disabled — factory owns no sockets; keep alive for the provider lifetime.
        var radiusClients = new RadiusClientFactory(options, NullLoggerFactory.Instance, TimeProvider.System);
        var radius = new RadiusPasswordAuthenticationProvider(
            options,
            radiusClients,
            new CiscoAvPairPrivilegeMapper(),
            accounts,
            TimeProvider.System,
            NullLogger<RadiusPasswordAuthenticationProvider>.Instance,
            audit);
        return new EnablePrivilegeService(
            options,
            hasher,
            radius,
            NullLogger<EnablePrivilegeService>.Instance,
            audit);
    }

    /// <summary>
    /// Creates a full in-process dispatcher tuple with empty registries.
    /// </summary>
    /// <param name="audit">Optional shared audit store.</param>
    /// <param name="timeProvider">Optional time provider.</param>
    /// <param name="applicationInfo">Optional application info provider.</param>
    /// <param name="dnsConfiguration">Optional DNS configuration provider.</param>
    /// <param name="interfaces">Optional interface inspector.</param>
    /// <param name="routes">Optional routing inspector.</param>
    /// <param name="operatorSessions">Optional operator session tracker.</param>
    /// <param name="ipv6Availability">Optional IPv6 availability helper.</param>
    /// <param name="configuration">Optional configuration management service.</param>
    /// <returns>Dispatcher and collaborating services.</returns>
    public static (
        TechConnectCliDispatcher Dispatcher,
        ConnectorSessionRegistry Registry,
        OperatorAttachmentManager Attachments,
        ConnectorSerialConfigurationCache SerialCache,
        PendingBaudRequestTracker PendingBaud,
        OperatorAuthorizationService Authorization,
        InMemoryAuditEventStore Audit,
        ConnectorConnectionTracker Tracker,
        ConnectorDisconnectService Disconnect,
        EnablePrivilegeService Enable) CreateDispatcher(
        InMemoryAuditEventStore? audit = null,
        TimeProvider? timeProvider = null,
        IApplicationInfoProvider? applicationInfo = null,
        IDnsConfigurationProvider? dnsConfiguration = null,
        INetworkInterfaceInspector? interfaces = null,
        IRoutingTableInspector? routes = null,
        SshOperatorSessionTracker? operatorSessions = null,
        IIPv6NetworkAvailability? ipv6Availability = null,
        ConfigurationManagementService? configuration = null)
    {
        audit ??= CreateAudit();
        timeProvider ??= TimeProvider.System;
        var registry = new ConnectorSessionRegistry();
        var tracker = new ConnectorConnectionTracker();
        var attachments = new OperatorAttachmentManager(registry, timeProvider);
        var serialCache = new ConnectorSerialConfigurationCache();
        var pendingBaud = new PendingBaudRequestTracker();
        var authorization = CreateAuthorization(audit);
        var disconnect = CreateDisconnectService(registry, tracker, authorization, audit);
        var enable = CreateEnableService(audit: audit);
        var dispatcher = new TechConnectCliDispatcher(
            registry,
            attachments,
            serialCache,
            authorization,
            pendingBaud,
            timeProvider,
            audit,
            ipv6Availability: ipv6Availability,
            applicationInfo: applicationInfo,
            dnsConfiguration: dnsConfiguration,
            interfaces: interfaces,
            routes: routes,
            operatorSessions: operatorSessions,
            configuration: configuration);
        return (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, audit, tracker, disconnect, enable);
    }

    /// <summary>
    /// Creates a test <see cref="AuthenticatedOperator"/> with default administrator-boundary privilege.
    /// </summary>
    /// <param name="privilege">Optional privilege override.</param>
    /// <param name="username">Operator username.</param>
    /// <returns>Authenticated operator identity.</returns>
    public static AuthenticatedOperator Operator(
        PrivilegeLevel? privilege = null,
        string username = "op") =>
        TestAuthenticatedOperator.Create(username, privilege ?? PrivilegeLevel.AdministratorBoundary);
}
