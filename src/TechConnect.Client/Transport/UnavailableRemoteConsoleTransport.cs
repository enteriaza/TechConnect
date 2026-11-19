// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    UnavailableRemoteConsoleTransport.cs
//
// Purpose:
// Test/substitute IRemoteConsoleTransport that never connects; production DI
// registers TcpMtlsRemoteConsoleTransport instead.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;
using TechConnect.Client.Models;

namespace TechConnect.Client.Transport;

/// <summary>
/// Test/substitute transport that never connects.
/// </summary>
/// <remarks>
/// Production DI registers <see cref="TcpMtlsRemoteConsoleTransport"/> instead.
/// All send/connect operations fail with <see cref="RemoteTransportUnavailableException"/>.
/// Events are intentionally no-ops (add/remove discarded).
/// </remarks>
internal sealed class UnavailableRemoteConsoleTransport : IRemoteConsoleTransport
{
    /// <inheritdoc />
    public bool IsConnected => false;

    /// <inheritdoc />
    public Func<ReadOnlyMemory<byte>, CancellationToken, Task>? SerialDataHandler { get; set; }

    /// <inheritdoc />
    public event EventHandler<ReadOnlyMemory<byte>>? SerialDataReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<SerialPortConfiguration>? ConfigurationReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<string>? ControlMessageReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public event EventHandler<string>? Faulted
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    /// <exception cref="RemoteTransportUnavailableException">Always thrown when not cancelled.</exception>
    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new RemoteTransportUnavailableException(
            "Remote console transport is not implemented yet. The central service is not available."));
    }

    /// <inheritdoc />
    /// <exception cref="RemoteTransportUnavailableException">Always thrown when not cancelled.</exception>
    public Task SendSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new RemoteTransportUnavailableException(
            "Remote console transport is not connected."));
    }

    /// <inheritdoc />
    /// <exception cref="RemoteTransportUnavailableException">Always thrown when not cancelled.</exception>
    public Task SendControlMessageAsync(string json, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new RemoteTransportUnavailableException(
            "Remote console transport is not connected."));
    }

    /// <inheritdoc />
    /// <exception cref="RemoteTransportUnavailableException">Always thrown when not cancelled.</exception>
    public Task SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromException(new RemoteTransportUnavailableException(
            "Remote console transport is not connected."));
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
