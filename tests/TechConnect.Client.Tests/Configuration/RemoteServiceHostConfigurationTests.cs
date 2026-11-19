// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    RemoteServiceHostConfigurationTests.cs
//
// Purpose:
// Verifies RemoteServiceHost configuration binding and validation for TCP/mTLS hosts.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TechConnect.Client.Configuration;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Configuration;

/// <summary>
/// Unit tests for RemoteServiceHost configuration binding and validation.
/// </summary>
public sealed class RemoteServiceHostConfigurationTests
{
    /// <summary>
    /// Scenario: Production host with empty RemoteService:Host in appsettings.
    /// Expected: options bind empty host and remote remains unconfigured.
    /// </summary>
    [Fact]
    public void ProductionDefaults_LeaveHostEmpty_AndRemoteUnconfigured()
    {
        using var dir = new TempContentRoot();
        File.WriteAllText(
            Path.Combine(dir.Path, "appsettings.json"),
            """
            {
              "RemoteService": {
                "Host": "",
                "Port": 9443,
                "TlsHostName": "",
                "ClientCertificate": { "Thumbprint": "" }
              },
              "Logging": { "File": { "Directory": "" } }
            }
            """);

        using var host = BuildHost(dir.Path, environmentName: Environments.Production);
        var options = host.Services.GetRequiredService<IOptions<RemoteServiceOptions>>().Value;

        options.Host.Should().BeEmpty();
        options.Port.Should().Be(9443);
        RemoteEndpoint.IsConfigured(options).Should().BeFalse();
    }

    /// <summary>
    /// Scenario: Development overlay supplies host/port/TLS/thumbprint.
    /// Expected: options bind those values and IsConfigured is true.
    /// </summary>
    [Fact]
    public void DevelopmentOverlay_BindsHostPortTlsAndThumbprint_AndMarksConfigured()
    {
        using var dir = new TempContentRoot();
        File.WriteAllText(
            Path.Combine(dir.Path, "appsettings.json"),
            """
            {
              "RemoteService": {
                "Host": "",
                "Port": 9443,
                "TlsHostName": "",
                "ClientCertificate": { "Thumbprint": "" }
              },
              "Logging": { "File": { "Directory": "" } }
            }
            """);
        File.WriteAllText(
            Path.Combine(dir.Path, "appsettings.Development.json"),
            """
            {
              "RemoteService": {
                "Host": "127.0.0.1",
                "Port": 9443,
                "TlsHostName": "localhost",
                "ClientCertificate": {
                  "Thumbprint": "6DDB9305515BC495739B9D50B34C3621D02BEC61",
                  "StoreName": "My",
                  "StoreLocation": "CurrentUser"
                }
              }
            }
            """);

        using var host = BuildHost(dir.Path, environmentName: Environments.Development);
        var env = host.Services.GetRequiredService<IHostEnvironment>();
        var options = host.Services.GetRequiredService<IOptions<RemoteServiceOptions>>().Value;

        env.EnvironmentName.Should().Be(Environments.Development);
        env.ContentRootPath.Should().Be(dir.Path);
        options.Host.Should().Be("127.0.0.1");
        options.Port.Should().Be(9443);
        options.TlsHostName.Should().Be("localhost");
        options.ClientCertificate.Thumbprint.Should().Be("6DDB9305515BC495739B9D50B34C3621D02BEC61");
        RemoteEndpoint.IsConfigured(options).Should().BeTrue();
    }

    /// <summary>
    /// Scenario: Development appsettings present but environment is Production.
    /// Expected: Development overlay is ignored; production defaults win.
    /// </summary>
    [Fact]
    public void DevelopmentFileIgnored_WhenEnvironmentIsProduction()
    {
        using var dir = new TempContentRoot();
        File.WriteAllText(
            Path.Combine(dir.Path, "appsettings.json"),
            """
            {
              "RemoteService": {
                "Host": "",
                "Port": 9443,
                "TlsHostName": ""
              },
              "Logging": { "File": { "Directory": "" } }
            }
            """);
        File.WriteAllText(
            Path.Combine(dir.Path, "appsettings.Development.json"),
            """
            {
              "RemoteService": {
                "Host": "127.0.0.1",
                "Port": 9443,
                "TlsHostName": "localhost"
              }
            }
            """);

        using var host = BuildHost(dir.Path, environmentName: Environments.Production);
        var options = host.Services.GetRequiredService<IOptions<RemoteServiceOptions>>().Value;

        options.Host.Should().BeEmpty();
        RemoteEndpoint.IsConfigured(options).Should().BeFalse();
    }

    /// <summary>
    /// Builds a minimal host that binds RemoteService/FileLogging options from <paramref name="contentRoot"/>.
    /// </summary>
    private static IHost BuildHost(string contentRoot, string environmentName)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = environmentName,
            Args = []
        });

        // Avoid Serilog file sinks writing into the repo during unit tests.
        builder.Services.AddOptions<RemoteServiceOptions>()
            .BindConfiguration(RemoteServiceOptions.SectionName)
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<RemoteServiceOptions>, RemoteServiceOptionsValidator>();
        builder.Services.AddOptions<FileLoggingOptions>()
            .Bind(builder.Configuration.GetSection(FileLoggingOptions.SectionName));
        builder.Services.AddSingleton(TimeProvider.System);

        return builder.Build();
    }

    /// <summary>
    /// Temporary content-root directory for isolated host configuration tests; deleted on Dispose.
    /// </summary>
    private sealed class TempContentRoot : IDisposable
    {
        /// <summary>
        /// Absolute path of the unique temporary content-root directory.
        /// </summary>
        public string Path { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TechConnectClientConfigTests-" + Guid.NewGuid().ToString("N"));

        /// <summary>
        /// Creates the temporary directory on disk.
        /// </summary>
        public TempContentRoot()
        {
            Directory.CreateDirectory(Path);
        }

        /// <summary>
        /// Deletes the temporary content-root directory.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp test roots.
            }
        }
    }
}
