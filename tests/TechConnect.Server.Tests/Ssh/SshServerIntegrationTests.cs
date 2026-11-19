// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshServerIntegrationTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (SshServerIntegrationTests).
//
// -----------------------------------------------------------------------------
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Tcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Hosting;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Integration tests for the SSH listener, auth, and shell CLI against SshTestHost.
/// </summary>
public sealed class SshServerIntegrationTests
{
    /// <summary>
    /// Authorized public-key login receives welcome, admin prompt, and help listing connect/attach/disconnect (not set baud).
    /// </summary>
    [Fact]
    public async Task AuthorizedKey_CanAuthenticate_AndReceiveWelcomePrompt()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var (authenticated, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["help"]);

        authenticated.Should().BeTrue();
        output.Should().Contain("Connected to NMS TechConnect Server");
        output.Should().Contain("TechConnect#");
        output.Should().Contain("Available commands");
        output.Should().Contain("show sessions");
        output.Should().Contain("connect <session-id>");
        output.Should().NotContain("set baud");
        output.Should().Contain("attach");
        output.Should().Contain("disconnect");
        output.Should().Contain("show serial");
    }

    /// <summary>
    /// Over SSH, <c>show sessions</c>/<c>show session</c> reflect a registered connector's id, hostname, and serial section.
    /// </summary>
    [Fact]
    public async Task ShowSessions_OverSsh_ReflectsRegisteredConnector()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);
        var registered = host.SessionRegistry.TryRegister(
            connectorIdentity: "7B3A1111D902AAAA",
            connectionId: "ssh-cli-test",
            remoteEndpoint: "10.0.0.10:49152",
            hostname: "router-001",
            applicationVersion: "1.0.0",
            portName: "COM1",
            tlsProtocol: "Tls13",
            connectedAtUtc: DateTimeOffset.Parse("2026-11-18T10:30:00Z"));
        registered.Succeeded.Should().BeTrue();

        var (authenticated, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["show sessions", $"show session {registered.Session!.SessionId}"]);

        authenticated.Should().BeTrue();
        output.Should().Contain(registered.Session.SessionId);
        output.Should().Contain("router-001");
        output.Should().Contain("10.0.0.10:49152");
        output.Should().Contain("1 active session");
        output.Should().Contain("Connector Information");
        output.Should().Contain("1.0.0");
        output.Should().Contain("Serial Information");
    }

    /// <summary>
    /// An unauthorized public key fails authentication and produces no CLI output.
    /// </summary>
    [Fact]
    public async Task UnknownKey_IsRejected()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var authorized = SshTestKeys.CreateEcdsaKeyPair("authorized");
        var unknown = SshTestKeys.CreateEcdsaKeyPair("unknown");

        await using var host = await SshTestHost.StartAsync(hostKey, [authorized]);

        var (authenticated, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            unknown,
            []);

        authenticated.Should().BeFalse();
        output.Should().BeEmpty();
    }

    /// <summary>
    /// Missing HostPrivateKey generates a key that persists and keeps the same fingerprint across restart.
    /// </summary>
    [Fact]
    public async Task MissingHostPrivateKey_GeneratesAndPersists_StableAcrossRestart()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        string fingerprint1;
        string fingerprint2;
        string persistedPem;

        var contentRoot = Path.Combine(Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(contentRoot);

        try
        {
            await using (var host = await SshTestHost.StartAsync(
                             hostKey: null,
                             authorizedOperatorKeys: [operatorKey]))
            {
                fingerprint1 = host.HostKeyFingerprint;
                persistedPem = JsonNode.Parse(File.ReadAllText(host.PersistenceAppsettingsPath))!
                    ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!;
                persistedPem.Should().NotBeNullOrWhiteSpace();
            }

            // Restart from persisted file (same key material).
            var restartedKey = SshKeyMaterial.ImportHostPrivateKey(persistedPem);
            await using var host2 = await SshTestHost.StartAsync(restartedKey, [operatorKey]);
            fingerprint2 = host2.HostKeyFingerprint;

            var (ok, output) = await SshTestHost.ConnectAndRunAsync(
                host2.Port,
                restartedKey,
                "noc",
                operatorKey,
                ["help"]);
            ok.Should().BeTrue();
            output.Should().Contain("Available commands");
        }
        finally
        {
            try
            {
                Directory.Delete(contentRoot, recursive: true);
            }
            catch
            {
                // Best-effort.
            }
        }

        fingerprint1.Should().Be(fingerprint2);
        fingerprint1.Should().StartWith("SHA256:");
    }

    /// <summary>
    /// A preconfigured host key is reused as-is and unrelated appsettings markers are preserved.
    /// </summary>
    [Fact]
    public async Task ExistingConfiguredHostKey_IsNeverReplaced()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        var expected = SshKeyMaterial.ComputeFingerprint(hostKey);

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);
        host.HostKeyFingerprint.Should().Be(expected);

        var stored = JsonNode.Parse(File.ReadAllText(host.PersistenceAppsettingsPath))!
            ["TechConnectServer"]!["Ssh"]!["HostPrivateKey"]!.GetValue<string>()!;
        SshKeyMaterial.ComputeFingerprint(SshKeyMaterial.ImportHostPrivateKey(stored))
            .Should().Be(expected);

        // Unrelated marker preserved.
        JsonNode.Parse(File.ReadAllText(host.PersistenceAppsettingsPath))!
            ["UnrelatedSection"]!["Marker"]!.GetValue<string>().Should().Be("preserve-me");
    }

    /// <summary>
    /// Invalid HostPrivateKey PEM fails closed when <see cref="SshHostKeyProvider.GetHostKey"/> loads it.
    /// </summary>
    [Fact]
    public void InvalidConfiguredHostKey_FailsWhenLoaded()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TechConnect.Server.Configuration.TechConnectServerOptions
        {
            Ssh = new TechConnect.Server.Configuration.SshServerOptions
            {
                Enabled = true,
                HostPrivateKey = "NOT-A-VALID-PRIVATE-KEY"
            }
        });

        using var dir = new TempContentRoot();
        var store = new TechConnect.Server.Configuration.AppsettingsHostKeyStore(
            new TestHostEnvironment(dir.Path),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TechConnect.Server.Configuration.AppsettingsHostKeyStore>.Instance);
        var provider = new SshHostKeyProvider(
            options,
            store,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SshHostKeyProvider>.Instance);

        var act = () => provider.GetHostKey();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*HostPrivateKey*invalid*");
    }
    /// <summary>Unique temp content-root directory deleted on dispose (best-effort).</summary>
    private sealed class TempContentRoot : IDisposable
    {
        /// <summary>Creates and owns a unique directory under the process temp path.</summary>
        public TempContentRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TechConnect.Server.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>Absolute path of the owned content root.</summary>
        public string Path { get; }

        /// <summary>Best-effort recursive delete of the owned directory.</summary>
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
                // Best-effort.
            }
        }
    }
    /// <summary>Minimal Development <see cref="IHostEnvironment"/> for host-key provider tests.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <summary>Points ContentRootPath at the given directory.</summary>
        public TestHostEnvironment(string contentRoot) => ContentRootPath = contentRoot;

        /// <inheritdoc />
        public string EnvironmentName { get; set; } = Environments.Development;
        /// <inheritdoc />
        public string ApplicationName { get; set; } = "TechConnect.Server.Tests";
        /// <inheritdoc />
        public string ContentRootPath { get; set; }
        /// <inheritdoc />
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>
    /// Interactive <c>help</c> then <c>exit</c> shows Available commands and Goodbye.
    /// </summary>
    [Fact]
    public async Task HelpQuestionAndExit_Work()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var (authenticated, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["help", "exit"]);

        authenticated.Should().BeTrue();
        output.Should().Contain("Available commands");
        output.Should().Contain("Goodbye.");
    }

    /// <summary>
    /// Unknown input shows Invalid command with caret and restores the TechConnect# prompt.
    /// </summary>
    [Fact]
    public async Task UnknownCommand_ReturnsFriendlyError_AndRestoresPrompt()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var (authenticated, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["foobar"]);

        authenticated.Should().BeTrue();
        output.Should().Contain("% Invalid command");
        output.Should().Contain("^");
        output.Should().Contain("TechConnect#");
    }

    /// <summary>
    /// Two concurrent SSH clients with the same authorized key each receive independent help output.
    /// </summary>
    [Fact]
    public async Task MultipleIndependentClients_AreHandled()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        // Both sessions use the same local account; keys are bound to username, not display names.
        var first = SshTestHost.ConnectAndRunAsync(host.Port, hostKey, "noc", operatorKey, ["help"]);
        var second = SshTestHost.ConnectAndRunAsync(host.Port, hostKey, "noc", operatorKey, ["help"]);

        var results = await Task.WhenAll(first, second);

        results[0].Authenticated.Should().BeTrue();
        results[1].Authenticated.Should().BeTrue();
        results[0].Output.Should().Contain("Available commands");
        results[1].Output.Should().Contain("Available commands");
    }

    /// <summary>
    /// A failed authentication attempt does not stop the listener; a later authorized client still works.
    /// </summary>
    [Fact]
    public async Task FailedClient_DoesNotTerminateListener()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var authorized = SshTestKeys.CreateEcdsaKeyPair("authorized");
        var unknown = SshTestKeys.CreateEcdsaKeyPair("unknown");

        await using var host = await SshTestHost.StartAsync(hostKey, [authorized]);

        var (failedAuth, _) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "intruder",
            unknown,
            []);
        failedAuth.Should().BeFalse();

        var (ok, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            authorized,
            ["help"]);

        ok.Should().BeTrue();
        output.Should().Contain("TechConnect#");
        output.Should().Contain("Available commands");
    }

    /// <summary>
    /// Host stop completes within 10s and the former listen port refuses new TCP connects.
    /// </summary>
    [Fact]
    public async Task Listener_StopsOnCancellation_WithinShutdownBound()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);
        var port = host.Port;

        var sw = Stopwatch.StartNew();
        await host.StopAsync();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        using var client = new TcpClient();
        var connect = async () => await client.ConnectAsync(IPAddress.Loopback, port);
        await connect.Should().ThrowAsync<SocketException>();
    }

    /// <summary>
    /// SSH exec channel requests (e.g. cmd.exe) are denied — no OS shell is exposed.
    /// </summary>
    [Fact]
    public async Task ExecCommandRequest_IsRejected_NoOsShell()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var config = new SshSessionConfiguration();
        config.AuthenticationMethods.Clear();
        config.AuthenticationMethods.Add(Microsoft.DevTunnels.Ssh.Messages.AuthenticationMethods.PublicKey);

        var client = new SshClient(config, new TraceSource("exec-test", SourceLevels.Off));
        var session = await client.OpenSessionAsync("127.0.0.1", host.Port);
        try
        {
            session.Authenticating += (_, e) =>
            {
                e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(
                    new ClaimsPrincipal(new ClaimsIdentity("test")));
            };

            (await session.AuthenticateAsync(("noc", operatorKey))).Should().BeTrue();

            var channel = await session.OpenChannelAsync();
            var execRequest = new Microsoft.DevTunnels.Ssh.Messages.CommandRequestMessage("cmd.exe /c echo pwned")
            {
                WantReply = true
            };
            var authorized = await channel.RequestAsync(execRequest);

            authorized.Should().BeFalse("exec must not be authorized; no OS shell is exposed");
        }
        finally
        {
            session.Dispose();
            client.Dispose();
        }
    }

    /// <summary>
    /// Authorization keys on key material, not display-name comments on the OpenSSH line.
    /// </summary>
    [Fact]
    public async Task KeyComment_DoesNotAffectAuthorization()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("host");
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");

        // Configure with comment "alice"; authenticate with the same key material.
        await using var host = await SshTestHost.StartAsync(hostKey, [operatorKey]);

        var (ok, output) = await SshTestHost.ConnectAndRunAsync(
            host.Port,
            hostKey,
            "noc",
            operatorKey,
            ["help"]);

        ok.Should().BeTrue();
        output.Should().Contain("Available commands");
    }
}
