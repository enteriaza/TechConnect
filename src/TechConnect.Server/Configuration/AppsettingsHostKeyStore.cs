// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AppsettingsHostKeyStore.cs
//
// Purpose:
// Resolves and persists the SSH host private key via content-root appsettings
// JSON files with mutex-guarded reads/writes and empty-override semantics.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Configuration;

/// <summary>
/// Resolves and persists the SSH host private key using standard .NET configuration files
/// under the application content root:
/// <list type="bullet">
/// <item><description><c>appsettings.json</c> (base / Production persistence target)</description></item>
/// <item><description><c>appsettings.{Environment}.json</c> (environment override / non-Production persistence target)</description></item>
/// </list>
/// An empty <c>HostPrivateKey</c> in an environment-specific file does not override a valid base key
/// and does not trigger regeneration.
/// </summary>
/// <remarks>
/// Persistence never overwrites a non-empty host key already present in the target file
/// or when effective resolution already has a key. Cross-process access is serialized
/// with a local named mutex derived from the persistence path.
/// </remarks>
internal sealed class AppsettingsHostKeyStore
{
    /// <summary>
    /// Base configuration file name under the content root.
    /// </summary>
    public const string BaseFileName = "appsettings.json";

    /// <summary>
    /// Configuration path of the host private key property (documentation / diagnostics).
    /// </summary>
    public const string HostPrivateKeyPath = "TechConnectServer:Ssh:HostPrivateKey";

    /// <summary>
    /// JSON serializer options used when rewriting configuration files (indented).
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Hosting environment that supplies content root and environment name.
    /// </summary>
    private readonly IHostEnvironment _environment;

    /// <summary>
    /// Logger for persistence success (never logs key material).
    /// </summary>
    private readonly ILogger<AppsettingsHostKeyStore> _logger;

    /// <summary>
    /// Named mutex name unique to <see cref="PersistenceFilePath"/>.
    /// </summary>
    private readonly string _mutexName;

    /// <summary>
    /// Initializes a store for the current host environment.
    /// </summary>
    /// <param name="environment">Host environment providing content root and name.</param>
    /// <param name="logger">Logger for persistence operations.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="environment"/> or <paramref name="logger"/> is <see langword="null"/>.
    /// </exception>
    public AppsettingsHostKeyStore(
        IHostEnvironment environment,
        ILogger<AppsettingsHostKeyStore> logger)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        PersistenceFilePath = ResolvePersistenceFilePath(_environment);
        _mutexName = CreateMutexName(PersistenceFilePath);
    }

    /// <summary>
    /// JSON file that receives a newly generated host key for the current hosting environment.
    /// </summary>
    public string PersistenceFilePath { get; }

    /// <summary>
    /// Absolute path to <see cref="BaseFileName"/> under the content root.
    /// </summary>
    public string BaseFilePath =>
        Path.GetFullPath(Path.Combine(_environment.ContentRootPath, BaseFileName));

    /// <summary>
    /// Absolute path to the environment-specific appsettings file, or <see langword="null"/> in Production.
    /// </summary>
    public string? EnvironmentFilePath =>
        IsProductionEnvironment(_environment.EnvironmentName)
            ? null
            : Path.GetFullPath(Path.Combine(
                _environment.ContentRootPath,
                $"appsettings.{_environment.EnvironmentName}.json"));

    /// <summary>
    /// Resolves the effective host private key from base + environment JSON files,
    /// ignoring empty environment-specific overrides.
    /// </summary>
    public EffectiveHostPrivateKey? TryResolveEffectiveHostPrivateKey()
    {
        using (AcquireMutex())
        {
            return TryResolveEffectiveHostPrivateKeyUnlocked();
        }
    }

    /// <summary>
    /// Reads whatever is currently stored in the persistence target file (after generation).
    /// </summary>
    public string? TryReadPersistedHostPrivateKey()
    {
        using (AcquireMutex())
        {
            return ReadNonEmptyHostPrivateKey(PersistenceFilePath);
        }
    }

    /// <summary>
    /// Writes <paramref name="privateKeyPem"/> into <see cref="PersistenceFilePath"/>,
    /// preserving unrelated JSON. Refuses to overwrite a non-empty existing host key in that file.
    /// </summary>
    public void PersistGeneratedHostPrivateKey(string privateKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(privateKeyPem);

        using (AcquireMutex())
        {
            string? tempPath = null;
            try
            {
                var existingInTarget = ReadNonEmptyHostPrivateKey(PersistenceFilePath);
                if (!string.IsNullOrWhiteSpace(existingInTarget))
                {
                    throw new InvalidOperationException(
                        $"Refusing to overwrite an existing SSH host private key in '{PersistenceFilePath}'.");
                }

                // Also refuse if effective resolution already has a key (e.g. valid base key
                // while targeting Development) — generation should not have been requested.
                var effective = TryResolveEffectiveHostPrivateKeyUnlocked();
                if (effective is not null)
                {
                    throw new InvalidOperationException(
                        $"Refusing to generate an SSH host key because an effective key already exists in '{effective.SourceFilePath}'.");
                }

                var root = ReadOrCreateRoot(PersistenceFilePath);
                var techConnect = GetOrCreateObject(root, "TechConnectServer");
                var ssh = GetOrCreateObject(techConnect, "Ssh");
                ssh["HostPrivateKey"] = privateKeyPem;

                var directory = Path.GetDirectoryName(PersistenceFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                tempPath = PersistenceFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var json = root.ToJsonString(WriteOptions);
                if (!json.EndsWith('\n'))
                {
                    json += Environment.NewLine;
                }

                File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (File.Exists(PersistenceFilePath))
                {
                    File.Replace(
                        sourceFileName: tempPath,
                        destinationFileName: PersistenceFilePath,
                        destinationBackupFileName: null,
                        ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, PersistenceFilePath);
                }

                RestrictAccess(PersistenceFilePath);

                _logger.LogInformation(
                    "Persisted new SSH host private key to {ConfigurationFile} (environment {Environment})",
                    PersistenceFilePath,
                    _environment.EnvironmentName);
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to persist the SSH host private key to '{PersistenceFilePath}'. " +
                    "The server will not start with an ephemeral host key.",
                    ex);
            }
            finally
            {
                if (tempPath is not null)
                {
                    TryDelete(tempPath);
                }
            }
        }
    }

    /// <summary>
    /// Computes the absolute path of the JSON file that should receive a newly generated host key.
    /// </summary>
    /// <param name="environment">Host environment providing content root and name.</param>
    /// <returns>
    /// <c>appsettings.json</c> for Production (or blank environment name);
    /// otherwise <c>appsettings.{Environment}.json</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="environment"/> is <see langword="null"/>.</exception>
    public static string ResolvePersistenceFilePath(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var fileName = IsProductionEnvironment(environment.EnvironmentName)
            ? BaseFileName
            : $"appsettings.{environment.EnvironmentName}.json";
        return Path.GetFullPath(Path.Combine(environment.ContentRootPath, fileName));
    }

    /// <summary>
    /// Returns whether the environment name is treated as Production for host-key persistence.
    /// </summary>
    /// <param name="environmentName">Environment name; null/whitespace is treated as Production.</param>
    /// <returns><see langword="true"/> when persistence targets the base appsettings file.</returns>
    public static bool IsProductionEnvironment(string? environmentName) =>
        string.IsNullOrWhiteSpace(environmentName) ||
        string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Mutex-free effective key resolution used while the caller already holds the store lock.
    /// </summary>
    /// <returns>Effective key and source path, or <see langword="null"/> when neither file contributes a non-empty key.</returns>
    private EffectiveHostPrivateKey? TryResolveEffectiveHostPrivateKeyUnlocked()
    {
        // Environment-specific file wins only when it contributes a non-empty HostPrivateKey.
        // Empty string / whitespace is treated as absent so it cannot shadow a valid base key.
        if (EnvironmentFilePath is not null)
        {
            var envKey = ReadNonEmptyHostPrivateKey(EnvironmentFilePath);
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                return new EffectiveHostPrivateKey(envKey, EnvironmentFilePath);
            }
        }

        var baseKey = ReadNonEmptyHostPrivateKey(BaseFilePath);
        if (!string.IsNullOrWhiteSpace(baseKey))
        {
            return new EffectiveHostPrivateKey(baseKey, BaseFilePath);
        }

        return null;
    }

    /// <summary>
    /// Reads a non-empty <c>TechConnectServer:Ssh:HostPrivateKey</c> string from a JSON file.
    /// </summary>
    /// <param name="path">Absolute path to an appsettings JSON file.</param>
    /// <returns>Trimmed PEM text, or <see langword="null"/> when absent/empty/file missing.</returns>
    /// <exception cref="InvalidOperationException">The file exists but cannot be parsed as JSON.</exception>
    private static string? ReadNonEmptyHostPrivateKey(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var root = JsonNode.Parse(text);
            var value = root?["TechConnectServer"]?["Ssh"]?["HostPrivateKey"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to read SSH host private key from '{path}'.",
                ex);
        }
    }

    /// <summary>
    /// Loads an existing JSON object root from <paramref name="path"/>, or creates an empty object.
    /// </summary>
    /// <param name="path">Configuration file path.</param>
    /// <returns>Mutable JSON root for persistence.</returns>
    /// <exception cref="InvalidOperationException">Existing file content is not valid JSON.</exception>
    private static JsonNode ReadOrCreateRoot(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text)
                ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Unable to parse '{path}' while persisting the SSH host key.",
                ex);
        }
    }

    /// <summary>
    /// Acquires the cross-process mutex for this store's persistence path (30s timeout).
    /// </summary>
    /// <returns>Lease that releases and disposes the mutex on dispose.</returns>
    /// <exception cref="InvalidOperationException">Acquisition timed out.</exception>
    private MutexLease AcquireMutex()
    {
        var mutex = new Mutex(initiallyOwned: false, _mutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
            {
                mutex.Dispose();
                throw new InvalidOperationException(
                    "Timed out waiting for the SSH host-key configuration lock.");
            }
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new MutexLease(mutex);
    }

    /// <summary>
    /// RAII lease that releases and disposes a held <see cref="Mutex"/>.
    /// </summary>
    private sealed class MutexLease : IDisposable
    {
        /// <summary>
        /// Held named mutex.
        /// </summary>
        private readonly Mutex _mutex;

        /// <summary>
        /// Indicates whether <see cref="Dispose"/> has already run.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Initializes a lease over an already-acquired mutex.
        /// </summary>
        /// <param name="mutex">Mutex owned by the caller.</param>
        public MutexLease(Mutex mutex) => _mutex = mutex;

        /// <summary>
        /// Releases the mutex (best-effort) and disposes it.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned — ignore.
            }

            _mutex.Dispose();
        }
    }

    /// <summary>
    /// Builds a local named-mutex identifier from the persistence path hash.
    /// </summary>
    /// <param name="path">Absolute persistence file path.</param>
    /// <returns>Mutex name under the <c>Local\</c> namespace.</returns>
    private static string CreateMutexName(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        return @"Local\TechConnect.Server.HostKey." + hash;
    }

    /// <summary>
    /// Gets or creates a nested JSON object property under <paramref name="parent"/>.
    /// </summary>
    /// <param name="parent">Parent JSON node (must be an object).</param>
    /// <param name="propertyName">Child object property name.</param>
    /// <returns>Existing or newly created <see cref="JsonObject"/>.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="parent"/> is not a JSON object.</exception>
    private static JsonObject GetOrCreateObject(JsonNode parent, string propertyName)
    {
        if (parent is not JsonObject obj)
        {
            throw new InvalidOperationException(
                $"Configuration node '{propertyName}' parent is not a JSON object.");
        }

        if (obj[propertyName] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        obj[propertyName] = created;
        return created;
    }

    /// <summary>
    /// Best-effort restriction of file permissions on non-Windows platforms (user read/write only).
    /// </summary>
    /// <param name="path">Persisted configuration file path.</param>
    private static void RestrictAccess(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception)
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Best-effort deletion of a temporary file left after atomic replace.
    /// </summary>
    /// <param name="path">Temporary file path.</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup of temp file.
        }
    }
}

/// <summary>
/// Effective SSH host private key PEM and the JSON file it was read from.
/// </summary>
/// <param name="PrivateKeyPem">Non-empty OpenSSH PEM private key text.</param>
/// <param name="SourceFilePath">Absolute path of the appsettings file that contributed the key.</param>
internal sealed record EffectiveHostPrivateKey(string PrivateKeyPem, string SourceFilePath);
