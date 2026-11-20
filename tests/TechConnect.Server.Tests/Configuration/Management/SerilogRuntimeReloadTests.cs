// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SerilogRuntimeReloadTests.cs
//
// Purpose:
// Demonstrates which Serilog settings follow an IConfigurationRoot.Reload()
// when the logger is built with ReadFrom.Configuration (as Program/AddSerilog does).
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Evidence for the runtime/restart classification of Serilog settings.
/// </summary>
public sealed class SerilogRuntimeReloadTests : IDisposable
{
    /// <summary>Temporary content root.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-serilog-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temp directory.</summary>
    public SerilogRuntimeReloadTests() => Directory.CreateDirectory(_dir);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Writes appsettings.json with the given Serilog MinimumLevel JSON.</summary>
    private void WriteSettings(string minimumLevelJson) =>
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), "{ \"Serilog\": { \"MinimumLevel\": " + minimumLevelJson + " } }");

    /// <summary>Builds a configuration root (no file watcher; reload is explicit) and logger.</summary>
    private (IConfigurationRoot Root, Logger Logger) Build()
    {
        var root = new ConfigurationBuilder()
            .SetBasePath(_dir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();
        return (root, new LoggerConfiguration().ReadFrom.Configuration(root).CreateLogger());
    }

    /// <summary>Default minimum level follows reload.</summary>
    [Fact]
    public void DefaultMinimumLevel_FollowsConfigurationReload()
    {
        WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Warning\" } }");
        var (root, logger) = Build();
        using (logger)
        {
            logger.IsEnabled(LogEventLevel.Debug).Should().BeFalse();

            WriteSettings("{ \"Default\": \"Debug\", \"Override\": { \"Microsoft\": \"Warning\" } }");
            root.Reload();
            logger.IsEnabled(LogEventLevel.Debug).Should().BeTrue();

            WriteSettings("{ \"Default\": \"Error\", \"Override\": { \"Microsoft\": \"Warning\" } }");
            root.Reload();
            logger.IsEnabled(LogEventLevel.Warning).Should().BeFalse();
            logger.IsEnabled(LogEventLevel.Error).Should().BeTrue();
        }
    }

    /// <summary>An override source present at startup follows reload.</summary>
    [Fact]
    public void ExistingOverrideLevel_FollowsConfigurationReload()
    {
        WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Warning\" } }");
        var (root, logger) = Build();
        using (logger)
        {
            var microsoft = logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.Hosting");
            microsoft.IsEnabled(LogEventLevel.Information).Should().BeFalse();

            WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Debug\" } }");
            root.Reload();
            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.Hosting")
                .IsEnabled(LogEventLevel.Debug).Should().BeTrue();
        }
    }

    /// <summary>
    /// An override source added after startup does not take effect on reload, so adding a
    /// source is restart-required.
    /// </summary>
    [Fact]
    public void AddedOverrideSource_DoesNotTakeEffectOnReload()
    {
        WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Warning\" } }");
        var (root, logger) = Build();
        using (logger)
        {
            WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Warning\", \"Contoso\": \"Error\" } }");
            root.Reload();

            logger.ForContext(Constants.SourceContextPropertyName, "Contoso.Widgets")
                .IsEnabled(LogEventLevel.Information).Should().BeTrue("added sources are only honoured after a restart");
        }
    }

    /// <summary>
    /// Records what removing an override source does on reload.
    /// </summary>
    [Fact]
    public void RemovedOverrideSource_KeepsPreviousLevelUntilRestart()
    {
        WriteSettings("{ \"Default\": \"Information\", \"Override\": { \"Microsoft\": \"Warning\" } }");
        var (root, logger) = Build();
        using (logger)
        {
            WriteSettings("{ \"Default\": \"Information\", \"Override\": { } }");
            root.Reload();

            logger.ForContext(Constants.SourceContextPropertyName, "Microsoft.Hosting")
                .IsEnabled(LogEventLevel.Information).Should().BeFalse("the source keeps its last level until a restart");
        }
    }
}
