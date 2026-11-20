// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfigurationSchema.cs
//
// Purpose:
// Explicit inventory of every appsettings.json setting managed by the SSH
// configuration CLI: JSON location, CLI keyword, type, default, range,
// sensitivity, and whether a change applies at runtime or needs a restart.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace TechConnect.Server.Configuration.Management;

/// <summary>
/// Typed value kinds accepted by configuration-mode commands.
/// </summary>
internal enum ConfigValueKind
{
    /// <summary>JSON boolean; accepts <c>true|false|enable|disable</c>.</summary>
    Boolean,

    /// <summary>JSON integer constrained by <see cref="ConfigSetting.Minimum"/>/<see cref="ConfigSetting.Maximum"/>.</summary>
    Integer,

    /// <summary>IPv4/IPv6 literal.</summary>
    IpAddress,

    /// <summary>Host name or IP literal.</summary>
    Host,

    /// <summary>Single-line printable text.</summary>
    Text,

    /// <summary>One of <see cref="ConfigSetting.AllowedValues"/> (case-insensitive input, canonical storage).</summary>
    Enum,

    /// <summary>40- or 64-character hex certificate thumbprint.</summary>
    Thumbprint,

    /// <summary>Cisco privilege level 1–15 stored as canonical decimal text.</summary>
    PrivilegeLevel,

    /// <summary>Directory path (relative or absolute) without control or invalid path characters.</summary>
    DirectoryPath,

    /// <summary>File-name prefix without path separators or invalid file-name characters.</summary>
    FileNamePrefix,

    /// <summary>OpenSSH <c>authorized_keys</c> public-key line.</summary>
    PublicKeyLine,

    /// <summary>Sensitive: Identity V3 operator password hash (subject = username).</summary>
    PasswordHash,

    /// <summary>Sensitive: Identity V3 enable-secret hash (subject = <c>enable</c>).</summary>
    EnableSecretHash,

    /// <summary>Sensitive: RADIUS shared secret (plaintext by protocol necessity).</summary>
    SharedSecret,

    /// <summary>Sensitive: OpenSSH PEM SSH host private key.</summary>
    HostPrivateKey
}

/// <summary>
/// Value shape of a configuration setting.
/// </summary>
internal enum ConfigShape
{
    /// <summary>Single JSON value.</summary>
    Scalar,

    /// <summary>JSON array of strings; supports add/remove/set.</summary>
    List,

    /// <summary>JSON object of string keys to values (for example Serilog level overrides).</summary>
    Map
}

/// <summary>
/// How a saved change reaches the running server.
/// </summary>
internal enum ConfigApplyMode
{
    /// <summary>Consumed once at startup; persisted change takes effect after a restart.</summary>
    RestartRequired,

    /// <summary>Applied to the running server after a successful <c>write</c>.</summary>
    Runtime
}

/// <summary>
/// One leaf setting in <c>appsettings.json</c>.
/// </summary>
internal sealed class ConfigSetting
{
    /// <summary>CLI keyword (kebab-case of the JSON property, or a Cisco-style name for secrets).</summary>
    public required string Keyword { get; init; }

    /// <summary>JSON property name within the parent object.</summary>
    public required string JsonName { get; init; }

    /// <summary>Help text.</summary>
    public required string Description { get; init; }

    /// <summary>Value kind (for lists and maps, the kind of each value).</summary>
    public required ConfigValueKind Kind { get; init; }

    /// <summary>Scalar, list, or map.</summary>
    public ConfigShape Shape { get; init; } = ConfigShape.Scalar;

    /// <summary>Factory for the default JSON value (matches the options class initializer).</summary>
    public Func<JsonNode?> Default { get; init; } = static () => JsonValue.Create(string.Empty);

    /// <summary>Inclusive minimum for <see cref="ConfigValueKind.Integer"/>.</summary>
    public long Minimum { get; init; } = int.MinValue;

    /// <summary>Inclusive maximum for <see cref="ConfigValueKind.Integer"/>.</summary>
    public long Maximum { get; init; } = int.MaxValue;

    /// <summary>Allowed canonical values for <see cref="ConfigValueKind.Enum"/>.</summary>
    public IReadOnlyList<string> AllowedValues { get; init; } = [];

    /// <summary>When <see langword="true"/>, an unset value is stored as JSON <c>null</c>.</summary>
    public bool Nullable { get; init; }

    /// <summary>Maximum text length for string kinds.</summary>
    public int MaxLength { get; init; } = 1024;

    /// <summary>Runtime vs restart-required.</summary>
    public ConfigApplyMode ApplyMode { get; init; } = ConfigApplyMode.RestartRequired;

    /// <summary>Whether the value must never be displayed or logged.</summary>
    public bool IsSecret => Kind is ConfigValueKind.PasswordHash
        or ConfigValueKind.EnableSecretHash
        or ConfigValueKind.SharedSecret
        or ConfigValueKind.HostPrivateKey;

    /// <summary>Help placeholder describing the expected value.</summary>
    public string ValueHelp => Kind switch
    {
        ConfigValueKind.Boolean => "true|false|enable|disable",
        ConfigValueKind.Integer => $"<{Minimum}-{Maximum}>",
        ConfigValueKind.IpAddress => "<ip-address>",
        ConfigValueKind.Host => "<hostname|ip-address>",
        ConfigValueKind.Enum => string.Join('|', AllowedValues),
        ConfigValueKind.Thumbprint => "<hex-thumbprint>",
        ConfigValueKind.PrivilegeLevel => "<1-15>",
        ConfigValueKind.DirectoryPath => "<directory>",
        ConfigValueKind.FileNamePrefix => "<file-name-prefix>",
        ConfigValueKind.PublicKeyLine => "<authorized_keys line>",
        ConfigValueKind.PasswordHash or ConfigValueKind.EnableSecretHash => "<cr> (prompt) | hash <identity-v3-hash>",
        ConfigValueKind.SharedSecret => "<cr> (prompt)",
        ConfigValueKind.HostPrivateKey => "generate",
        _ => "<text>"
    };
}

/// <summary>
/// Keyed list of JSON objects (for example local users keyed by username).
/// </summary>
internal sealed class ConfigCollection
{
    /// <summary>CLI keyword that enters (or with <c>no</c>, removes) an entry.</summary>
    public required string Keyword { get; init; }

    /// <summary>
    /// Prompt mode name of the entry sub-mode, for example <c>config-user</c>; <see langword="null"/> when an entry
    /// is configured entirely on one line in the parent context and has no sub-mode (its single setting is then
    /// shown under the entry's own path, for example <c>enable-secret-level 5 &lt;hidden&gt;</c>).
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>JSON array property name within the parent section object.</summary>
    public required string JsonName { get; init; }

    /// <summary>JSON property of each entry that identifies it.</summary>
    public required string KeyJsonName { get; init; }

    /// <summary>Kind of the key (<see cref="ConfigValueKind.Text"/> usernames or privilege levels).</summary>
    public required ConfigValueKind KeyKind { get; init; }

    /// <summary>Help placeholder for the key argument.</summary>
    public required string KeyHelp { get; init; }

    /// <summary>Help text.</summary>
    public required string Description { get; init; }

    /// <summary>Settings of each entry (excluding the key).</summary>
    public required IReadOnlyList<ConfigSetting> Settings { get; init; }
}

/// <summary>
/// Configuration context mapped to a JSON object.
/// </summary>
internal sealed class ConfigSection
{
    /// <summary>CLI keyword that enters this context (empty for the global root).</summary>
    public required string Keyword { get; init; }

    /// <summary>Prompt mode name, for example <c>config-ssh</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>JSON path segments relative to the parent section object.</summary>
    public required IReadOnlyList<string> JsonPath { get; init; }

    /// <summary>Help text.</summary>
    public required string Description { get; init; }

    /// <summary>Leaf settings in this object.</summary>
    public IReadOnlyList<ConfigSetting> Settings { get; init; } = [];

    /// <summary>Keyed object lists in this object.</summary>
    public IReadOnlyList<ConfigCollection> Collections { get; init; } = [];

    /// <summary>Nested contexts.</summary>
    public IReadOnlyList<ConfigSection> Children { get; init; } = [];
}

/// <summary>
/// The authoritative inventory of managed <c>appsettings.json</c> settings.
/// </summary>
/// <remarks>
/// Mirrors <see cref="TechConnectServerOptions"/>, <see cref="FileLoggingOptions"/>, and the
/// <c>Serilog</c> section. Defaults match the options-class initializers. The legacy
/// <c>TechConnectServer:Ssh:AuthorizedPublicKeys</c> property is intentionally not managed
/// (startup rejects any non-empty value); it is preserved untouched on <c>write</c>.
/// </remarks>
internal static class ConfigurationSchema
{
    /// <summary>Serilog level names accepted by <c>Serilog:MinimumLevel</c>.</summary>
    public static readonly IReadOnlyList<string> SerilogLevels =
        ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];

    /// <summary>Enrichers available from referenced Serilog packages.</summary>
    public static readonly IReadOnlyList<string> SerilogEnrichers = ["FromLogContext"];

    /// <summary>Global configuration context (<c>TechConnect(config)#</c>).</summary>
    public static ConfigSection Root { get; } = BuildRoot();

    /// <summary>Builds the schema tree.</summary>
    private static ConfigSection BuildRoot()
    {
        var ssh = new ConfigSection
        {
            Keyword = "ssh",
            Mode = "config-ssh",
            JsonPath = ["TechConnectServer", "Ssh"],
            Description = "SSH operator listener (TechConnectServer:Ssh)",
            Settings =
            [
                Bool("enabled", "Enabled", "Start the SSH listener", true),
                Ip("listen-address", "ListenAddress", "SSH listener bind address", "0.0.0.0"),
                Int("port", "Port", "SSH listener TCP port", 2222, 1, 65535),
                new ConfigSetting
                {
                    Keyword = "host-private-key",
                    JsonName = "HostPrivateKey",
                    Description = "SSH host private key (generate a new key; never displayed)",
                    Kind = ConfigValueKind.HostPrivateKey,
                    MaxLength = 64 * 1024
                },
                Int("maximum-concurrent-connections", "MaximumConcurrentConnections", "Maximum concurrent SSH sessions", 50, 1, int.MaxValue),
                Int("shutdown-timeout-seconds", "ShutdownTimeoutSeconds", "Graceful SSH shutdown timeout (seconds)", 10, 1, int.MaxValue)
            ]
        };

        var user = new ConfigCollection
        {
            Keyword = "username",
            Mode = "config-user",
            JsonName = "LocalUsers",
            KeyJsonName = "Username",
            KeyKind = ConfigValueKind.Text,
            KeyHelp = "<username>",
            Description = "Local operator account (LocalUsers[])",
            Settings =
            [
                Bool("enabled", "Enabled", "Account may log in", true),
                new ConfigSetting
                {
                    Keyword = "privilege-level",
                    JsonName = "PrivilegeLevel",
                    Description = "Cisco privilege level 1-15",
                    Kind = ConfigValueKind.PrivilegeLevel,
                    Default = static () => JsonValue.Create("1")
                },
                Secret("password", "PasswordHash", "Login password (stored as Identity V3 hash)", ConfigValueKind.PasswordHash),
                new ConfigSetting
                {
                    Keyword = "authorized-public-keys",
                    JsonName = "AuthorizedPublicKeys",
                    Description = "OpenSSH public keys for this account",
                    Kind = ConfigValueKind.PublicKeyLine,
                    Shape = ConfigShape.List,
                    Default = static () => new JsonArray(),
                    MaxLength = 16 * 1024
                }
            ]
        };

        var enableLevel = new ConfigCollection
        {
            Keyword = "enable-secret-level",
            JsonName = "EnableSecrets",
            KeyJsonName = "PrivilegeLevel",
            KeyKind = ConfigValueKind.PrivilegeLevel,
            KeyHelp = "<1-15>",
            Description = "Privilege-specific enable secret (EnableSecrets[])",
            Settings =
            [
                Secret("secret", "SecretHash", "Enable secret for this level (stored as Identity V3 hash)", ConfigValueKind.EnableSecretHash)
            ]
        };

        var local = new ConfigSection
        {
            Keyword = "local",
            Mode = "config-auth-local",
            JsonPath = ["Local"],
            Description = "Local accounts and enable secrets (Authentication:Local)",
            Settings =
            [
                Bool("password-authentication-enabled", "PasswordAuthenticationEnabled", "Offer SSH password authentication", true),
                Bool("public-key-authentication-enabled", "PublicKeyAuthenticationEnabled", "Offer SSH public-key authentication", true),
                Secret("enable-secret", "EnableSecretHash", "Level-15 enable secret (stored as Identity V3 hash)", ConfigValueKind.EnableSecretHash)
            ],
            Collections = [user, enableLevel]
        };

        ConfigSection RadiusServer(string keyword, string json, string description) => new()
        {
            Keyword = keyword,
            Mode = "config-radius-server",
            JsonPath = [json],
            Description = description,
            Settings =
            [
                new ConfigSetting
                {
                    Keyword = "address",
                    JsonName = "Address",
                    Description = "RADIUS server host name or IP address",
                    Kind = ConfigValueKind.Host,
                    MaxLength = 253
                },
                Int("port", "Port", "RADIUS authentication UDP port", 1812, 1, 65535),
                Secret("shared-secret", "SharedSecret", "RADIUS shared secret", ConfigValueKind.SharedSecret, maxLength: 256)
            ]
        };

        var radius = new ConfigSection
        {
            Keyword = "radius",
            Mode = "config-auth-radius",
            JsonPath = ["Radius"],
            Description = "RADIUS password authentication (Authentication:Radius)",
            Settings =
            [
                Bool("enabled", "Enabled", "Use RADIUS for password authentication", false),
                Bool("local-fallback-enabled", "LocalFallbackEnabled", "Fall back to local passwords when RADIUS is unavailable", false),
                Int("timeout-milliseconds", "TimeoutMilliseconds", "Per-attempt timeout (milliseconds)", 3000, 100, 120_000),
                Int("max-attempts", "MaxAttempts", "Send attempts per server", 2, 1, 5),
                new ConfigSetting
                {
                    Keyword = "nas-identifier",
                    JsonName = "NasIdentifier",
                    Description = "NAS-Identifier attribute",
                    Kind = ConfigValueKind.Text,
                    MaxLength = 253,
                    Default = static () => JsonValue.Create("techconnect")
                },
                new ConfigSetting
                {
                    Keyword = "nas-ip-address",
                    JsonName = "NasIpAddress",
                    Description = "NAS-IP-Address attribute (optional)",
                    Kind = ConfigValueKind.IpAddress,
                    Nullable = true,
                    Default = static () => null
                }
            ],
            Children =
            [
                RadiusServer("primary-server", "PrimaryServer", "Primary RADIUS server"),
                RadiusServer("secondary-server", "SecondaryServer", "Secondary RADIUS server (empty address = none)")
            ]
        };

        var authentication = new ConfigSection
        {
            Keyword = "authentication",
            Mode = "config-auth",
            JsonPath = ["TechConnectServer", "Authentication"],
            Description = "Operator authentication (TechConnectServer:Authentication)",
            Children = [local, radius]
        };

        var audit = new ConfigSection
        {
            Keyword = "audit",
            Mode = "config-audit",
            JsonPath = ["TechConnectServer", "Audit"],
            Description = "In-memory audit trail (TechConnectServer:Audit)",
            Settings =
            [
                Bool("enabled", "Enabled", "Record operational audit events", true, ConfigApplyMode.Runtime),
                Int("maximum-events", "MaximumEvents", "Retained audit events", 10_000, 1, 1_000_000, ConfigApplyMode.Runtime)
            ]
        };

        var dns = new ConfigSection
        {
            Keyword = "dns",
            Mode = "config-dns",
            JsonPath = ["TechConnectServer", "Dns"],
            Description = "TechConnect DNS resolvers (TechConnectServer:Dns)",
            Settings =
            [
                new ConfigSetting
                {
                    Keyword = "servers",
                    JsonName = "Servers",
                    Description = "Ordered DNS server addresses (empty disables DNS)",
                    Kind = ConfigValueKind.IpAddress,
                    Shape = ConfigShape.List,
                    Default = static () => new JsonArray(),
                    ApplyMode = ConfigApplyMode.Runtime
                },
                Int("query-timeout-seconds", "QueryTimeoutSeconds", "Per-server query timeout (seconds)", 3, 1, 60, ConfigApplyMode.Runtime)
            ]
        };

        var clientValidation = new ConfigSection
        {
            Keyword = "client-certificate-validation",
            Mode = "config-connector-mtls",
            JsonPath = ["ClientCertificateValidation"],
            Description = "Connector mutual-TLS client certificate requirements",
            Settings =
            [
                Bool("require-client-certificate", "RequireClientCertificate", "Require a client certificate (must be true when enabled)", true),
                new ConfigSetting
                {
                    Keyword = "allowed-certificate-authorities",
                    JsonName = "AllowedCertificateAuthorities",
                    Description = "CA thumbprints required in the client chain",
                    Kind = ConfigValueKind.Thumbprint,
                    Shape = ConfigShape.List,
                    Default = static () => new JsonArray()
                }
            ]
        };

        var connector = new ConfigSection
        {
            Keyword = "connector",
            Mode = "config-connector",
            JsonPath = ["TechConnectServer", "Connector"],
            Description = "Connector TCP/mTLS listener (TechConnectServer:Connector)",
            Settings =
            [
                Bool("enabled", "Enabled", "Start the connector listener", false),
                Ip("listen-address", "ListenAddress", "Connector listener bind address", "0.0.0.0"),
                Int("port", "Port", "Connector listener TCP port", 9443, 1, 65535),
                new ConfigSetting
                {
                    Keyword = "server-certificate-thumbprint",
                    JsonName = "ServerCertificateThumbprint",
                    Description = "TLS server certificate thumbprint",
                    Kind = ConfigValueKind.Thumbprint
                },
                new ConfigSetting
                {
                    Keyword = "server-certificate-store-name",
                    JsonName = "ServerCertificateStoreName",
                    Description = "X509 store name holding the server certificate",
                    Kind = ConfigValueKind.Enum,
                    AllowedValues = Enum.GetNames<StoreName>(),
                    Default = static () => JsonValue.Create("My")
                },
                new ConfigSetting
                {
                    Keyword = "server-certificate-store-location",
                    JsonName = "ServerCertificateStoreLocation",
                    Description = "X509 store location of the server certificate",
                    Kind = ConfigValueKind.Enum,
                    AllowedValues = Enum.GetNames<StoreLocation>(),
                    Default = static () => JsonValue.Create("CurrentUser")
                },
                Text("expected-server-subject", "ExpectedServerSubject", "Exact server certificate subject (optional)"),
                Text("expected-server-issuer", "ExpectedServerIssuer", "Exact server certificate issuer (optional)"),
                Int("maximum-concurrent-connections", "MaximumConcurrentConnections", "Maximum concurrent connector connections", 100, 1, int.MaxValue),
                Int("handshake-timeout-seconds", "HandshakeTimeoutSeconds", "TLS/hello handshake timeout (seconds)", 30, 1, int.MaxValue),
                Int("shutdown-timeout-seconds", "ShutdownTimeoutSeconds", "Graceful connector shutdown timeout (seconds)", 10, 1, int.MaxValue)
            ],
            Children = [clientValidation]
        };

        var logFile = new ConfigSection
        {
            Keyword = "file",
            Mode = "config-log-file",
            JsonPath = ["File"],
            Description = "Rolling log file (Logging:File)",
            Settings =
            [
                new ConfigSetting
                {
                    Keyword = "directory",
                    JsonName = "Directory",
                    Description = "Log directory (empty = default location)",
                    Kind = ConfigValueKind.DirectoryPath,
                    MaxLength = 260
                },
                new ConfigSetting
                {
                    Keyword = "file-name-prefix",
                    JsonName = "FileNamePrefix",
                    Description = "Log file name prefix",
                    Kind = ConfigValueKind.FileNamePrefix,
                    MaxLength = 100,
                    Default = static () => JsonValue.Create("techconnect-server")
                },
                Int("retained-file-count-limit", "RetainedFileCountLimit", "Retained rolled log files", 14, 1, int.MaxValue),
                Int("file-size-limit-bytes", "FileSizeLimitBytes", "Maximum log file size (bytes)", 10L * 1024 * 1024, 1024, long.MaxValue)
            ]
        };

        var logging = new ConfigSection
        {
            Keyword = "logging",
            Mode = "config-logging",
            JsonPath = ["Logging"],
            Description = "Logging providers (Logging)",
            Children = [logFile]
        };

        var serilog = new ConfigSection
        {
            Keyword = "serilog",
            Mode = "config-serilog",
            JsonPath = ["Serilog"],
            Description = "Serilog levels and enrichment (Serilog)",
            Settings =
            [
                new ConfigSetting
                {
                    Keyword = "minimum-level",
                    JsonName = "MinimumLevel:Default",
                    Description = "Default minimum log level",
                    Kind = ConfigValueKind.Enum,
                    AllowedValues = SerilogLevels,
                    Default = static () => JsonValue.Create("Information"),
                    ApplyMode = ConfigApplyMode.Runtime
                },
                new ConfigSetting
                {
                    Keyword = "minimum-level-override",
                    JsonName = "MinimumLevel:Override",
                    Description = "Per-source minimum level override (level changes apply at runtime; adding or removing a source needs a restart)",
                    Kind = ConfigValueKind.Enum,
                    AllowedValues = SerilogLevels,
                    Shape = ConfigShape.Map,
                    Default = static () => new JsonObject(),
                    ApplyMode = ConfigApplyMode.Runtime
                },
                new ConfigSetting
                {
                    Keyword = "enrich",
                    JsonName = "Enrich",
                    Description = "Serilog enrichers",
                    Kind = ConfigValueKind.Enum,
                    AllowedValues = SerilogEnrichers,
                    Shape = ConfigShape.List,
                    Default = static () => new JsonArray()
                }
            ]
        };

        return new ConfigSection
        {
            Keyword = string.Empty,
            Mode = "config",
            JsonPath = [],
            Description = "Global configuration",
            Children = [ssh, authentication, audit, dns, connector, logging, serilog]
        };
    }

    /// <summary>Boolean setting helper.</summary>
    private static ConfigSetting Bool(
        string keyword,
        string json,
        string description,
        bool defaultValue,
        ConfigApplyMode apply = ConfigApplyMode.RestartRequired) => new()
        {
            Keyword = keyword,
            JsonName = json,
            Description = description,
            Kind = ConfigValueKind.Boolean,
            Default = () => JsonValue.Create(defaultValue),
            ApplyMode = apply
        };

    /// <summary>Integer setting helper.</summary>
    private static ConfigSetting Int(
        string keyword,
        string json,
        string description,
        long defaultValue,
        long min,
        long max,
        ConfigApplyMode apply = ConfigApplyMode.RestartRequired) => new()
        {
            Keyword = keyword,
            JsonName = json,
            Description = description,
            Kind = ConfigValueKind.Integer,
            Default = () => JsonValue.Create(defaultValue),
            Minimum = min,
            Maximum = max,
            ApplyMode = apply
        };

    /// <summary>IP address setting helper.</summary>
    private static ConfigSetting Ip(string keyword, string json, string description, string defaultValue) => new()
    {
        Keyword = keyword,
        JsonName = json,
        Description = description,
        Kind = ConfigValueKind.IpAddress,
        Default = () => JsonValue.Create(defaultValue)
    };

    /// <summary>Optional free-text setting helper (default empty).</summary>
    private static ConfigSetting Text(string keyword, string json, string description) => new()
    {
        Keyword = keyword,
        JsonName = json,
        Description = description,
        Kind = ConfigValueKind.Text
    };

    /// <summary>Sensitive setting helper (default empty).</summary>
    private static ConfigSetting Secret(
        string keyword,
        string json,
        string description,
        ConfigValueKind kind,
        int maxLength = 1024) => new()
        {
            Keyword = keyword,
            JsonName = json,
            Description = description,
            Kind = kind,
            MaxLength = maxLength
        };

    /// <summary>
    /// Enumerates every leaf setting with its global JSON path (for inventory tests and docs).
    /// </summary>
    /// <returns>Configuration-key style paths (collections use <c>[*]</c>).</returns>
    public static IEnumerable<(string Path, ConfigSetting Setting)> EnumerateSettingPaths()
    {
        return Walk(Root, string.Empty);

        static IEnumerable<(string, ConfigSetting)> Walk(ConfigSection section, string prefix)
        {
            var path = Combine(prefix, string.Join(':', section.JsonPath));
            foreach (var setting in section.Settings)
            {
                yield return (Combine(path, setting.JsonName), setting);
            }

            foreach (var collection in section.Collections)
            {
                var itemPath = Combine(path, collection.JsonName + "[*]");
                yield return (Combine(itemPath, collection.KeyJsonName), new ConfigSetting
                {
                    Keyword = collection.Keyword,
                    JsonName = collection.KeyJsonName,
                    Description = collection.Description,
                    Kind = collection.KeyKind
                });
                foreach (var setting in collection.Settings)
                {
                    yield return (Combine(itemPath, setting.JsonName), setting);
                }
            }

            foreach (var child in section.Children)
            {
                foreach (var entry in Walk(child, path))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>Joins configuration key segments with <c>:</c>, skipping empties.</summary>
    internal static string Combine(string prefix, string segment) =>
        string.IsNullOrEmpty(prefix) ? segment : string.IsNullOrEmpty(segment) ? prefix : prefix + ":" + segment;
}
