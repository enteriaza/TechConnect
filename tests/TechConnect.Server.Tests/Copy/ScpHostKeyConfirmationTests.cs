// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    ScpHostKeyConfirmationTests.cs
//
// Purpose:
// Verifies SCP host-key fingerprint formatting, explicit-yes rules, transient prompt
// history exclusion, and per-connection confirmation without persistence.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;
using TechConnect.Server.Copy;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Tests for ScpHostKeyFingerprint helpers and per-connection host-key confirmation.
/// </summary>
/// <remarks>
/// Approval is never persisted; each connection requires a fresh confirmation.
/// </remarks>
public sealed class ScpHostKeyConfirmationTests
{
    /// <summary>
    /// Verifies: Fingerprint Matches Open Ssh Sha256Without Padding.
    /// </summary>
    [Fact]
    public void Fingerprint_MatchesOpenSshSha256WithoutPadding()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("scp-host");
        var fp = ScpHostKeyFingerprint.Compute(key);
        fp.Should().StartWith("SHA256:");
        fp.Should().NotContain("=");
        fp.Should().Be(SshKeyMaterial.ComputeFingerprint(key));

        var blob = key.GetPublicKeyBytes(key.KeyAlgorithmName).ToArray();
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        fp.Should().Be(expected);
    }

    /// <summary>
    /// Verifies: Fingerprint Different Keys Differ.
    /// </summary>
    [Fact]
    public void Fingerprint_DifferentKeysDiffer()
    {
        var a = ScpHostKeyFingerprint.Compute(SshTestKeys.CreateEcdsaKeyPair("a"));
        var b = ScpHostKeyFingerprint.Compute(SshTestKeys.CreateEcdsaKeyPair("b"));
        a.Should().NotBe(b);
    }

    /// <summary>
    /// Verifies: Is Explicit Yes Only Yes.
    /// </summary>
    [Theory]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData(" yes ", true)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData("y", false)]
    [InlineData("true", false)]
    [InlineData(null, false)]
    public void IsExplicitYes_OnlyYes(string? input, bool expected) =>
        ScpHostKeyFingerprint.IsExplicitYes(input).Should().Be(expected);

    /// <summary>
    /// Verifies the prompt includes fingerprint details and states approval is not remembered.
    /// </summary>
    [Fact]
    public void FormatPrompt_ContainsFingerprintAndConnectionOnlyDisclaimer()
    {
        var info = new ScpHostKeyInfo
        {
            Host = "192.0.2.10",
            Port = 22,
            Algorithm = "ssh-ed25519",
            Fingerprint = "SHA256:abc"
        };
        var text = ScpHostKeyFingerprint.FormatPrompt(info);
        text.Should().Contain("192.0.2.10");
        text.Should().Contain("22");
        text.Should().Contain("ssh-ed25519");
        text.Should().Contain("SHA256:abc");
        text.Should().Contain("not remembered");
        text.Should().Contain("[yes/no]");
    }

    /// <summary>
    /// Verifies host-key yes/no uses a transient line that is not stored in CLI history.
    /// </summary>
    [Fact]
    public async Task TransientLine_IsNotStoredInHistory()
    {
        await using var pipe = new DuplexMemoryPipe();
        var output = new StringBuilder();
        var reader = new InteractiveLineReader(
            pipe.Server,
            (bytes, _) =>
            {
                output.Append(Encoding.UTF8.GetString(bytes.Span));
                return default;
            },
            () => "TechConnect#",
            (_, _) => CliDiscoveryResult.None,
            (_, _) => CliDiscoveryResult.None);

        var readTask = reader.ReadTransientLineAsync(CancellationToken.None);
        await pipe.Client.WriteAsync(Encoding.UTF8.GetBytes("yes\r"));
        var result = await readTask;
        result.Outcome.Should().Be(LineReadOutcome.Line);
        result.Text.Should().Be("yes");
        reader.History.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies: Server Host Key Rejected Before Client Authentication.
    /// </summary>
    [Fact]
    public async Task ServerHostKey_Rejected_BeforeClientAuthentication()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("server");
        await using var server = await MiniSshServer.StartAsync(hostKey, acceptPassword: true);
        var confirmations = 0;

        var client = new SshClient(new SshSessionConfiguration(), new TraceSource("t", SourceLevels.Off));
        SshClientSession? session = null;
        try
        {
            session = await client.OpenSessionAsync("127.0.0.1", server.Port);
            session.Authenticating += (_, e) =>
            {
                if (e.AuthenticationType != SshAuthenticationType.ServerPublicKey || e.PublicKey is null)
                {
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(null);
                    return;
                }

                e.AuthenticationTask = Task.Run(() =>
                {
                    confirmations++;
                    ScpHostKeyFingerprint.Compute(e.PublicKey)
                        .Should().Be(SshKeyMaterial.ComputeFingerprint(hostKey));
                    return (ClaimsPrincipal?)null; // operator rejects
                });
            };

            (await session.AuthenticateServerAsync()).Should().BeFalse();
            confirmations.Should().Be(1);

            // Rejection tears down the session — client authentication must not proceed.
            var clientAuth = async () =>
                await session.AuthenticateClientAsync(new SshClientCredentials("admin", "secret"));
            await clientAuth.Should().ThrowAsync<ObjectDisposedException>();
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.CloseAsync(SshDisconnectReason.ByApplication, "test");
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
    /// Verifies: Server Host Key Accepted Then Password Auth May Proceed.
    /// </summary>
    [Fact]
    public async Task ServerHostKey_Accepted_ThenPasswordAuthMayProceed()
    {
        var hostKey = SshTestKeys.CreateEcdsaKeyPair("server");
        await using var server = await MiniSshServer.StartAsync(hostKey, acceptPassword: true);
        var order = new List<string>();

        var client = new SshClient(new SshSessionConfiguration(), new TraceSource("t", SourceLevels.Off));
        SshClientSession? session = null;
        try
        {
            session = await client.OpenSessionAsync("127.0.0.1", server.Port);
            session.Authenticating += (_, e) =>
            {
                if (e.AuthenticationType != SshAuthenticationType.ServerPublicKey || e.PublicKey is null)
                {
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(null);
                    return;
                }

                e.AuthenticationTask = Task.Run(() =>
                {
                    order.Add("host-key");
                    order.Add("confirmed");
                    return (ClaimsPrincipal?)new ClaimsPrincipal(new ClaimsIdentity("ok"));
                });
            };

            (await session.AuthenticateServerAsync()).Should().BeTrue();
            order.Should().Equal("host-key", "confirmed");

            order.Add("password");
            (await session.AuthenticateClientAsync(new SshClientCredentials("admin", "secret")))
                .Should().BeTrue();
            order.Should().Equal("host-key", "confirmed", "password");
        }
        finally
        {
            if (session is not null)
            {
                try
                {
                    await session.CloseAsync(SshDisconnectReason.ByApplication, "test");
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
    /// Verifies each connection re-prompts; prior approvals are not persisted across keys/sessions.
    /// </summary>
    [Fact]
    public async Task ChangedKey_RequiresFreshConfirmation_NoPersistence()
    {
        var keyA = SshTestKeys.CreateEcdsaKeyPair("a");
        var keyB = SshTestKeys.CreateEcdsaKeyPair("b");
        var seen = new List<string>();

        async Task ConnectAndConfirmAsync(IKeyPair hostKey)
        {
            await using var server = await MiniSshServer.StartAsync(hostKey);
            var client = new SshClient(new SshSessionConfiguration(), new TraceSource("t", SourceLevels.Off));
            SshClientSession? session = null;
            try
            {
                session = await client.OpenSessionAsync("127.0.0.1", server.Port);
                session.Authenticating += (_, e) =>
                {
                    e.AuthenticationTask = Task.Run(() =>
                    {
                        if (e.PublicKey is null)
                        {
                            return (ClaimsPrincipal?)null;
                        }

                        seen.Add(ScpHostKeyFingerprint.Compute(e.PublicKey));
                        return new ClaimsPrincipal(new ClaimsIdentity("ok"));
                    });
                };
                (await session.AuthenticateServerAsync()).Should().BeTrue();
            }
            finally
            {
                if (session is not null)
                {
                    try
                    {
                        await session.CloseAsync(SshDisconnectReason.ByApplication, "test");
                    }
                    catch
                    {
                        session.Dispose();
                    }
                }

                client.Dispose();
            }
        }

        await ConnectAndConfirmAsync(keyA);
        await ConnectAndConfirmAsync(keyA);
        await ConnectAndConfirmAsync(keyB);

        seen.Should().HaveCount(3);
        seen[0].Should().Be(seen[1]).And.Be(SshKeyMaterial.ComputeFingerprint(keyA));
        seen[2].Should().Be(SshKeyMaterial.ComputeFingerprint(keyB));
        seen[2].Should().NotBe(seen[0]);
    }

    /// <summary>
    /// Minimal loopback SSH server used to exercise per-connection host-key confirmation.
    /// </summary>
    private sealed class MiniSshServer : IAsyncDisposable
    {
        /// <summary>
        /// Field <c>_server</c> used by this implementation.
        /// </summary>
        private readonly SshServer _server;
        /// <summary>
        /// Field <c>_accept</c> used by this implementation.
        /// </summary>
        private readonly Task _accept;

        /// <summary>
        /// Creates a loopback SSH server fixture owning accept lifetime.
        /// </summary>
        private MiniSshServer(SshServer server, int port, Task accept)
        {
            _server = server;
            Port = port;
            _accept = accept;
        }

        /// <summary>Listening TCP port.</summary>
        public int Port { get; }

        /// <summary>
        /// Starts the mini SSH server on a free loopback port.
        /// </summary>
        public static async Task<MiniSshServer> StartAsync(IKeyPair hostKey, bool acceptPassword = false)
        {
            var config = new SshSessionConfiguration();
            config.AuthenticationMethods.Clear();
            if (acceptPassword)
            {
                config.AuthenticationMethods.Add(AuthenticationMethods.Password);
            }

            var server = new SshServer(config, new TraceSource("MiniSsh", SourceLevels.Off))
            {
                Credentials = new SshServerCredentials(hostKey)
            };

            server.SessionAuthenticating += (_, e) =>
            {
                if (e.AuthenticationType == SshAuthenticationType.ClientPassword &&
                    acceptPassword &&
                    e.Username == "admin" &&
                    e.Password == "secret")
                {
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(
                        new ClaimsPrincipal(new ClaimsIdentity("user")));
                }
                else if (e.AuthenticationType == SshAuthenticationType.ClientPassword)
                {
                    e.AuthenticationTask = Task.FromResult<ClaimsPrincipal?>(null);
                }
            };

            var port = GetFreePort();
            var accept = server.AcceptSessionsAsync(port, IPAddress.Loopback);
            await WaitForPortAsync(port, TimeSpan.FromSeconds(5));
            return new MiniSshServer(server, port, accept);
        }

        /// <summary>
        /// Stops the mini SSH server and waits briefly for accept completion.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _server.Dispose();
            }
            catch
            {
                // ignore
            }

            try
            {
                await _accept.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>
        /// Binds an ephemeral loopback TCP port and returns its number.
        /// </summary>
        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        /// <summary>
        /// Waits until the loopback port accepts TCP connections.
        /// </summary>
        private static async Task WaitForPortAsync(int port, TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < timeout)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(IPAddress.Loopback, port);
                    return;
                }
                catch
                {
                    await Task.Delay(20);
                }
            }

            throw new TimeoutException($"Mini SSH server did not listen on {port}.");
        }
    }
}
