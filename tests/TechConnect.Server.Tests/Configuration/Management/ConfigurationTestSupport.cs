// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ConfigurationTestSupport.cs
//
// Purpose:
// Handwritten fakes and fixtures for configuration-management tests: a temp
// content root seeded with the shipped appsettings.json, a capturing logger,
// and a fault-injecting configuration file store.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Configuration.Management;
using TechConnect.Server.NameResolution;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Temp content root containing a copy of the shipped <c>appsettings.json</c>.
/// </summary>
internal sealed class ConfigurationFixture : IDisposable
{
    /// <summary>Creates the fixture, optionally replacing the file content.</summary>
    /// <param name="json">Optional appsettings.json content.</param>
    /// <param name="overrides">
    /// Optional higher-precedence provider layered over the file, standing in for environment
    /// variables or <c>appsettings.{Environment}.json</c> without touching the real environment.
    /// </param>
    public ConfigurationFixture(string? json = null, IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Directory.CreateDirectory(ContentRoot);
        File.WriteAllText(FilePath, json ?? ShippedAppsettings());
        var builder = new ConfigurationBuilder()
            .SetBasePath(ContentRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
        if (overrides is not null)
        {
            builder.AddInMemoryCollection(overrides);
        }

        Root = builder.Build();
    }

    /// <summary>Temp content root.</summary>
    public string ContentRoot { get; } = Path.Combine(Path.GetTempPath(), "tc-config-" + Guid.NewGuid().ToString("N"));

    /// <summary>appsettings.json path.</summary>
    public string FilePath => Path.Combine(ContentRoot, "appsettings.json");

    /// <summary>Effective configuration (explicit reload only).</summary>
    public IConfigurationRoot Root { get; }

    /// <summary>Current file text.</summary>
    public string ReadFile() => File.ReadAllText(FilePath);

    /// <summary>The appsettings.json shipped with the server (copied to the test output).</summary>
    public static string ShippedAppsettings() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

    /// <summary>Creates a management service over this fixture.</summary>
    public ConfigurationManagementService CreateService(
        IConfigurationFileStore? store = null,
        ILogger<ConfigurationManagementService>? logger = null,
        ConfiguredDnsResolver? dns = null,
        InMemoryAuditEventStore? audit = null) =>
        new(
            Root,
            store ?? new AppsettingsConfigurationFileStore(ContentRoot),
            logger ?? new CapturingLogger<ConfigurationManagementService>(),
            dns,
            audit,
            audit);

    /// <summary>A level-15 operator.</summary>
    public static AuthenticatedOperator Admin(string username = "admin") =>
        TestAuthenticatedOperator.Create(username, PrivilegeLevel.FromInt(15));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(ContentRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Schema/candidate navigation helpers keyed by CLI keyword paths (for example <c>"authentication radius primary-server"</c>).
/// </summary>
internal static class ConfigPaths
{
    /// <summary>Finds a schema setting by section path and keyword.</summary>
    public static ConfigSetting Setting(string sectionPath, string keyword) =>
        Section(sectionPath).Settings.First(s => s.Keyword == keyword);

    /// <summary>Finds a section by space-separated keyword path.</summary>
    public static ConfigSection Section(string path)
    {
        var section = ConfigurationSchema.Root;
        foreach (var keyword in path.Split(' '))
        {
            section = section.Children.First(c => c.Keyword == keyword);
        }

        return section;
    }

    /// <summary>Candidate object for a section (created when absent).</summary>
    public static JsonObject Node(JsonObject model, string path)
    {
        var node = model;
        var section = ConfigurationSchema.Root;
        foreach (var keyword in path.Split(' '))
        {
            section = section.Children.First(c => c.Keyword == keyword);
            node = ConfigurationModel.ObjectAt(node, section.JsonPath, create: true)!;
        }

        return node;
    }

    /// <summary>Sets a scalar in the candidate.</summary>
    public static void Set(ConfigurationEditSession edit, string section, string keyword, JsonNode? value) =>
        ConfigurationModel.SetValue(Node(edit.Candidate, section), Setting(section, keyword), value);
}

/// <summary>
/// Logger fake that records formatted messages and structured values.
/// </summary>
/// <typeparam name="T">Category.</typeparam>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Captured entries.</summary>
    private readonly List<(LogLevel Level, string Message, string Values)> _entries = [];

    /// <summary>Snapshot of captured entries.</summary>
    public IReadOnlyList<(LogLevel Level, string Message, string Values)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    /// <summary>All captured text (messages, structured values, exceptions).</summary>
    public string AllText => string.Join("\n", Entries.Select(e => e.Message + " | " + e.Values));

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var values = new StringBuilder();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                values.Append(pair.Key).Append('=').Append(pair.Value).Append(';');
            }
        }

        if (exception is not null)
        {
            values.Append(exception);
        }

        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception), values.ToString()));
        }
    }
}

/// <summary>
/// File store fake delegating to a real store with injectable failures and gates.
/// </summary>
internal sealed class FaultInjectingFileStore : IConfigurationFileStore
{
    /// <summary>Real store.</summary>
    private readonly IConfigurationFileStore _inner;

    /// <summary>Creates the fake.</summary>
    public FaultInjectingFileStore(IConfigurationFileStore inner) => _inner = inner;

    /// <summary>Exception thrown by the next write (then cleared).</summary>
    public Exception? FailNextWrite { get; set; }

    /// <summary>When set, writes wait for this gate before delegating.</summary>
    public TaskCompletionSource? WriteGate { get; set; }

    /// <summary>Signalled when a write has started.</summary>
    public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of completed writes.</summary>
    public int Writes { get; private set; }

    /// <inheritdoc />
    public string FilePath => _inner.FilePath;

    /// <inheritdoc />
    public byte[]? ReadAllBytes() => _inner.ReadAllBytes();

    /// <inheritdoc />
    public async Task WriteAtomicallyAsync(byte[] content, CancellationToken cancellationToken)
    {
        WriteStarted.TrySetResult();
        if (WriteGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (FailNextWrite is { } failure)
        {
            FailNextWrite = null;
            throw failure;
        }

        await _inner.WriteAtomicallyAsync(content, cancellationToken);
        Writes++;
    }
}
