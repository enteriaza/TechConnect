// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfigurationManagementService.cs
//
// Purpose:
// Running / startup / candidate configuration views, full candidate validation
// with the real startup validators, serialized conflict-checked atomic writes
// of appsettings.json, and post-persistence runtime application.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Configuration.Management;

/// <summary>Outcome of a <c>write</c>.</summary>
internal enum ConfigurationWriteStatus
{
    /// <summary>Persisted successfully.</summary>
    Saved,

    /// <summary>Candidate failed validation; nothing was written.</summary>
    ValidationFailed,

    /// <summary>appsettings.json changed since the candidate baseline; nothing was written.</summary>
    Conflict,

    /// <summary>I/O failure; the original file is intact.</summary>
    Failed,

    /// <summary>Cancelled before the file was replaced; the original file is intact.</summary>
    Cancelled
}

/// <summary>
/// Result of a <c>write</c>.
/// </summary>
/// <param name="Status">Outcome.</param>
/// <param name="Messages">Operator-facing failure messages, or notes for a successful save (never secret values).</param>
/// <param name="AppliedAtRuntime">Setting paths applied to the running server.</param>
/// <param name="RestartRequired">Saved setting paths that take effect after a restart.</param>
/// <param name="RuntimeApplyFailures">Runtime settings that were saved but could not be applied.</param>
internal sealed record ConfigurationWriteResult(
    ConfigurationWriteStatus Status,
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> AppliedAtRuntime,
    IReadOnlyList<string> RestartRequired,
    IReadOnlyList<string> RuntimeApplyFailures)
{
    /// <summary>Creates a non-saved result with messages.</summary>
    public static ConfigurationWriteResult NotSaved(ConfigurationWriteStatus status, params string[] messages) =>
        new(status, messages, [], [], []);
}

/// <summary>
/// Validated, bound view of a merged configuration document.
/// </summary>
/// <param name="Failures">Validation failures (empty when valid).</param>
/// <param name="FileBytes">
/// Startup file merged with the full candidate — the effective view that is validated; never persisted
/// (null when the startup file cannot be parsed).
/// </param>
/// <param name="Server">Bound <see cref="TechConnectServerOptions"/> (null when binding failed).</param>
internal sealed record ConfigurationValidationResult(
    IReadOnlyList<string> Failures,
    byte[]? FileBytes,
    TechConnectServerOptions? Server)
{
    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid => Failures.Count == 0 && FileBytes is not null && Server is not null;
}

/// <summary>
/// Owns configuration-mode persistence and the running-configuration view.
/// </summary>
/// <remarks>
/// <para>
/// <b>Running</b>: values the server is using — captured from the effective configuration when
/// this singleton is created at host startup, then updated only for runtime-applicable settings
/// after a successful write. <c>Serilog:MinimumLevel</c> follows configuration reloads, so it is
/// read live. <b>Startup</b>: <c>appsettings.json</c> on disk. <b>Candidate</b>: per-operator
/// copy held by <see cref="ConfigurationEditSession"/>.
/// </para>
/// <para>
/// Writes are serialized; each write re-hashes the file and refuses to overwrite it when it
/// changed since the candidate's baseline. Secret values are never logged.
/// </para>
/// </remarks>
internal sealed class ConfigurationManagementService
{
    /// <summary>Hash used for a missing startup file.</summary>
    private const string MissingFileHash = "absent";

    /// <summary>Effective configuration (all providers).</summary>
    private readonly IConfiguration _configuration;

    /// <summary>Startup file persistence.</summary>
    private readonly IConfigurationFileStore _fileStore;

    /// <summary>Logger (paths and counts only; never values).</summary>
    private readonly ILogger<ConfigurationManagementService> _logger;

    /// <summary>Optional runtime DNS target.</summary>
    private readonly ConfiguredDnsResolver? _dnsResolver;

    /// <summary>Optional runtime audit target.</summary>
    private readonly InMemoryAuditEventStore? _auditStore;

    /// <summary>Audit writer.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Serializes writes (and edit-session baselines) against each other.</summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Guards <see cref="_running"/>.</summary>
    private readonly object _runningGate = new();

    /// <summary>Serilog override sources the logger was built with (only these follow reload).</summary>
    private readonly HashSet<string> _startupOverrideSources;

    /// <summary>Running configuration snapshot (Serilog levels are overlaid live in <see cref="GetRunning"/>).</summary>
    private JsonObject _running;

    /// <summary>
    /// Creates the service and captures the running configuration.
    /// </summary>
    /// <param name="configuration">Effective host configuration.</param>
    /// <param name="fileStore">Startup file store.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="dnsResolver">Optional resolver that accepts runtime DNS changes.</param>
    /// <param name="auditStore">Optional audit store that accepts runtime audit changes.</param>
    /// <param name="audit">Optional audit writer.</param>
    public ConfigurationManagementService(
        IConfiguration configuration,
        IConfigurationFileStore fileStore,
        ILogger<ConfigurationManagementService> logger,
        ConfiguredDnsResolver? dnsResolver = null,
        InMemoryAuditEventStore? auditStore = null,
        IAuditEventWriter? audit = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dnsResolver = dnsResolver;
        _auditStore = auditStore;
        _audit = audit ?? auditStore as IAuditEventWriter ?? new NoOpAuditEventWriter();
        _running = ConfigurationModel.Read(_configuration);
        var overrides = ConfigurationModel.GetValue(
            ConfigurationModel.ObjectAt(_running, SerilogSection.JsonPath, create: false) ?? new JsonObject(),
            SerilogSection.Settings.First(s => s.JsonName == "MinimumLevel:Override")) as JsonObject;
        _startupOverrideSources = new HashSet<string>(
            overrides?.Select(p => p.Key) ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Absolute path of the startup configuration file.</summary>
    public string StartupFilePath => _fileStore.FilePath;

    /// <summary>
    /// Begins a private configuration session from the current effective configuration.
    /// </summary>
    /// <param name="identity">Operator entering configuration mode.</param>
    /// <param name="notes">Informational notes for the operator (may be empty).</param>
    /// <returns>New edit session.</returns>
    public ConfigurationEditSession BeginEdit(AuthenticatedOperator identity, out IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _writeLock.Wait();
        try
        {
            // Pick up any external edit now so the baseline hash and effective view agree.
            (_configuration as IConfigurationRoot)?.Reload();
            var bytes = _fileStore.ReadAllBytes();
            var effective = ConfigurationModel.Read(_configuration);
            var session = new ConfigurationEditSession(identity.Username, identity.OperatorSessionId, effective, Hash(bytes));

            var list = new List<string>();
            try
            {
                var overridden = ConfigurationModel.ChangedEntries(ConfigurationModel.ReadJson(bytes), effective).Count;
                if (overridden > 0)
                {
                    list.Add(
                        $"% Note: {overridden} setting(s) differ from startup-config because another configuration " +
                        "source overrides them; 'write' saves only the settings you change, never the overriding values.");
                }
            }
            catch (InvalidDataException)
            {
                list.Add("% Warning: startup-config is not valid JSON; 'write' will be refused until it is repaired.");
            }

            notes = list;
            _logger.LogInformation(
                "Operator {Operator} entered configuration mode (session {SessionId})",
                identity.Username,
                identity.OperatorSessionId);
            _audit.TryWrite(
                AuditEventType.ConfigurationModeEntered,
                AuditEventOutcome.Informational,
                "Entered configuration mode",
                new AuditEventContext { ActorUsername = identity.Username, SessionId = identity.OperatorSessionId });
            return session;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Returns a copy of the running configuration.</summary>
    public JsonObject GetRunning()
    {
        JsonObject running;
        lock (_runningGate)
        {
            running = (JsonObject)_running.DeepClone();
        }

        // The Serilog reader re-evaluates the default level and the levels of override sources
        // that existed at startup on configuration reload; added or removed sources need a
        // restart (SerilogRuntimeReloadTests).
        var live = ConfigurationModel.Read(_configuration);
        var serilog = SerilogSection;
        var serilogRunning = ConfigurationModel.ObjectAt(running, serilog.JsonPath, create: true)!;
        var serilogLive = ConfigurationModel.ObjectAt(live, serilog.JsonPath, create: false) ?? new JsonObject();
        var defaultLevel = serilog.Settings.First(s => s.JsonName == "MinimumLevel:Default");
        ConfigurationModel.SetValue(serilogRunning, defaultLevel, ConfigurationModel.GetValue(serilogLive, defaultLevel));

        var overrides = serilog.Settings.First(s => s.JsonName == "MinimumLevel:Override");
        var runningOverrides = ConfigurationModel.GetValue(serilogRunning, overrides) as JsonObject ?? new JsonObject();
        var liveOverrides = ConfigurationModel.GetValue(serilogLive, overrides) as JsonObject ?? new JsonObject();
        var effective = new JsonObject(ConfigurationModel.NodeOptions);
        foreach (var pair in runningOverrides)
        {
            var value = _startupOverrideSources.Contains(pair.Key) && liveOverrides.TryGetPropertyValue(pair.Key, out var liveValue)
                ? liveValue
                : pair.Value;
            effective[pair.Key] = value?.DeepClone();
        }

        ConfigurationModel.SetValue(serilogRunning, overrides, effective);
        return running;
    }

    /// <summary>The <c>serilog</c> schema section.</summary>
    private static ConfigSection SerilogSection => ConfigurationSchema.Root.Children.First(c => c.Keyword == "serilog");

    /// <summary>Renders <c>show running-config</c>, including saved changes awaiting restart.</summary>
    public string RenderRunning()
    {
        var running = GetRunning();
        var text = ConfigurationModel.Render(running, "Current configuration (running):");
        var pending = GetPendingRestart(running);
        if (pending.Count == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text);
        sb.Append("\r\n! Saved changes awaiting a server restart (not active):");
        foreach (var path in pending)
        {
            sb.Append("\r\n!   ").Append(path);
        }

        return sb.ToString();
    }

    /// <summary>Renders <c>show startup-config</c> from appsettings.json.</summary>
    public string RenderStartup()
    {
        byte[]? bytes;
        try
        {
            bytes = _fileStore.ReadAllBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "% Unable to read startup-config.";
        }

        if (bytes is null)
        {
            return "% startup-config is not present.";
        }

        try
        {
            return ConfigurationModel.Render(ConfigurationModel.ReadJson(bytes), "Startup configuration (appsettings.json):");
        }
        catch (InvalidDataException)
        {
            return "% startup-config is not valid JSON.";
        }
    }

    /// <summary>Saved restart-required setting paths whose values are not yet active.</summary>
    public IReadOnlyList<string> GetPendingRestart() => GetPendingRestart(GetRunning());

    /// <summary>Pending restart paths relative to <paramref name="running"/>.</summary>
    private IReadOnlyList<string> GetPendingRestart(JsonObject running) =>
        ConfigurationModel.ChangedEntries(running, ConfigurationModel.Read(_configuration))
            .Select(e => e.Path)
            .ToList();

    /// <summary>
    /// Validates a candidate exactly as it would be written: merged into the current startup file,
    /// bound to the options classes, and checked by the real startup validators.
    /// </summary>
    /// <param name="candidate">Candidate model.</param>
    /// <returns>Validation result.</returns>
    public ConfigurationValidationResult Validate(JsonObject candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Validate(candidate, ReadStartupBytesForMerge(out var readError), readError);
    }

    /// <summary>Validation core over already-read startup bytes.</summary>
    private static ConfigurationValidationResult Validate(JsonObject candidate, byte[]? startupBytes, string? readError)
    {
        var failures = new List<string>();
        if (readError is not null)
        {
            failures.Add(readError);
            return new ConfigurationValidationResult(failures, null, null);
        }

        ConfigurationModel.ValidateValues(candidate, failures);

        JsonObject root;
        try
        {
            root = startupBytes is { Length: > 0 }
                ? JsonNode.Parse(startupBytes, ConfigurationModel.NodeOptions, ConfigurationModel.FileParseOptions) as JsonObject
                  ?? throw new JsonException()
                : new JsonObject(ConfigurationModel.NodeOptions);
        }
        catch (JsonException)
        {
            failures.Add("startup-config (appsettings.json) is not a valid JSON object; it must be repaired before writing.");
            return new ConfigurationValidationResult(failures, null, null);
        }

        ConfigurationModel.MergeInto(root, candidate);
        var bytes = Serialize(root);

        var server = new TechConnectServerOptions();
        var fileLogging = new FileLoggingOptions();
        try
        {
            var bound = new ConfigurationBuilder().AddJsonStream(new MemoryStream(bytes, writable: false)).Build();
            bound.GetSection(TechConnectServerOptions.SectionName).Bind(server);
            bound.GetSection(FileLoggingOptions.SectionName).Bind(fileLogging);
        }
        catch (InvalidOperationException)
        {
            // Binder messages may echo values; report generically.
            failures.Add("One or more settings have a value of the wrong type.");
            return new ConfigurationValidationResult(failures, bytes, null);
        }

        var serverResult = new TechConnectServerOptionsValidator().Validate(null, server);
        if (serverResult.Failed)
        {
            failures.AddRange(serverResult.Failures);
        }

        var loggingResult = new FileLoggingOptionsValidator().Validate(null, fileLogging);
        if (loggingResult.Failed)
        {
            failures.AddRange(loggingResult.Failures);
        }

        return new ConfigurationValidationResult(failures, bytes, server);
    }

    /// <summary>
    /// Validates and atomically persists the candidate, then applies runtime-applicable settings.
    /// </summary>
    /// <param name="session">Operator edit session.</param>
    /// <param name="cancellationToken">Cancels before the file is replaced.</param>
    /// <returns>Write result.</returns>
    public async Task<ConfigurationWriteResult> WriteAsync(ConfigurationEditSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ConfigurationWriteResult.NotSaved(ConfigurationWriteStatus.Cancelled, "% Write cancelled. Configuration not saved.");
        }

        try
        {
            var startupBytes = ReadStartupBytesForMerge(out var readError);
            if (readError is null && Hash(startupBytes) != session.StartupFileHash)
            {
                return Refuse(
                    session,
                    ConfigurationWriteStatus.Conflict,
                    "conflict",
                    "% startup-config (appsettings.json) was modified after this configuration session began",
                    "% (by another operator or externally). Configuration not saved.",
                    "% Review 'show startup-config', then 'end' and 'configure terminal' to edit the current configuration.");
            }

            var validation = Validate(session.Candidate, startupBytes, readError);
            if (!validation.IsValid)
            {
                var messages = new List<string> { "% Configuration validation failed. Configuration not saved." };
                messages.AddRange(validation.Failures.Select(f => "%   " + f));
                return Refuse(session, ConfigurationWriteStatus.ValidationFailed, "validation", [.. messages]);
            }

            // Validation used the full effective view (what the next start sees); the file receives only
            // the operator's changes so values from other providers (for example environment secrets)
            // are never copied into it.
            var fileBytes = BuildFileBytes(startupBytes, session.Baseline, session.Candidate);
            var overriddenChanges = OverriddenPaths(startupBytes)
                .Intersect(ConfigurationModel.ChangedEntries(session.Baseline, session.Candidate).Select(e => e.Path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            try
            {
                await _fileStore.WriteAtomicallyAsync(fileBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Refuse(session, ConfigurationWriteStatus.Cancelled, "cancelled", "% Write cancelled. Configuration not saved.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogWarning(
                    "Configuration write to {ConfigurationFile} failed for {Operator}: {ErrorType}",
                    _fileStore.FilePath,
                    session.OperatorUsername,
                    ex.GetType().Name);
                return Refuse(session, ConfigurationWriteStatus.Failed, "io", "% Unable to write startup-config. The existing file is unchanged.");
            }

            // Persisted: from here on nothing may be reported as unsaved.
            var previousRunning = GetRunning();
            var changed = ConfigurationModel.ChangedEntries(session.Baseline, session.Candidate);
            session.MarkSaved(Hash(fileBytes));

            // Reload so the effective configuration reflects the file just written (file watching is
            // asynchronous); runtime values and the report follow the effective view, so a change that
            // another provider overrides is never claimed as applied.
            var reloaded = TryReloadConfiguration();
            var effective = reloaded ? ConfigurationModel.Read(_configuration) : session.Candidate;
            var effectiveServer = reloaded ? BindEffectiveServer() ?? validation.Server! : validation.Server!;
            var (applied, applyFailures) = ApplyRuntime(previousRunning, effective, effectiveServer, reloaded);
            var restart = ConfigurationModel.ChangedEntries(GetRunning(), effective)
                .Select(e => e.Path)
                .Except(applyFailures, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var notes = overriddenChanges.Count == 0
                ? []
                : new[]
                {
                    "% Note: saved to appsettings.json, but another configuration source (for example an environment variable) " +
                    "overrides and still takes precedence for: " + string.Join(", ", overriddenChanges)
                };

            _logger.LogInformation(
                "Operator {Operator} wrote configuration to {ConfigurationFile}: {ChangedCount} setting(s) changed ({ChangedPaths}); runtime-applied={RuntimeCount}; restart-required={RestartCount}",
                session.OperatorUsername,
                _fileStore.FilePath,
                changed.Count,
                string.Join(", ", changed.Select(c => c.Path)),
                applied.Count,
                restart.Count);
            _audit.TryWrite(
                AuditEventType.ConfigurationSaved,
                AuditEventOutcome.Success,
                "Configuration written to startup-config",
                new AuditEventContext
                {
                    ActorUsername = session.OperatorUsername,
                    SessionId = session.OperatorSessionId,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["changed"] = changed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["restartRequired"] = restart.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }
                });

            return new ConfigurationWriteResult(ConfigurationWriteStatus.Saved, notes, applied, restart, applyFailures);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Discards a session's unsaved changes (logs counts only).
    /// </summary>
    /// <param name="session">Edit session being abandoned.</param>
    /// <param name="reason">Short reason, for example <c>end</c> or <c>disconnect</c>.</param>
    /// <returns>Number of discarded setting changes.</returns>
    public int Discard(ConfigurationEditSession session, string reason)
    {
        ArgumentNullException.ThrowIfNull(session);
        var count = session.HasUnsavedChanges ? session.UnsavedChangeCount : 0;
        if (count > 0)
        {
            _logger.LogInformation(
                "Discarded {Count} unsaved configuration change(s) for {Operator} ({Reason})",
                count,
                session.OperatorUsername,
                reason);
            _audit.TryWrite(
                AuditEventType.ConfigurationDiscarded,
                AuditEventOutcome.Informational,
                "Unsaved configuration changes discarded",
                new AuditEventContext
                {
                    ActorUsername = session.OperatorUsername,
                    SessionId = session.OperatorSessionId,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["reason"] = reason
                    }
                });
        }
        else
        {
            _logger.LogDebug("Operator {Operator} left configuration mode ({Reason})", session.OperatorUsername, reason);
        }

        return count;
    }

    /// <summary>
    /// Applies runtime-applicable settings after persistence. Each target receives one complete,
    /// already-validated value set; the running snapshot is updated only for targets that applied.
    /// </summary>
    private (IReadOnlyList<string> Applied, IReadOnlyList<string> Failures) ApplyRuntime(
        JsonObject previousRunning,
        JsonObject candidate,
        TechConnectServerOptions server,
        bool reloaded)
    {
        var runtimeChanges = ConfigurationModel.ChangedEntries(previousRunning, candidate)
            .Where(e => e.ApplyMode == ConfigApplyMode.Runtime)
            .ToList();
        var failedPrefixes = new List<string>();

        void Apply(string pathPrefix, bool available, Action apply, Func<ConfigSetting, bool>? copyToRunning)
        {
            if (!runtimeChanges.Any(e => e.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!available)
            {
                failedPrefixes.Add(pathPrefix);
                return;
            }

            try
            {
                apply();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or FormatException)
            {
                _logger.LogWarning("Runtime configuration apply failed for {Section}: {ErrorType}", pathPrefix.Trim(), ex.GetType().Name);
                failedPrefixes.Add(pathPrefix);
                return;
            }

            if (copyToRunning is not null)
            {
                lock (_runningGate)
                {
                    CopySettings(candidate, _running, copyToRunning);
                }
            }
        }

        Apply("dns ", _dnsResolver is not null, () => _dnsResolver!.ApplyOptions(server.Dns), s => IsInSection(s, "dns"));
        Apply("audit ", _auditStore is not null, () => _auditStore!.ApplyOptions(server.Audit), s => IsInSection(s, "audit"));
        Apply("serilog ", reloaded, static () => { }, copyToRunning: null);

        // Report against what is actually running now, so partial support (for example Serilog
        // override sources added after startup) is never claimed as applied.
        var stillDifferent = ConfigurationModel.ChangedEntries(GetRunning(), candidate)
            .Select(e => e.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var applied = runtimeChanges.Where(e => !stillDifferent.Contains(e.Path)).Select(e => e.Path).ToList();
        var failures = runtimeChanges
            .Where(e => stillDifferent.Contains(e.Path) && failedPrefixes.Any(p => e.Path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(e => e.Path)
            .ToList();
        return (applied, failures);
    }

    /// <summary>Reloads the effective configuration; false when unsupported or failed.</summary>
    private bool TryReloadConfiguration()
    {
        if (_configuration is not IConfigurationRoot root)
        {
            return false;
        }

        try
        {
            root.Reload();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or InvalidOperationException)
        {
            _logger.LogWarning("Configuration reload after write failed: {ErrorType}", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>Records a refused/failed write and builds its result.</summary>
    private ConfigurationWriteResult Refuse(
        ConfigurationEditSession session,
        ConfigurationWriteStatus status,
        string reason,
        params string[] messages)
    {
        _logger.LogInformation(
            "Configuration write by {Operator} not saved ({Reason})",
            session.OperatorUsername,
            reason);
        _audit.TryWrite(
            AuditEventType.ConfigurationSaveFailed,
            AuditEventOutcome.Failure,
            "Configuration write not saved",
            new AuditEventContext
            {
                ActorUsername = session.OperatorUsername,
                SessionId = session.OperatorSessionId,
                Details = new Dictionary<string, string>(StringComparer.Ordinal) { ["reason"] = reason }
            });
        return ConfigurationWriteResult.NotSaved(status, messages);
    }

    /// <summary>Reads startup bytes for merging; returns an operator-safe error on I/O failure.</summary>
    private byte[]? ReadStartupBytesForMerge(out string? error)
    {
        error = null;
        try
        {
            return _fileStore.ReadAllBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "Unable to read startup-config (appsettings.json).";
            return null;
        }
    }

    /// <summary>
    /// Builds the bytes to persist: the current startup file with only the operator's changes applied.
    /// </summary>
    /// <remarks>Called after <see cref="Validate(JsonObject, byte[], string)"/> accepted the same startup bytes.</remarks>
    private static byte[] BuildFileBytes(byte[]? startupBytes, JsonObject baseline, JsonObject candidate)
    {
        var root = startupBytes is { Length: > 0 }
            ? (JsonObject)JsonNode.Parse(startupBytes, ConfigurationModel.NodeOptions, ConfigurationModel.FileParseOptions)!
            : new JsonObject(ConfigurationModel.NodeOptions);
        ConfigurationModel.MergeChangesInto(root, baseline, candidate);
        return Serialize(root);
    }

    /// <summary>Serializes a file document exactly as written to disk.</summary>
    private static byte[] Serialize(JsonObject root) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(root.ToJsonString(ConfigurationModel.FileWriteOptions) + Environment.NewLine);

    /// <summary>Setting paths whose effective value differs from the startup file (another provider overrides them).</summary>
    private IEnumerable<string> OverriddenPaths(byte[]? startupBytes)
    {
        try
        {
            return ConfigurationModel.ChangedEntries(ConfigurationModel.ReadJson(startupBytes), ConfigurationModel.Read(_configuration))
                .Select(e => e.Path)
                .ToList();
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>Binds the server options from the (reloaded) effective configuration; null when binding fails.</summary>
    private TechConnectServerOptions? BindEffectiveServer()
    {
        try
        {
            var server = new TechConnectServerOptions();
            _configuration.GetSection(TechConnectServerOptions.SectionName).Bind(server);
            return server;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>SHA-256 of file content (or a marker for a missing file).</summary>
    internal static string Hash(byte[]? bytes) =>
        bytes is null ? MissingFileHash : Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Whether a top-level section with <paramref name="keyword"/> owns <paramref name="setting"/>.</summary>
    private static bool IsInSection(ConfigSetting setting, string keyword) =>
        ConfigurationSchema.Root.Children.First(c => c.Keyword == keyword).Settings.Contains(setting);

    /// <summary>Copies selected section-level settings from one model to another.</summary>
    private static void CopySettings(JsonObject from, JsonObject to, Func<ConfigSetting, bool> predicate)
    {
        Copy(ConfigurationSchema.Root, from, to);

        void Copy(ConfigSection section, JsonObject source, JsonObject target)
        {
            foreach (var setting in section.Settings.Where(predicate))
            {
                ConfigurationModel.SetValue(target, setting, ConfigurationModel.GetValue(source, setting));
            }

            foreach (var child in section.Children)
            {
                Copy(
                    child,
                    ConfigurationModel.ObjectAt(source, child.JsonPath, create: false) ?? new JsonObject(),
                    ConfigurationModel.ObjectAt(target, child.JsonPath, create: true)!);
            }
        }
    }
}
