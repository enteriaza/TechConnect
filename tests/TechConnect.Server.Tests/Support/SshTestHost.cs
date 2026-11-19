// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshTestHost.cs
//
// Purpose:
// Starts a temp-content-root TechConnect host with SSH enabled for
// integration tests, plus SSH client connect helpers.
//
// -----------------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Configuration;
using TechConnect.Server.Hosting;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Authentication;

namespace TechConnect.Server.Tests.Support;

/// <summary>
/// Disposable SSH test host with temp content root, appsettings, and client connect helpers.
/// </summary>
/// <remarks>
/// Limitations: binds SSH to loopback on an ephemeral port; connector listener is not
/// required for SSH-only tests. Temp content root and logs are best-effort deleted on dispose.
/// Client helpers use DevTunnels SSH client with known host key; they are not a full OpenSSH matrix.
/// </remarks>
internal sealed class SshTestHost : IAsyncDisposable
{
    /// <summary>Temporary content root directory for this host instance.</summary>
    private readonly string _contentRoot;

    /// <summary>Running generic host.</summary>
    private readonly IHost _host;

    /// <summary>
    /// Initializes a started SSH test host wrapper.
    /// </summary>
    /// <param name="contentRoot">Temp content root.</param>
    /// <param name="port">SSH listen port.</param>
    /// <param name="environmentName">Hosting environment name.</param>
    /// <param name="host">Started <see cref="IHost"/>.</param>
    private SshTestHost(string contentRoot, int port, string environmentName, IHost host)
    {
        _contentRoot = contentRoot;
        Port = port;
        EnvironmentName = environmentName;
        _host = host;
    }

    /// <summary>Loopback SSH listen port.</summary>
    public int Port { get; }

    /// <summary>Hosting environment name used for appsettings persistence selection.</summary>
    public string EnvironmentName { get; }

    /// <summary>Absolute temp content root path.</summary>
    public string ContentRoot => _contentRoot;

    /// <summary>Path to base <c>appsettings.json</c> under the content root.</summary>
    public string BaseAppsettingsPath => Path.Combine(_contentRoot, "appsettings.json");

    /// <summary>
    /// Path to the environment-specific persistence file (or base file in Production).
    /// </summary>
    public string PersistenceAppsettingsPath =>
        AppsettingsHostKeyStore.IsProductionEnvironment(EnvironmentName)
            ? BaseAppsettingsPath
            : Path.Combine(_contentRoot, $"appsettings.{EnvironmentName}.json");

    /// <summary>Loaded SSH host key fingerprint from DI.</summary>
    public string HostKeyFingerprint =>
        _host.Services.GetRequiredService<SshHostKeyProvider>().Fingerprint;

    /// <summary>Loaded SSH host key source label/path from DI.</summary>
    public string HostKeySource =>
        _host.Services.GetRequiredService<SshHostKeyProvider>().Source;

    /// <summary>Connector session registry from DI (shared with CLI).</summary>
    public ConnectorSessionRegistry SessionRegistry =>
        _host.Services.GetRequiredService<ConnectorSessionRegistry>();

    /// <summary>Local operator account store from DI.</summary>
    public LocalOperatorAccountStore AccountStore =>
        _host.Services.GetRequiredService<LocalOperatorAccountStore>();

    /// <summary>
    /// Starts a TechConnect host with SSH enabled on a free loopback port and temp content root.
    /// </summary>
    public static async Task<SshTestHost> StartAsync(
        IKeyPair? hostKey,
        IKeyPair[] authorizedOperatorKeys,
        string environmentName = "Production",
        int maximumConcurrentConnections = 10,
        bool writeAppsettingsFile = true,
        string username = "noc",
        string? passwordHash = null,
        bool? passwordAuthenticationEnabled = null,
        bool? publicKeyAuthenticationEnabled = null,
        string privilegeLevel = "15",
        CancellationToken cancellationToken = default)
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        var port = GetFreeTcpPort();
        var authorizedLines = authorizedOperatorKeys
            .Select((k, i) => SshTestKeys.ExportPublicKeyLine(k, $"{username}-{i}"))
            .ToArray();

        var enablePassword = passwordAuthenticationEnabled
            ?? !string.IsNullOrWhiteSpace(passwordHash);
        var enablePublicKey = publicKeyAuthenticationEnabled
            ?? authorizedLines.Length > 0;

        if (writeAppsettingsFile)
        {
            var persistProduction = AppsettingsHostKeyStore.IsProductionEnvironment(environmentName);
            WriteAppsettings(
                Path.Combine(contentRoot, "appsettings.json"),
                hostPrivateKey: persistProduction
                    ? (hostKey is null ? string.Empty : SshTestKeys.ExportPrivateKeyPem(hostKey))
                    : string.Empty,
                includeHostPrivateKeyProperty: persistProduction || hostKey is null,
                authorizedPublicKeys: authorizedLines,
                listenPort: port,
                username: username,
                passwordHash: passwordHash,
                privilegeLevel: privilegeLevel,
                passwordAuthenticationEnabled: enablePassword,
                publicKeyAuthenticationEnabled: enablePublicKey);

            if (!persistProduction)
            {
                WriteAppsettings(
                    Path.Combine(contentRoot, $"appsettings.{environmentName}.json"),
                    hostPrivateKey: hostKey is null ? string.Empty : SshTestKeys.ExportPrivateKeyPem(hostKey),
                    includeHostPrivateKeyProperty: true,
                    authorizedPublicKeys: authorizedLines,
                    listenPort: port,
                    username: username,
                    passwordHash: passwordHash,
                    privilegeLevel: privilegeLevel,
                    passwordAuthenticationEnabled: enablePassword,
                    publicKeyAuthenticationEnabled: enablePublicKey);
            }
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = contentRoot,
            EnvironmentName = environmentName,
            Args = []
        });

        var memory = new Dictionary<string, string?>
        {
            ["TechConnectServer:Ssh:Enabled"] = "true",
            ["TechConnectServer:Ssh:ListenAddress"] = "127.0.0.1",
            ["TechConnectServer:Ssh:Port"] = port.ToString(),
            ["TechConnectServer:Ssh:MaximumConcurrentConnections"] = maximumConcurrentConnections.ToString(),
            ["TechConnectServer:Ssh:ShutdownTimeoutSeconds"] = "5",
            ["TechConnectServer:Authentication:Local:PasswordAuthenticationEnabled"] = enablePassword ? "true" : "false",
            ["TechConnectServer:Authentication:Local:PublicKeyAuthenticationEnabled"] = enablePublicKey ? "true" : "false",
            ["Logging:File:Directory"] = Path.Combine(contentRoot, "logs"),
            ["Logging:File:FileNamePrefix"] = "test",
            ["Serilog:MinimumLevel:Default"] = "Warning"
        };

        if (!writeAppsettingsFile)
        {
            memory["TechConnectServer:Ssh:HostPrivateKey"] =
                hostKey is null ? string.Empty : SshTestKeys.ExportPrivateKeyPem(hostKey);
            memory["TechConnectServer:Authentication:Local:LocalUsers:0:Username"] = username;
            memory["TechConnectServer:Authentication:Local:LocalUsers:0:Enabled"] = "true";
            memory["TechConnectServer:Authentication:Local:LocalUsers:0:PrivilegeLevel"] = privilegeLevel;
            if (!string.IsNullOrWhiteSpace(passwordHash))
            {
                memory["TechConnectServer:Authentication:Local:LocalUsers:0:PasswordHash"] = passwordHash;
            }

            for (var i = 0; i < authorizedLines.Length; i++)
            {
                memory[$"TechConnectServer:Authentication:Local:LocalUsers:0:AuthorizedPublicKeys:{i}"] =
                    authorizedLines[i];
            }
        }

        builder.Configuration.AddInMemoryCollection(memory);
        builder.Logging.ClearProviders();
        builder.AddTechConnectServerHost();

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await WaitForPortAsync(port, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

        return new SshTestHost(contentRoot, port, environmentName, host);
    }

    /// <summary>
    /// Writes a minimal appsettings JSON file for SSH host-key and local-user test scenarios.
    /// </summary>
    public static void WriteAppsettings(
        string path,
        string hostPrivateKey,
        IReadOnlyList<string> authorizedPublicKeys,
        int listenPort = 2222,
        string? extraMarker = "preserve-me",
        bool includeHostPrivateKeyProperty = true,
        string username = "noc",
        string? passwordHash = null,
        string privilegeLevel = "15",
        bool passwordAuthenticationEnabled = false,
        bool publicKeyAuthenticationEnabled = true)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var ssh = new JsonObject
        {
            ["Enabled"] = true,
            ["ListenAddress"] = "127.0.0.1",
            ["Port"] = listenPort,
            ["MaximumConcurrentConnections"] = 10,
            ["ShutdownTimeoutSeconds"] = 5
        };

        if (includeHostPrivateKeyProperty)
        {
            ssh["HostPrivateKey"] = hostPrivateKey;
        }

        var localUser = new JsonObject
        {
            ["Username"] = username,
            ["Enabled"] = true,
            ["PrivilegeLevel"] = privilegeLevel,
            ["PasswordHash"] = passwordHash ?? string.Empty,
            ["AuthorizedPublicKeys"] = new JsonArray(
                authorizedPublicKeys.Select(k => JsonValue.Create(k)).ToArray())
        };

        var root = new JsonObject
        {
            ["TechConnectServer"] = new JsonObject
            {
                ["Ssh"] = ssh,
                ["Authentication"] = new JsonObject
                {
                    ["Local"] = new JsonObject
                    {
                        ["PasswordAuthenticationEnabled"] = passwordAuthenticationEnabled,
                        ["PublicKeyAuthenticationEnabled"] = publicKeyAuthenticationEnabled,
                        ["LocalUsers"] = new JsonArray(localUser)
                    }
                }
            },
            ["Logging"] = new JsonObject
            {
                ["File"] = new JsonObject
                {
                    ["Directory"] = "logs",
                    ["FileNamePrefix"] = "test",
                    ["RetainedFileCountLimit"] = 3,
                    ["FileSizeLimitBytes"] = 1048576
                }
            },
            ["UnrelatedSection"] = new JsonObject
            {
                ["Marker"] = extraMarker
            }
        };

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    }

    /// <summary>
    /// Stops the host with a bounded timeout.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _host.StopAsync(timeout.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the host and best-effort deletes the temp content root.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort test cleanup.
        }

        _host.Dispose();

        try
        {
            if (Directory.Exists(_contentRoot))
            {
                Directory.Delete(_contentRoot, recursive: true);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>
    /// Connects with public-key auth, runs CLI commands, returns auth success and captured output.
    /// </summary>
    public static async Task<(bool Authenticated, string Output)> ConnectAndRunAsync(
        int port,
        IKeyPair serverHostKey,
        string username,
        IKeyPair clientKey,
        IEnumerable<string> commands,
        CancellationToken cancellationToken = default)
    {
        var credentials = new SshClientCredentials(username, [clientKey]);
        return await ConnectAndRunCoreAsync(
                port,
                serverHostKey,
                credentials,
                includePublicKey: true,
                includePassword: false,
                commands,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Connects with password auth, runs CLI commands, returns auth success and captured output.
    /// </summary>
    public static async Task<(bool Authenticated, string Output)> ConnectWithPasswordAndRunAsync(
        int port,
        IKeyPair serverHostKey,
        string username,
        string password,
        IEnumerable<string> commands,
        CancellationToken cancellationToken = default)
    {
        var credentials = new SshClientCredentials(username, password);
        return await ConnectAndRunCoreAsync(
                port,
                serverHostKey,
                credentials,
                includePublicKey: false,
                includePassword: true,
                commands,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared DevTunnels SSH client connect/shell/command runner for test helpers.
    /// </summary>
    private static async Task<(bool Authenticated, string Output)> ConnectAndRunCoreAsync(
        int port,
        IKeyPair serverHostKey,
        SshClientCredentials credentials,
        bool includePublicKey,
        bool includePassword,
        IEnumerable<string> commands,
        CancellationToken cancellationToken)
    {
        var config = new SshSessionConfiguration();
        config.AuthenticationMethods.Clear();
        if (includePublicKey)
        {
            config.AuthenticationMethods.Add(AuthenticationMethods.PublicKey);
        }

        if (includePassword)
        {
            config.AuthenticationMethods.Add(AuthenticationMethods.Password);
        }

        var client = new SshClient(config, new TraceSource("SshTestClient", SourceLevels.Off));
        SshClientSession? session = null;
        try
        {
            session = await client.OpenSessionAsync("127.0.0.1", port).ConfigureAwait(false);
            session.Authenticating += (_, e) =>
            {
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(
                    e.PublicKey is not null &&
                    e.PublicKey.GetPublicKeyBytes().Equals(serverHostKey.GetPublicKeyBytes())
                        ? new ClaimsPrincipal(new ClaimsIdentity("test"))
                        : null);
            };

            var authenticated = await session.AuthenticateAsync(credentials).ConfigureAwait(false);
            if (!authenticated)
            {
                return (false, string.Empty);
            }

            var channel = await session.OpenChannelAsync().ConfigureAwait(false);
            var shellOk = await channel.RequestAsync(new ShellRequestMessage()).ConfigureAwait(false);
            if (!shellOk)
            {
                return (true, string.Empty);
            }

            await using var stream = new SshStream(channel);
            var buffer = new StringBuilder();
            var searchFrom = 0;
            searchFrom = await ReadUntilPromptAsync(stream, buffer, searchFrom, cancellationToken)
                .ConfigureAwait(false);

            foreach (var command in commands)
            {
                var payload = Encoding.UTF8.GetBytes(command + "\r\n");
                await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

                if (command.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                    command.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    searchFrom = await ReadUntilAsync(stream, buffer, searchFrom, "Goodbye.", cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    searchFrom = await ReadUntilPromptAsync(stream, buffer, searchFrom, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return (true, buffer.ToString());
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.CloseAsync(SshDisconnectReason.ByApplication, "test complete").ConfigureAwait(false);
                }
                catch
                {
                    session.Dispose();
                }
            }

            client.Dispose();
        }
    }

    /// <summary>
    /// Test support member <c>ReadUntilPromptAsync</c>.
    /// </summary>
    private static Task<int> ReadUntilPromptAsync(
        Stream stream,
        StringBuilder buffer,
        int searchFrom,
        CancellationToken cancellationToken) =>
        ReadUntilAsync(
            stream,
            buffer,
            searchFrom,
            static text =>
            {
                var hash = text.IndexOf("TechConnect#", StringComparison.Ordinal);
                if (hash >= 0)
                {
                    return (hash, "TechConnect#".Length);
                }

                var gt = text.IndexOf("TechConnect>", StringComparison.Ordinal);
                return gt >= 0 ? (gt, "TechConnect>".Length) : (-1, 0);
            },
            "TechConnect#|TechConnect>",
            cancellationToken);

    /// <summary>
    /// Test support member <c>ReadUntilAsync</c>.
    /// </summary>
    private static Task<int> ReadUntilAsync(
        Stream stream,
        StringBuilder buffer,
        int searchFrom,
        string marker,
        CancellationToken cancellationToken) =>
        ReadUntilAsync(
            stream,
            buffer,
            searchFrom,
            text =>
            {
                var matchIndex = text.IndexOf(marker, StringComparison.Ordinal);
                return matchIndex >= 0 ? (matchIndex, marker.Length) : (-1, 0);
            },
            marker,
            cancellationToken);

    /// <summary>
    /// Test support member <c>ReadUntilAsync</c>.
    /// </summary>
    private static async Task<int> ReadUntilAsync(
        Stream stream,
        StringBuilder buffer,
        int searchFrom,
        Func<string, (int Index, int Length)> findMarker,
        string markerDescription,
        CancellationToken cancellationToken)
    {
        var byteBuffer = new byte[256];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = buffer.ToString();
            var slice = text.Length <= searchFrom ? string.Empty : text[searchFrom..];
            var (relativeIndex, length) = findMarker(slice);
            if (relativeIndex >= 0)
            {
                return searchFrom + relativeIndex + length;
            }

            var readTask = stream.ReadAsync(byteBuffer, cancellationToken).AsTask();
            var completed = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken))
                .ConfigureAwait(false);
            if (completed != readTask)
            {
                continue;
            }

            var read = await readTask.ConfigureAwait(false);
            if (read == 0)
            {
                text = buffer.ToString();
                slice = text.Length <= searchFrom ? string.Empty : text[searchFrom..];
                (relativeIndex, length) = findMarker(slice);
                if (relativeIndex >= 0)
                {
                    return searchFrom + relativeIndex + length;
                }

                throw new EndOfStreamException(
                    $"Stream closed before marker '{markerDescription}' was observed.");
            }

            buffer.Append(Encoding.UTF8.GetString(byteBuffer, 0, read));
        }

        throw new TimeoutException(
            $"Timed out waiting for marker '{markerDescription}'. Output so far:{Environment.NewLine}{buffer}");
    }

    /// <summary>
    /// Allocates an ephemeral free TCP port on loopback.
    /// </summary>
    internal static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Polls until the SSH listen port accepts connections or timeout elapses.
    /// </summary>
    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException($"SSH test host did not begin listening on port {port}.");
    }
}
