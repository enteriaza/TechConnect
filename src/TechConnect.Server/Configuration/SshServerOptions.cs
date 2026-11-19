// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshServerOptions.cs
//
// Purpose:
// Defines the TechConnectServer options root plus SSH listener, audit trail,
// and file-logging option types bound from configuration.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Configuration;

/// <summary>
/// Root options bound from the <c>TechConnectServer</c> configuration section.
/// </summary>
public sealed class TechConnectServerOptions
{
    /// <summary>
    /// Configuration section name for <see cref="TechConnectServerOptions"/>.
    /// </summary>
    public const string SectionName = "TechConnectServer";

    /// <summary>
    /// SSH listener and host-key options.
    /// </summary>
    public SshServerOptions Ssh { get; set; } = new();

    /// <summary>
    /// Local and RADIUS operator authentication options.
    /// </summary>
    public AuthenticationOptions Authentication { get; set; } = new();

    /// <summary>
    /// Connector TLS listener options.
    /// </summary>
    public ConnectorListenerOptions Connector { get; set; } = new();

    /// <summary>
    /// In-memory operational audit trail options.
    /// </summary>
    public AuditOptions Audit { get; set; } = new();

    /// <summary>
    /// Explicit DNS servers used by TechConnect. Empty list disables DNS (literals still work).
    /// </summary>
    public DnsOptions Dns { get; set; } = new();
}

/// <summary>
/// In-memory operational audit trail. Not persistent across process restarts.
/// </summary>
public sealed class AuditOptions
{
    /// <summary>
    /// When <see langword="true"/>, CLI/SSH operational events are recorded in the in-memory audit trail.
    /// </summary>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum retained events. Oldest events are evicted when exceeded.
    /// </summary>
    /// <value>Defaults to 10_000. Validated to 1–1_000_000.</value>
    public int MaximumEvents { get; set; } = 10_000;
}

/// <summary>
/// SSH listener bind settings, host private key material, and connection limits.
/// </summary>
/// <remarks>
/// Bound under <c>TechConnectServer:Ssh</c>. When <see cref="Enabled"/> is false,
/// SSH-related validation is skipped.
/// </remarks>
public sealed class SshServerOptions
{
    /// <summary>
    /// When <see langword="false"/>, the SSH listener is not started and SSH option validation is skipped.
    /// </summary>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Bind address for the SSH listener (for example <c>0.0.0.0</c> or <c>127.0.0.1</c>).
    /// </summary>
    /// <value>Defaults to <c>0.0.0.0</c>.</value>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// TCP port for the SSH listener.
    /// </summary>
    /// <value>Defaults to 2222. Validated to 1–65535 when SSH is enabled.</value>
    public int Port { get; set; } = 2222;

    /// <summary>
    /// OpenSSH PEM private host key.
    /// </summary>
    /// <remarks>
    /// When empty on first start, a key is generated and persisted into the runtime
    /// <c>appsettings.json</c> (or environment-specific file). Never log this value.
    /// An empty environment-specific override does not wipe a valid base key
    /// (see <see cref="SshHostKeyOptionsSetup"/> / <see cref="AppsettingsHostKeyStore"/>).
    /// </remarks>
    public string HostPrivateKey { get; set; } = string.Empty;

    /// <summary>
    /// Legacy global operator public keys. No longer used for authentication.
    /// </summary>
    /// <remarks>
    /// Presence of any non-empty entry fails startup — migrate keys to
    /// <c>TechConnectServer:Authentication:Local:LocalUsers[*]:AuthorizedPublicKeys</c>.
    /// </remarks>
    public List<string> AuthorizedPublicKeys { get; set; } = [];

    /// <summary>
    /// Maximum concurrent SSH sessions accepted by the listener.
    /// </summary>
    /// <value>Defaults to 50; must be at least 1 when SSH is enabled.</value>
    public int MaximumConcurrentConnections { get; set; } = 50;

    /// <summary>
    /// Graceful SSH shutdown timeout in seconds (session close + accept-loop wait).
    /// </summary>
    /// <value>Defaults to 10; must be at least 1 when SSH is enabled.</value>
    public int ShutdownTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Rolling file logging options bound from <c>Logging:File</c>.
/// </summary>
public sealed class FileLoggingOptions
{
    /// <summary>
    /// Configuration section name for <see cref="FileLoggingOptions"/>.
    /// </summary>
    public const string SectionName = "Logging:File";

    /// <summary>
    /// Optional log directory. Empty triggers <see cref="Hosting.LogDirectoryResolver"/> fallback.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// File name prefix before the Serilog date roll suffix (for example <c>techconnect-server-.log</c>).
    /// </summary>
    /// <value>Defaults to <c>techconnect-server</c>.</value>
    public string FileNamePrefix { get; set; } = "techconnect-server";

    /// <summary>
    /// Maximum number of retained rolled log files.
    /// </summary>
    /// <value>Defaults to 14; must be at least 1.</value>
    public int RetainedFileCountLimit { get; set; } = 14;

    /// <summary>
    /// Maximum size in bytes of a single log file before size-based rolling.
    /// </summary>
    /// <value>Defaults to 10 MiB; must be at least 1024.</value>
    public long FileSizeLimitBytes { get; set; } = 10 * 1024 * 1024;
}
