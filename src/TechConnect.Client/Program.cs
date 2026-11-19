// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    Program.cs
//
// Purpose:
// WinForms process entry point: bootstrap Serilog, host composition, remote
// configuration diagnostics, UI run loop, and fatal exit handling.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using TechConnect.Client.Forms;
using TechConnect.Client.Hosting;

namespace TechConnect.Client;

/// <summary>
/// TechConnect.Client process entry point for the WinForms technician console.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    /// <remarks>
    /// Sets the working directory to the content root, creates a bootstrap Serilog
    /// logger, builds the generic host via <see cref="HostingExtensions.AddTechConnectClientHost"/>,
    /// logs remote configuration once, starts the host, runs <see cref="MainForm"/> on the
    /// STA UI thread, then stops the host with a 10-second bound.
    /// </remarks>
    /// <param name="args">Command-line arguments forwarded to the host builder.</param>
    /// <returns>
    /// <c>0</c> on clean stop; <c>1</c> on options validation failure or unexpected termination.
    /// </returns>
    [STAThread]
    public static int Main(string[] args)
    {
        var contentRoot = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(contentRoot);

        var bootstrapLogDirectory = LogDirectoryResolver.Resolve(configuredDirectory: null, contentRoot);
        Directory.CreateDirectory(bootstrapLogDirectory);
        var bootstrapLogPath = Path.Combine(bootstrapLogDirectory, "techconnector-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "TechConnect.Client")
            .WriteTo.Console()
            .WriteTo.File(
                path: bootstrapLogPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("TechConnect.Client starting");

            ApplicationConfiguration.Initialize();

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = contentRoot
            });

            builder.AddTechConnectClientHost();

            using var host = builder.Build();

            RemoteConfigurationStartupDiagnostics.LogOnce(host);

            host.Start();
            Log.Information("TechConnect.Client host started");

            try
            {
                var mainForm = host.Services.GetRequiredService<MainForm>();
                Application.Run(mainForm);
            }
            finally
            {
                Log.Information("TechConnect.Client shutting down");
                host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }

            Log.Information("TechConnect.Client stopped");
            return 0;
        }
        catch (OptionsValidationException ex)
        {
            Log.Fatal(ex, "Configuration validation failed");
            foreach (var failure in ex.Failures)
            {
                Log.Fatal("Configuration error: {Failure}", failure);
            }

            return 1;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "TechConnect.Client terminated unexpectedly");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
