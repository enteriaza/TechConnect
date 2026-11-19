// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    HostingExtensions.cs
//
// Purpose:
// Registers TechConnect.Client options validation, core singletons (including the
// production TCP/mTLS transport), MainForm, and Serilog file/console logging.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using TechConnect.Client.Abstractions;
using TechConnect.Client.Configuration;
using TechConnect.Client.Forms;
using TechConnect.Client.Services;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Hosting;

/// <summary>
/// DI and host composition helpers for TechConnect.Client.
/// </summary>
/// <remarks>
/// Production registers <see cref="TcpMtlsRemoteConsoleTransport"/> as
/// <see cref="IRemoteConsoleTransport"/>. Lifetimes are singletons for shared
/// serial/remote session state used by the WinForms UI.
/// </remarks>
public static class HostingExtensions
{
    /// <summary>
    /// Adds TechConnect.Client options, core services, and Serilog to the application builder.
    /// </summary>
    /// <param name="builder">Host application builder to configure.</param>
    /// <returns>The same <paramref name="builder"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static HostApplicationBuilder AddTechConnectClientHost(this HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddTechConnectClientOptions(builder.Configuration);
        builder.Services.AddTechConnectClientCore();
        builder.Services.AddTechConnectClientSerilog(builder.Configuration, builder.Environment.ContentRootPath);

        return builder;
    }

    /// <summary>
    /// Binds and validates <see cref="RemoteServiceOptions"/> and <see cref="FileLoggingOptions"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration root.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTechConnectClientOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<RemoteServiceOptions>()
            .BindConfiguration(RemoteServiceOptions.SectionName)
            .ValidateOnStart();

        services
            .AddOptions<FileLoggingOptions>()
            .Bind(configuration.GetSection(FileLoggingOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<RemoteServiceOptions>, RemoteServiceOptionsValidator>();
        services.AddSingleton<IValidateOptions<FileLoggingOptions>, FileLoggingOptionsValidator>();

        return services;
    }

    /// <summary>
    /// Registers serial, certificate, remote-session, and UI singletons.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// <see cref="IRemoteConsoleTransport"/> resolves to <see cref="TcpMtlsRemoteConsoleTransport"/>.
    /// </remarks>
    public static IServiceCollection AddTechConnectClientCore(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IJitterProvider, SharedRandomJitterProvider>();
        services.AddSingleton<ISerialPortFactory, SystemSerialPortFactory>();
        services.AddSingleton<ISerialPortEnumerator, SystemSerialPortEnumerator>();
        services.AddSingleton<ISerialPortFriendlyNameLookup, SerialPortFriendlyNameLookup>();
        services.AddSingleton<ICertificateStoreFactory, WindowsCertificateStoreFactory>();

        services.AddSingleton<ISerialPortDiscoveryService, SerialPortDiscoveryService>();
        services.AddSingleton<ISerialConnectionService, SerialConnectionService>();
        services.AddSingleton<IReconnectCoordinator, ReconnectCoordinator>();
        services.AddSingleton<IClientCertificateProvider, ClientCertificateProvider>();
        services.AddSingleton<IRemoteConsoleTransport, TcpMtlsRemoteConsoleTransport>();
        services.AddSingleton<IRemoteSessionService, RemoteSessionService>();

        services.AddSingleton<MainForm>();

        return services;
    }

    /// <summary>
    /// Configures Serilog console and rolling file sinks for TechConnect.Client.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration used for Serilog <c>ReadFrom.Configuration</c>.</param>
    /// <param name="contentRoot">Content root used when resolving relative log directories.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddTechConnectClientSerilog(
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
                .Enrich.WithProperty("Application", "TechConnect.Client")
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
