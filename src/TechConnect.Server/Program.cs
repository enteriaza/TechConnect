// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    Program.cs
//
// Purpose:
// Process entry point: optional --hash-password utility, bootstrap Serilog,
// host composition, connector startup diagnostics, and fatal exit handling.
//
// -----------------------------------------------------------------------------

using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Hosting;

namespace TechConnect.Server;

/// <summary>
/// TechConnect.Server process entry point and local password-hashing utility.
/// </summary>
public static class Program
{
    /// <summary>
    /// Application entry point.
    /// </summary>
    /// <remarks>
    /// When <c>--hash-password</c> / <c>hash-password</c> is present, returns after
    /// writing an Identity V3 hash to stdout without starting the host. Otherwise
    /// sets the working directory to the content root, creates a bootstrap logger,
    /// builds the generic host via <see cref="HostingExtensions.AddTechConnectServerHost"/>,
    /// logs connector configuration once, and runs until shutdown.
    /// </remarks>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>
    /// <c>0</c> on clean stop or successful hash; <c>1</c> on unexpected failure or
    /// options validation failure; <c>2</c> on hash-password usage/validation errors.
    /// </returns>
    public static async Task<int> Main(string[] args)
    {
        if (TryHandleHashPassword(args, out var hashExitCode))
        {
            return hashExitCode;
        }

        var contentRoot = AppContext.BaseDirectory;
        Directory.SetCurrentDirectory(contentRoot);

        var bootstrapLogDirectory = LogDirectoryResolver.Resolve(configuredDirectory: null, contentRoot);
        Directory.CreateDirectory(bootstrapLogDirectory);
        var bootstrapLogPath = Path.Combine(bootstrapLogDirectory, "techconnect-server-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "TechConnect.Server")
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
            Log.Information("TechConnect.Server starting");

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = contentRoot
            });

            builder.AddTechConnectServerHost();

            using var host = builder.Build();
            ConnectorConfigurationStartupDiagnostics.LogOnce(host);
            await host.RunAsync().ConfigureAwait(false);

            Log.Information("TechConnect.Server stopped");
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
            Log.Fatal(ex, "TechConnect.Server terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Generates an ASP.NET Identity V3 password hash for <c>appsettings.json</c>.
    /// </summary>
    /// <remarks>
    /// Usage: <c>TechConnect.Server --hash-password &lt;username&gt;</c>
    /// (also accepts <c>hash-password</c>). Password is read from stdin (or a console
    /// prompt when interactive). The plaintext password is never written to application logs.
    /// </remarks>
    /// <param name="args">Process arguments.</param>
    /// <param name="exitCode">Process exit code when this path handles the request.</param>
    /// <returns>
    /// <see langword="true"/> when the hash-password command was recognized (handled);
    /// <see langword="false"/> to continue normal host startup.
    /// </returns>
    private static bool TryHandleHashPassword(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0];
        if (!string.Equals(command, "--hash-password", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command, "hash-password", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: TechConnect.Server --hash-password <username>");
            Console.Error.WriteLine("Password is read from stdin (one line) or an interactive prompt.");
            exitCode = 2;
            return true;
        }

        var username = args[1].Trim();
        if (!OperatorUsernameRules.IsValid(username))
        {
            Console.Error.WriteLine(
                "Username is invalid. Use 1–64 characters: letters, digits, '.', '_', '@', '+', '-'.");
            exitCode = 2;
            return true;
        }

        string? password;
        try
        {
            password = ReadPassword();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Unable to read password: {ex.Message}");
            exitCode = 1;
            return true;
        }

        if (string.IsNullOrEmpty(password))
        {
            Console.Error.WriteLine("Password must not be empty.");
            exitCode = 2;
            return true;
        }

        var hasher = new OperatorPasswordHasher();
        var hash = hasher.HashPassword(username, password);
        Console.WriteLine(hash);
        exitCode = 0;
        return true;
    }

    /// <summary>
    /// Reads a password from redirected stdin or an interactive console prompt (no echo).
    /// </summary>
    /// <returns>Password text, or <see langword="null"/> when stdin yields no line.</returns>
    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write("Password: ");
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }

        return buffer.ToString();
    }
}
