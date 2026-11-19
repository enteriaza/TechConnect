// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialRxOwnershipContractTests.cs
//
// Purpose:
// Deterministic contract and ingress-race tests for PERF-CLI-01 RX ownership
// (ephemeral local vs durable remote ingress).
//
// -----------------------------------------------------------------------------

using System.Text;
using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Serial;

/// <summary>
/// Contract tests for serial RX payload lifetime and ingress publication gating.
/// </summary>
public sealed class SerialRxOwnershipContractTests
{
    private static (SerialConnectionService Service, FakeSerialPortFactory Factory) Create()
    {
        var factory = new FakeSerialPortFactory();
        var service = new SerialConnectionService(
            new CollectingLogger<SerialConnectionService>(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
            factory);
        return (service, factory);
    }

    /// <summary>
    /// Scenario: ingress disabled; local console copies during DataTransferred.
    /// Expected: drained ring contains distinct successive payloads (synchronous copy).
    /// </summary>
    [Fact]
    public async Task LocalConsole_CopiesEphemeralSynchronously_PreservesDistinctReads()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.IsRemoteIngressEnabled.Should().BeFalse();

            var console = new SerialConsoleBuffer(maxPendingBytes: 8192);
            var callbacks = 0;
            service.DataTransferred += (_, chunk) =>
            {
                console.Enqueue(chunk);
                Interlocked.Increment(ref callbacks);
            };

            factory.Ports["COM1"].InjectRx("1111"u8.ToArray());
            factory.Ports["COM1"].InjectRx("2222"u8.ToArray());
            await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref callbacks) >= 2);

            console.TryDrain(out var data, out var dropped).Should().BeTrue();
            dropped.Should().Be(0);
            data.Should().Equal("11112222"u8.ToArray());
            service.Incoming.TryRead(out _).Should().BeFalse();
        }
    }

    /// <summary>
    /// Scenario: document that retaining ephemeral <see cref="ReadOnlyMemory{T}"/> across reads is unsafe.
    /// Expected: after a second RX overwrites the reusable buffer, a retained first memory aliases new data.
    /// </summary>
    [Fact]
    public async Task RetainingEphemeralMemory_AcrossReads_AliasesReusableBuffer()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(false);

            ReadOnlyMemory<byte> retained = default;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            service.DataTransferred += (_, chunk) =>
            {
                if (chunk.Direction != SerialDataDirection.Rx)
                {
                    return;
                }

                // Contract violation: store memory without copying.
                if (Interlocked.Increment(ref count) == 1)
                {
                    retained = chunk.Data;
                    first.TrySetResult();
                }
                else
                {
                    second.TrySetResult();
                }
            };

            factory.Ports["COM1"].InjectRx("AAAA"u8.ToArray());
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));

            factory.Ports["COM1"].InjectRx("BBBB"u8.ToArray());
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Ephemeral view was overwritten by the next ReadAsync into the same buffer.
            Encoding.ASCII.GetString(retained.Span).Should().Be("BBBB");
        }
    }

    /// <summary>
    /// Scenario: process RX while ingress disabled, then enable ingress.
    /// Expected: the already-handled ephemeral chunk never appears on Incoming.
    /// </summary>
    [Fact]
    public async Task EnableAfterLocalOnlyRead_DoesNotPublishPriorEphemeralToIncoming()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(false);

            var localSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.DataTransferred += (_, chunk) =>
            {
                if (chunk.Direction == SerialDataDirection.Rx && chunk.Data.Length == 5)
                {
                    localSeen.TrySetResult();
                }
            };

            factory.Ports["COM1"].InjectRx("local"u8.ToArray());
            await localSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));

            service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel.Should().Be(0);
            service.Incoming.TryRead(out _).Should().BeFalse();

            service.SetRemoteIngressEnabled(true);
            // Prior chunk must not appear; only subsequent durable reads publish.
            service.Incoming.TryRead(out _).Should().BeFalse();
            service.GetReceiveDiagnostics().BytesSkippedRemoteIngressDisabled.Should().BeGreaterThanOrEqualTo(5);
        }
    }

    /// <summary>
    /// Scenario: durable Incoming payloads retained across later RX that fills the read buffer.
    /// Expected: earlier channel payloads remain unchanged.
    /// </summary>
    [Fact]
    public async Task DurableIncoming_Unchanged_AfterReusableBufferOverwrite()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            factory.Ports["COM1"].InjectRx("keep"u8.ToArray());
            await TestHelpers.WaitUntilAsync(
                () => service.GetReceiveDiagnostics().BytesPublishedToIncomingChannel >= 4);

            service.Incoming.TryRead(out var first).Should().BeTrue();
            var retained = first!.Data;

            factory.Ports["COM1"].InjectRx(new byte[64]);
            await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 4 + 64);

            Encoding.ASCII.GetString(retained.Span).Should().Be("keep");
        }
    }

    /// <summary>
    /// Scenario: disable waits for an accepted in-flight publisher, then drain.
    /// Expected: no further Incoming items after drain; local RX still delivers.
    /// </summary>
    [Fact]
    public async Task DisableDrain_ThenLocalRx_ContinuesWithoutIncomingGrowth()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(true);

            factory.Ports["COM1"].InjectRx(new byte[32]);
            await TestHelpers.WaitUntilAsync(
                () => service.GetReceiveDiagnostics().PendingIncomingItems > 0);

            service.SetRemoteIngressEnabled(false);
            var drained = service.DrainPendingRemoteIngress();
            drained.Should().BeGreaterThan(0);
            service.GetReceiveDiagnostics().PendingIncomingItems.Should().Be(0);
            service.Incoming.TryRead(out _).Should().BeFalse();

            var local = 0;
            service.DataTransferred += (_, chunk) =>
            {
                if (chunk.Direction == SerialDataDirection.Rx)
                {
                    Interlocked.Add(ref local, chunk.Data.Length);
                }
            };

            factory.Ports["COM1"].InjectRx(new byte[48]);
            await TestHelpers.WaitUntilAsync(() => Volatile.Read(ref local) >= 48);
            service.Incoming.TryRead(out _).Should().BeFalse();
            service.IsOpen.Should().BeTrue();
        }
    }

    /// <summary>
    /// Scenario: skipped counter increases for local-only RX; published stays zero.
    /// Expected: accounting matches diagnostic contract for disabled ingress.
    /// </summary>
    [Fact]
    public async Task LocalOnly_IncrementsSkipped_NotPublished()
    {
        var (service, factory) = Create();
        await using (service)
        {
            await service.OpenAsync("COM1", null, CancellationToken.None);
            service.SetRemoteIngressEnabled(false);

            factory.Ports["COM1"].InjectRx(new byte[100]);
            await TestHelpers.WaitUntilAsync(() => service.GetStats().BytesReceived >= 100);

            var d = service.GetReceiveDiagnostics();
            d.BytesPublishedToIncomingChannel.Should().Be(0);
            d.BytesSkippedRemoteIngressDisabled.Should().Be(100);
            d.BytesDeliveredToDataTransferred.Should().Be(100);
        }
    }
}
