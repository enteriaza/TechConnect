// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SerialConfigurationIntegrationTests.cs
//
// Purpose:
// Verifies serial configuration cache seeding from Hello, SerialConfiguration updates, and SetBaudResult completion.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Framing;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;
using TechConnect.Server.Attachment;
using TechConnect.Server.Sessions;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Connector;

/// <summary>
/// Integration tests for connector-reported serial configuration cache behaviour.
/// </summary>
public sealed class SerialConfigurationIntegrationTests
{
    /// <summary>
    /// Verifies Hello with baud seeds the cache and disconnect clears it.
    /// </summary>
    [Fact]
    public async Task Hello_SeedsSerialConfigurationCache()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "cfg",
            Hostname = "router-001",
            PortName = "COM3",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            Handshake = "None"
        });

        _ = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol.Reader.ReadAsync())!.Value.Payload);
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));

        var session = host.SessionRegistry.List().Single();
        host.SerialConfigCache.TryGet(session.SessionId, out var cached).Should().BeTrue();
        cached!.BaudRate.Should().Be(115200);
        cached.PortName.Should().Be("COM3");

        await ssl!.DisposeAsync();
        tcp!.Dispose();
        await host.WaitForRegisteredCountAsync(0, TimeSpan.FromSeconds(5));
        host.SerialConfigCache.TryGet(session.SessionId, out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a SerialConfiguration frame updates cached baud and framing fields.
    /// </summary>

    [Fact]
    public async Task SerialConfigurationMessage_UpdatesCache()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "cfg2",
            Hostname = "h",
            PortName = "COM1",
            BaudRate = 9600
        });
        _ = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol.Reader.ReadAsync())!.Value.Payload);
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));
        var session = host.SessionRegistry.List().Single();

        await protocol.WriteControlAsync(
            MessageType.SerialConfiguration,
            new SerialConfigurationMessage
            {
                BaudRate = 57600,
                DataBits = 7,
                Parity = "Even",
                StopBits = "Two",
                Handshake = "RequestToSend"
            });

        await TestWait.UntilAsync(
            () => host.SerialConfigCache.TryGet(session.SessionId, out var c) && c!.BaudRate == 57600,
            TimeSpan.FromSeconds(5));

        host.SerialConfigCache.TryGet(session.SessionId, out var cached).Should().BeTrue();
        cached!.DataBits.Should().Be(7);
        cached.Parity.Should().Be("Even");

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies successful SetBaudResult completes the pending request and refreshes the cache.
    /// </summary>

    [Fact]
    public async Task SetBaudResult_CompletesPendingRequest_AndUpdatesCacheOnSuccess()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "baud",
            Hostname = "h",
            PortName = "COM1",
            BaudRate = 115200
        });
        _ = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol.Reader.ReadAsync())!.Value.Payload);
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));
        var session = host.SessionRegistry.List().Single();

        host.AttachmentManager.TryAttach("op-1", session.SessionId, session.ConnectionId)
            .Succeeded.Should().BeTrue();
        host.PendingBaud.TryRegister("op-1", session.SessionId, 9600, out var pending, out _)
            .Should().BeTrue();

        await protocol.WriteControlAsync(
            MessageType.SetBaudResult,
            new SetBaudResultMessage
            {
                CorrelationId = pending!.CorrelationId,
                Success = true,
                Config = new SerialConfigurationMessage
                {
                    BaudRate = 9600,
                    DataBits = 8,
                    Parity = "None",
                    StopBits = "One",
                    Handshake = "None"
                }
            });

        var result = await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeTrue();
        result.EffectiveBaudRate.Should().Be(9600);
        host.SerialConfigCache.TryGet(session.SessionId, out var cached).Should().BeTrue();
        cached!.BaudRate.Should().Be(9600);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }

    /// <summary>
    /// Verifies failed SetBaudResult completes pending work without changing cached baud.
    /// </summary>

    [Fact]
    public async Task FailedSetBaudResult_DoesNotUpdateCache()
    {
        await using var host = await ConnectorTestHost.StartAsync();
        using var clientCert = host.CreateClientCertificate();
        var (ssl, tcp, error) = await host.ConnectClientAsync(clientCert);
        error.Should().BeNull();

        await using var protocol = new ProtocolSession(ssl!);
        await protocol.WriteHelloAsync(new HelloMessage
        {
            ClientSessionId = "baud-fail",
            Hostname = "h",
            PortName = "COM1",
            BaudRate = 115200
        });
        _ = ControlMessageSerializer.DeserializeHelloResult(
            (await protocol.Reader.ReadAsync())!.Value.Payload);
        await host.WaitForRegisteredCountAsync(1, TimeSpan.FromSeconds(5));
        var session = host.SessionRegistry.List().Single();
        host.SerialConfigCache.TryGet(session.SessionId, out var before).Should().BeTrue();

        host.AttachmentManager.TryAttach("op-1", session.SessionId, session.ConnectionId)
            .Succeeded.Should().BeTrue();
        host.PendingBaud.TryRegister("op-1", session.SessionId, 12345, out var pending, out _)
            .Should().BeTrue();

        await protocol.WriteControlAsync(
            MessageType.SetBaudResult,
            new SetBaudResultMessage
            {
                CorrelationId = pending!.CorrelationId,
                Success = false,
                Error = "Unsupported baud rate."
            });

        var result = await pending.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        result.Success.Should().BeFalse();
        host.SerialConfigCache.TryGet(session.SessionId, out var after).Should().BeTrue();
        after!.BaudRate.Should().Be(before!.BaudRate);

        await ssl!.DisposeAsync();
        tcp!.Dispose();
    }
}

/// <summary>
/// Small polling helper for integration tests waiting on asynchronous server-side cache updates.
/// </summary>
internal static class TestWait
{
    /// <summary>
    /// Polls <paramref name="condition"/> until true or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="condition">Predicate polled every 25ms.</param>
    /// <param name="timeout">Maximum wait.</param>
    /// <returns>A task that completes when the condition is met.</returns>
    /// <exception cref="TimeoutException">Condition was not met before timeout.</exception>
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Condition was not met.");
    }
}
