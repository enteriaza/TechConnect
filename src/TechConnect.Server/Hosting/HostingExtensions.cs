// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    HostingExtensions.cs
//
// Purpose:
// Registers TechConnect.Server options validation, core singletons, hosted
// listeners, and Serilog file/console logging on the generic host.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using TechConnect.Server.Attachment;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Authorization;
using TechConnect.Server.Certificates;
using TechConnect.Server.Configuration;
using TechConnect.Server.Connector;
using TechConnect.Server.Copy;
using TechConnect.Server.Diagnostics;
using TechConnect.Server.Inspection;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Ssh.Sessions;

namespace TechConnect.Server.Hosting;

/// <summary>
/// DI and host composition helpers for TechConnect.Server.
/// </summary>
/// <remarks>
/// Lifetimes are intentionally singletons for shared session/attachment state and
/// hosted listeners. Certificate store factories use <see cref="ServiceCollectionDescriptorExtensions.TryAddSingleton{TService,TImplementation}(IServiceCollection)"/>
/// so tests can replace them with ephemeral providers. SSH and connector listeners
/// are independent hosted services — either may fail without stopping the other.
/// </remarks>
public static class HostingExtensions
{
    /// <summary>
    /// Adds TechConnect options, core services, and Serilog to the application builder.
    /// </summary>
    /// <param name="builder">Host application builder to configure.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static HostApplicationBuilder AddTechConnectServerHost(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddTechConnectServerOptions(builder.Configuration);
        builder.Services.AddTechConnectServerCore();
        builder.Services.AddTechConnectServerSerilog(builder.Configuration, builder.Environment.ContentRootPath);

        return builder;
    }

    /// <summary>
    /// Binds and validates <see cref="TechConnectServerOptions"/> and <see cref="FileLoggingOptions"/>.
    /// </summary>
    /// <remarks>
    /// Registers <see cref="AppsettingsHostKeyStore"/> before post-configure so empty
    /// environment host-key overrides can be repaired. Options use
    /// <see cref="OptionsBuilderExtensions.ValidateOnStart{TOptions}(OptionsBuilder{TOptions})"/>.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Root configuration used for binding.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTechConnectServerOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Store must be available before post-configure (empty-override repair).
        services.AddSingleton<AppsettingsHostKeyStore>();

        services
            .AddOptions<TechConnectServerOptions>()
            .Bind(configuration.GetSection(TechConnectServerOptions.SectionName))
            .ValidateOnStart();

        services
            .AddOptions<FileLoggingOptions>()
            .Bind(configuration.GetSection(FileLoggingOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IPostConfigureOptions<TechConnectServerOptions>, SshHostKeyOptionsSetup>();
        services.AddSingleton<IValidateOptions<TechConnectServerOptions>, TechConnectServerOptionsValidator>();
        services.AddSingleton<IValidateOptions<FileLoggingOptions>, FileLoggingOptionsValidator>();

        return services;
    }

    /// <summary>
    /// Registers authentication, connector, SSH CLI, diagnostics, copy, and hosted listeners.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTechConnectServerCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        // TryAdd so tests can replace store/trust providers with ephemeral certificates.
        services.TryAddSingleton<ICertificateStoreFactory, WindowsCertificateStoreFactory>();
        services.TryAddSingleton<IClientCertificateTrustAnchorProvider, WindowsClientCertificateTrustAnchorProvider>();
        services.AddSingleton<ServerCertificateProvider>();
        services.AddSingleton<ConnectorClientCertificateValidator>();
        services.AddSingleton<ConnectorConnectionTracker>();
        services.AddSingleton<ConnectorSessionRegistry>();
        services.AddSingleton<ConnectorSerialConfigurationCache>();
        services.AddSingleton<OperatorAttachmentManager>();
        services.AddSingleton<PendingBaudRequestTracker>();
        services.AddSingleton<PendingSessionPingTracker>();
        services.AddSingleton<IIPv6NetworkAvailability, SystemIPv6NetworkAvailability>();
        services.AddSingleton<IIcmpEchoClient, RawSocketIcmpEchoClient>();
        services.AddSingleton<TechConnect.Server.NameResolution.IDnsResolver, TechConnect.Server.NameResolution.ConfiguredDnsResolver>();
        services.AddSingleton<IDestinationResolver, DestinationResolver>();
        services.AddSingleton<IIcmpTracerouteClient, RawSocketIcmpTracerouteClient>();
        services.AddSingleton<ConnectorDisconnectService>();
        services.AddSingleton<IApplicationInfoProvider, AssemblyApplicationInfoProvider>();
        services.AddSingleton<IDnsLookupService, ConfiguredDnsLookupService>();
        services.AddSingleton<IDnsConfigurationProvider, TechConnectDnsConfigurationProvider>();
        services.AddSingleton<INetworkInterfaceInspector, SystemNetworkInterfaceInspector>();
        services.AddSingleton<IRoutingTableInspector, PlatformRoutingTableInspector>();

        services.AddSingleton<SshHostKeyProvider>();
        services.AddSingleton<OperatorPasswordHasher>();
        services.AddSingleton<LocalOperatorAccountStore>();
        services.AddSingleton<CiscoAvPairPrivilegeMapper>();
        services.AddSingleton<RadiusClientFactory>();
        services.AddSingleton<RadiusPasswordAuthenticationProvider>();
        services.AddSingleton<InMemoryAuditEventStore>();
        services.AddSingleton<IAuditEventWriter>(sp => sp.GetRequiredService<InMemoryAuditEventStore>());
        services.AddSingleton<IAuditEventReader>(sp => sp.GetRequiredService<InMemoryAuditEventStore>());
        services.AddSingleton<OperatorAuthenticationService>();
        services.AddSingleton<EnablePrivilegeService>();
        services.AddSingleton<OperatorAuthorizationService>();
        services.AddSingleton<SshOperatorSessionTracker>();
        services.AddSingleton<TechConnectCliDispatcher>();
        services.AddSingleton<ActiveCopyRegistry>();
        services.AddSingleton<CopyTransferService>();
        services.AddSingleton<ICopyFileSourceFactory>(sp =>
            new CopyFileSourceFactory(sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));

        // Independent listeners — either may fail without stopping the other.
        services.AddHostedService<SshServerHostedService>();
        services.AddHostedService<ConnectorListenerHostedService>();
        return services;
    }

    /// <summary>
    /// Configures Serilog console and rolling-file sinks using <see cref="FileLoggingOptions"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration used for Serilog <c>ReadFrom.Configuration</c>.</param>
    /// <param name="contentRoot">Content root passed to <see cref="LogDirectoryResolver"/>.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTechConnectServerSerilog(
        this IServiceCollection services,
        IConfiguration configuration,
        string contentRoot)
    {
        var fileOptions = configuration.GetSection(FileLoggingOptions.SectionName).Get<FileLoggingOptions>()
            ?? new FileLoggingOptions();

        var logDirectory = LogDirectoryResolver.Resolve(fileOptions.Directory, contentRoot);
        Directory.CreateDirectory(logDirectory);

        var logPath = Path.Combine(logDirectory, $"{fileOptions.FileNamePrefix}-.log");

        services.AddSerilog((_, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "TechConnect.Server")
                .WriteTo.Console()
                .WriteTo.File(
                    path: logPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: fileOptions.RetainedFileCountLimit,
                    fileSizeLimitBytes: fileOptions.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    shared: true);
        });

        return services;
    }
}
