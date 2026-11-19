// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    WebSocketFramingHardeningTests.cs
//
// Purpose:
// Verifies WebSocket framing hardening for fragmented and oversized messages.
//
// -----------------------------------------------------------------------------

using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Client.Tests.Support;
using TechConnect.Client.Transport;

namespace TechConnect.Client.Tests.Transport;

/// <summary>
/// WebSocket framing hardening: concurrency, fragmentation, oversized messages, post-disconnect send.
/// </summary>
public sealed class WebSocketFramingHardeningTests
{
    /// <summary>
    /// Scenario: concurrent binary and text sends to WssTestServer.
    /// Expected: payloads arrive intact without cross-type corruption.
    /// </summary>
    [Fact]
    public async Task Concurrent_binary_and_text_sends_arrive_intact()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var binaryPayloads = Enumerable.Range(0, 15)
            .Select(i => Encoding.UTF8.GetBytes($"bin-{i}"))
            .ToArray();
        var textPayloads = Enumerable.Range(0, 15)
            .Select(i => ControlProtocol.Serialize(ControlProtocol.Create(
                ControlProtocol.TypeError,
                new ErrorPayload { Message = $"txt-{i}" })))
            .ToArray();

        var tasks = new List<Task>();
        for (var i = 0; i < binaryPayloads.Length; i++)
        {
            var bin = binaryPayloads[i];
            var text = textPayloads[i];
            tasks.Add(transport.SendSerialDataAsync(bin, CancellationToken.None));
            tasks.Add(transport.SendControlMessageAsync(text, CancellationToken.None));
        }

        await Task.WhenAll(tasks);

        await TestHelpers.WaitUntilAsync(
            () => server.ReceivedBinary.Count >= binaryPayloads.Length &&
                  server.ReceivedText.Count >= textPayloads.Length,
            timeout: TimeSpan.FromSeconds(10));

        var binaries = Drain(server.ReceivedBinary);
        var texts = Drain(server.ReceivedText);
        binaries.Should().BeEquivalentTo(binaryPayloads, o => o.WithoutStrictOrdering());
        texts.Should().BeEquivalentTo(textPayloads, o => o.WithoutStrictOrdering());
    }

    /// <summary>
    /// Scenario: fragmented binary and text frames.
    /// Expected: reassembled with type isolation preserved.
    /// </summary>
    [Fact]
    public async Task Fragmented_binary_and_text_are_reassembled_with_type_isolation()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);

        var serial = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.SerialDataReceived += (_, data) => serial.TrySetResult(data.ToArray());
        transport.ControlMessageReceived += (_, json) => control.TrySetResult(json);

        var binary = Enumerable.Range(0, 1200).Select(i => (byte)(i % 256)).ToArray();
        await server.SendBinaryFragmentAsync(binary.AsMemory(0, 400), endOfMessage: false);
        await server.SendBinaryFragmentAsync(binary.AsMemory(400, 400), endOfMessage: false);
        await server.SendBinaryFragmentAsync(binary.AsMemory(800), endOfMessage: true);

        var hello = ControlProtocol.Serialize(ControlProtocol.Create(
            ControlProtocol.TypeConnectorHello,
            new ConnectorHelloPayload
            {
                ClientSessionId = "x",
                Hostname = "h",
                PortName = "COM1",
                Config = new SerialConfigPayload { BaudRate = 115200 }
            }));
        var helloBytes = Encoding.UTF8.GetBytes(hello);
        var mid = helloBytes.Length / 2;
        await server.SendTextFragmentAsync(helloBytes.AsMemory(0, mid), endOfMessage: false);
        await server.SendTextFragmentAsync(helloBytes.AsMemory(mid), endOfMessage: true);

        (await serial.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Equal(binary);
        (await control.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(hello);
    }

    /// <summary>
    /// Scenario: oversized inbound WebSocket message.
    /// Expected: transport faults without raising SerialDataReceived.
    /// </summary>
    [Fact]
    public async Task Oversized_inbound_message_faults_without_raising_serial()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server, o => o.MaximumMessageBytes = 4096);
        await transport.ConnectAsync(CancellationToken.None);

        var serialRaised = false;
        transport.SerialDataReceived += (_, _) => serialRaised = true;
        var faulted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Faulted += (_, message) => faulted.TrySetResult(message);

        var oversized = new byte[8_000];
        Random.Shared.NextBytes(oversized);
        await server.SendBinaryAsync(oversized);

        var fault = await faulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fault.Should().Match(m => m.Contains("too large", StringComparison.OrdinalIgnoreCase));
        serialRaised.Should().BeFalse();
    }

    /// <summary>
    /// Scenario: Send after DisconnectAsync.
    /// Expected: fails promptly without deadlocking the send gate.
    /// </summary>
    [Fact]
    public async Task Send_after_disconnect_fails_without_deadlocking_gate()
    {
        await using var server = await WssTestServer.StartWithPersistentCertificatesAsync();
        await using var transport = CreateTransport(server);
        await transport.ConnectAsync(CancellationToken.None);
        await transport.DisconnectAsync(CancellationToken.None);

        var act = async () => await transport.SendSerialDataAsync("x"u8.ToArray(), CancellationToken.None);
        await act.Should().ThrowAsync<RemoteTransportUnavailableException>();

        var controlAct = async () => await transport.SendControlMessageAsync("{}", CancellationToken.None);
        await controlAct.Should().ThrowAsync<RemoteTransportUnavailableException>();
    }

    /// <summary>
    /// Builds a WebSocketRemoteConsoleTransport for the given options/cert provider.
    /// </summary>
    private static WebSocketRemoteConsoleTransport CreateTransport(
        WssTestServer server,
        Action<TechConnect.Client.Configuration.RemoteServiceOptions>? configure = null)
    {
        var options = new TechConnect.Client.Configuration.RemoteServiceOptions
        {
            BaseUrl = server.HttpsUri.ToString().TrimEnd('/'),
            WebSocketPath = "/ws",
            ConnectTimeoutSeconds = 5,
            HeartbeatIntervalSeconds = 15,
            MaximumMessageBytes = 65_536,
            ClientCertificate = new TechConnect.Client.Configuration.ClientCertificateOptions { Thumbprint = string.Empty }
        };
        configure?.Invoke(options);

        return new WebSocketRemoteConsoleTransport(
            new FakeClientCertificateProvider { IsConfigured = false },
            Microsoft.Extensions.Options.Options.Create(options),
            new CollectingLogger<WebSocketRemoteConsoleTransport>());
    }

    /// <summary>
    /// Drains a ConcurrentQueue into a List for assertions.
    /// </summary>
    private static List<T> Drain<T>(System.Collections.Concurrent.ConcurrentQueue<T> queue)
    {
        var list = new List<T>();
        while (queue.TryDequeue(out var item))
        {
            list.Add(item);
        }

        return list;
    }
}
