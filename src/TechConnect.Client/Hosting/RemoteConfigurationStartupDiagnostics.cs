// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteConfigurationStartupDiagnostics.cs
//
// Purpose:
// One-shot startup logging of remote endpoint configuration shape without
// emitting secrets, private keys, or full configuration dumps.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Hosting;

/// <summary>
/// One-shot startup diagnostics for remote endpoint configuration loading.
/// </summary>
/// <remarks>
/// Does not log secrets, private keys, or full configuration dumps. When a thumbprint
/// is present, only an 8-character prefix is logged.
/// </remarks>
internal static class RemoteConfigurationStartupDiagnostics
{
    /// <summary>
    /// Logs whether the remote endpoint and client-certificate thumbprint appear configured.
    /// </summary>
    /// <param name="host">Built host whose services supply environment and options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
    public static void LogOnce(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var env = host.Services.GetRequiredService<IHostEnvironment>();
        var options = host.Services.GetRequiredService<IOptions<RemoteServiceOptions>>().Value;
        var logger = host.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("TechConnect.Client.Configuration");

        var developmentPath = Path.Combine(env.ContentRootPath, "appsettings.Development.json");
        var developmentExists = File.Exists(developmentPath);
        var configured = RemoteEndpoint.IsConfigured(options);
        var thumbprint = options.ClientCertificate?.Thumbprint?.Trim() ?? string.Empty;
        var thumbprintConfigured = thumbprint.Length > 0;
        // Normalize before prefixing so diagnostics show uppercase hex only (no separators / mixed case).
        var thumbprintPrefix = thumbprintConfigured
            ? RemoteServiceOptionsValidator.FormatThumbprintPrefix(thumbprint)
            : "(none)";
        if (thumbprintConfigured && thumbprintPrefix.Length == 0)
        {
            thumbprintPrefix = "(invalid)";
        }

        logger.LogInformation(
            "Remote configuration: Environment={Environment}, ContentRoot={ContentRoot}, DevelopmentSettingsPresent={DevelopmentSettingsPresent}, EndpointConfigured={EndpointConfigured}, Host={Host}, Port={Port}, TlsHostName={TlsHostName}, ClientCertificateThumbprintConfigured={ClientCertificateThumbprintConfigured}, ClientCertificateThumbprintPrefix={ClientCertificateThumbprintPrefix}",
            env.EnvironmentName,
            env.ContentRootPath,
            developmentExists,
            configured,
            string.IsNullOrWhiteSpace(options.Host) ? "(empty)" : options.Host.Trim(),
            options.Port,
            string.IsNullOrWhiteSpace(options.TlsHostName) ? "(empty)" : options.TlsHostName.Trim(),
            thumbprintConfigured,
            thumbprintPrefix);

        if (!configured &&
            developmentExists &&
            !env.IsDevelopment() &&
            string.IsNullOrWhiteSpace(options.Host))
        {
            logger.LogWarning(
                "RemoteService:Host is empty while appsettings.Development.json is present under the content root, but the host environment is {Environment}. Set DOTNET_ENVIRONMENT=Development (or use the TechConnect.Client launchSettings profile) so Development settings are loaded.",
                env.EnvironmentName);
        }
    }
}
