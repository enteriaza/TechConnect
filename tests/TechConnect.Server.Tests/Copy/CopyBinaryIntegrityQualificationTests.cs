// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyBinaryIntegrityQualificationTests.cs
//
// Purpose:
// Qualification: fragmented reads, special byte runs, cancel/failure registry
// release, and concurrent attachment exclusivity for copy transfers.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Copy;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Copy;

/// <summary>Qualification: payload bytes must reach the serial-write path unchanged.</summary>
public sealed class CopyBinaryIntegrityQualificationTests
{
    /// <summary>Fixed timestamp for session registration fixtures.</summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");

    /// <summary>
    /// Member data: fragment sizes used by transfer integrity theory cases.
    /// </summary>
    /// <returns>Theory rows of maximum bytes returned per fragmented source read.</returns>
    public static IEnumerable<object[]> FragmentSizes() =>
    [
        [1],
        [3],
        [17],
        [128],
        [512],
        [1024],
        [8191],
        [8192],
        [9000]
    ];

    /// <summary>
    /// Verifies fragmented source reads still deliver exact opaque bytes (SHA-256 match).
    /// </summary>
    /// <param name="fragmentSize">Max bytes returned per source <c>ReadAsync</c>.</param>
    [Theory]
    [MemberData(nameof(FragmentSizes))]
    public async Task Transfer_FragmentedSourceReads_PreserveExactBytes(int fragmentSize)
    {
        var payload = BuildQualificationPayload(length: 50_000);
        var written = new List<byte>(payload.Length);
        var attachment = CreateAttachment();
        await using var source = new MemoryCopySource(
            "frag",
            payload,
            maxRead: fragmentSize);
        var service = new CopyTransferService(new ActiveCopyRegistry(), NullLogger<CopyTransferService>.Instance);

        var result = await service.TransferAsync(
            source,
            attachment,
            null!,
            "op-a",
            null,
            CancellationToken.None,
            (data, _) =>
            {
                written.AddRange(data.ToArray());
                return Task.CompletedTask;
            });

        result.Success.Should().BeTrue();
        written.Should().Equal(payload);
        Convert.ToHexString(SHA256.HashData(written.ToArray()))
            .Should().Be(Convert.ToHexString(SHA256.HashData(payload)));
    }

    /// <summary>
    /// Verifies: Transfer Long Zero And Ff Runs And Random Preserve Exact Bytes.
    /// </summary>
    [Fact]
    public async Task Transfer_LongZeroAndFfRuns_AndRandom_PreserveExactBytes()
    {
        var payload = new byte[32_768];
        payload.AsSpan(0, 4096).Fill(0x00);
        payload.AsSpan(4096, 4096).Fill(0xFF);
        RandomNumberGenerator.Fill(payload.AsSpan(8192));
        // Ensure CR/LF/NUL/invalid UTF-8 appear in the random region too.
        payload[8192] = 0x00;
        payload[8193] = 0x0D;
        payload[8194] = 0x0A;
        payload[8195] = 0x80;
        payload[8196] = 0xFF;

        var written = new List<byte>();
        await TransferToListAsync(payload, written);
        written.Should().Equal(payload);
    }

    /// <summary>
    /// Verifies: Transfer Cancellation Stops Further Writes And Releases Registry.
    /// </summary>
    [Fact]
    public async Task Transfer_Cancellation_StopsFurtherWrites_AndReleasesRegistry()
    {
        var payload = new byte[64_000];
        RandomNumberGenerator.Fill(payload);
        var written = 0;
        var registry = new ActiveCopyRegistry();
        var attachment = CreateAttachment();
        using var cts = new CancellationTokenSource();
        await using var source = new MemoryCopySource("cancel", payload, maxRead: 1000);
        var service = new CopyTransferService(registry, NullLogger<CopyTransferService>.Instance);

        var result = await service.TransferAsync(
            source,
            attachment,
            null!,
            "op-a",
            null,
            cts.Token,
            async (data, ct) =>
            {
                written += data.Length;
                if (written >= 5000)
                {
                    await cts.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }
            });

        result.Cancelled.Should().BeTrue();
        result.Success.Should().BeFalse();
        written.Should().BeLessThan(payload.Length);
        registry.IsActive(attachment.SessionId).Should().BeFalse();
    }

    /// <summary>
    /// Verifies: Concurrent Transfers Same Attachment Second Rejected Different Ok.
    /// </summary>
    [Fact]
    public async Task ConcurrentTransfers_SameAttachment_SecondRejected_DifferentOk()
    {
        var registry = new ActiveCopyRegistry();
        registry.TryBegin("sess-a", "op-1").Should().BeTrue();
        registry.TryBegin("sess-a", "op-2").Should().BeFalse();
        registry.TryBegin("sess-b", "op-2").Should().BeTrue();
        registry.End("sess-a", "op-2"); // wrong owner — must not release
        registry.IsActive("sess-a").Should().BeTrue();
        registry.End("sess-a", "op-1");
        registry.IsActive("sess-a").Should().BeFalse();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Verifies: Transfer Failure Releases Registry.
    /// </summary>
    [Fact]
    public async Task Transfer_Failure_ReleasesRegistry()
    {
        var registry = new ActiveCopyRegistry();
        var attachment = CreateAttachment();
        await using var source = new MemoryCopySource("fail", new byte[] { 1, 2, 3 });
        var service = new CopyTransferService(registry, NullLogger<CopyTransferService>.Instance);

        var result = await service.TransferAsync(
            source,
            attachment,
            null!,
            "op-a",
            null,
            CancellationToken.None,
            (_, _) => throw new IOException("serial write failed"));

        result.Success.Should().BeFalse();
        result.Message.Should().Be("% Copy failed.");
        registry.IsActive(attachment.SessionId).Should().BeFalse();
    }

    /// <summary>
    /// Builds a deterministic binary payload including edge byte values.
    /// </summary>
    internal static byte[] BuildQualificationPayload(int length)
    {
        var payload = new byte[length];
        for (var i = 0; i < 256 && i < length; i++)
        {
            payload[i] = (byte)i;
        }

        for (var i = 256; i < length; i++)
        {
            payload[i] = (byte)(i * 17);
        }

        if (length > 300)
        {
            payload.AsSpan(256, Math.Min(64, length - 256)).Fill(0x00);
            payload.AsSpan(320, Math.Min(64, length - 320)).Fill(0xFF);
        }

        return payload;
    }

    /// <summary>
    /// Transfers a payload through CopyTransferService into <paramref name="written"/>.
    /// </summary>
    private static async Task TransferToListAsync(byte[] payload, List<byte> written)
    {
        var attachment = CreateAttachment();
        await using var source = new MemoryCopySource("bin", payload, maxRead: 100);
        var service = new CopyTransferService(new ActiveCopyRegistry(), NullLogger<CopyTransferService>.Instance);
        var result = await service.TransferAsync(
            source,
            attachment,
            null!,
            "op-a",
            null,
            CancellationToken.None,
            (data, _) =>
            {
                written.AddRange(data.ToArray());
                return Task.CompletedTask;
            });
        result.Success.Should().BeTrue();
    }

    /// <summary>
    /// Creates a privilege-1 operator attachment fixture for transfer tests.
    /// </summary>
    /// <returns>Attachment bound to session <c>sess1</c>.</returns>
    private static OperatorAttachment CreateAttachment() =>
        new(
            "op-a",
            "sess1",
            "conn1",
            "host",
            T0,
            TestAuthenticatedOperator.Create("noc", PrivilegeLevel.FromInt(1), operatorSessionId: "op-a"));

    /// <summary>
    /// In-memory <see cref="ICopyFileSource"/> that optionally fragments reads.
    /// </summary>
    private sealed class MemoryCopySource : ICopyFileSource
    {
        /// <summary>Underlying fragmenting payload stream.</summary>
        private readonly FragmentingStream _stream;

        /// <summary>
        /// Creates a source over <paramref name="payload"/> with optional max read size.
        /// </summary>
        /// <param name="name">Display name.</param>
        /// <param name="payload">Raw binary payload.</param>
        /// <param name="maxRead">Maximum bytes per read.</param>
        public MemoryCopySource(string name, byte[] payload, int maxRead = int.MaxValue)
        {
            DisplayName = name;
            ContentLength = payload.Length;
            _stream = new FragmentingStream(payload, maxRead);
        }

        /// <inheritdoc />
        public string DisplayName { get; }

        /// <inheritdoc />
        public long? ContentLength { get; }

        /// <inheritdoc />
        public Stream Content => _stream;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Read-only stream that fragments reads to a configured maximum size.
    /// </summary>
    private sealed class FragmentingStream : Stream
    {
        /// <summary>Full payload buffer.</summary>
        private readonly byte[] _payload;

        /// <summary>Maximum bytes returned per read.</summary>
        private readonly int _maxRead;

        /// <summary>Current read offset.</summary>
        private int _offset;

        /// <summary>
        /// Creates a read-only stream that returns at most <paramref name="maxRead"/> bytes per read.
        /// </summary>
        /// <param name="payload">Binary payload.</param>
        /// <param name="maxRead">Maximum bytes per read (clamped to at least 1).</param>
        public FragmentingStream(byte[] payload, int maxRead)
        {
            _payload = payload;
            _maxRead = Math.Max(1, maxRead);
        }

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _payload.Length;

        /// <inheritdoc />
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        /// <inheritdoc />
        public override void Flush() { }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= _payload.Length)
            {
                return ValueTask.FromResult(0);
            }

            var n = Math.Min(Math.Min(buffer.Length, _maxRead), _payload.Length - _offset);
            _payload.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return ValueTask.FromResult(n);
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

