// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorConfigurationStartupDiagnostics.cs
//
// Purpose:
// Emits a one-shot startup log summarizing connector listener configuration
// loading without dumping secrets, private keys, or full option graphs.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Hosting;

/// <summary>
/// One-shot startup diagnostics for connector listener configuration loading.
/// </summary>
/// <remarks>
/// Does not log secrets, private keys, or full configuration dumps. Logs only a
/// short thumbprint prefix (first 8 hex characters) when a server certificate
/// thumbprint is configured. Warns when the connector is disabled while
/// <c>appsettings.Development.json</c> is present but the host environment is not Development.
/// </remarks>
internal static class ConnectorConfigurationStartupDiagnostics
{
    /// <summary>
    /// Logs connector configuration summary once for the built host.
    /// </summary>
    /// <param name="host">Built generic host whose DI container supplies options and logging.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    public static void LogOnce(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var env = host.Services.GetRequiredService<IHostEnvironment>();
        var options = host.Services.GetRequiredService<IOptions<TechConnectServerOptions>>().Value;
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("TechConnect.Server.Configuration");

        var connector = options.Connector ?? new ConnectorListenerOptions();
        var developmentPath = Path.Combine(env.ContentRootPath, "appsettings.Development.json");
        var developmentExists = File.Exists(developmentPath);
        var thumbprint = connector.ServerCertificateThumbprint?.Trim() ?? string.Empty;
        var thumbprintConfigured = thumbprint.Length > 0;
        var thumbprintPrefix = thumbprintConfigured
            ? thumbprint[..Math.Min(8, thumbprint.Length)]
            : "(none)";

        logger.LogInformation(
            "Connector configuration: Environment={Environment}, ContentRoot={ContentRoot}, DevelopmentSettingsPresent={DevelopmentSettingsPresent}, Enabled={Enabled}, ListenAddress={ListenAddress}, Port={Port}, ServerCertificateThumbprintConfigured={ServerCertificateThumbprintConfigured}, ServerCertificateThumbprintPrefix={ServerCertificateThumbprintPrefix}",
            env.EnvironmentName,
            env.ContentRootPath,
            developmentExists,
            connector.Enabled,
            connector.ListenAddress,
            connector.Port,
            thumbprintConfigured,
            thumbprintPrefix);

        if (!connector.Enabled &&
            developmentExists &&
            !env.IsDevelopment())
        {
            logger.LogWarning(
                "Connector listener is disabled while appsettings.Development.json is present under the content root, but the host environment is {Environment}. Set DOTNET_ENVIRONMENT=Development (or use the TechConnect.Server launchSettings profile) so Development settings are loaded.",
                env.EnvironmentName);
        }
    }
}
