// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    HttpCopyQualificationTests.cs
//
// Purpose:
// Qualification coverage for HTTP copy: loopback SHA-256 integrity, empty files,
// non-success/redirect rejection, auth error opacity, mid-stream failure, cancel.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Qualification tests for HttpCopyFileSource binary streaming and failure modes.
/// </summary>
/// <remarks>
/// Passwords must not appear in exception messages; response bodies are treated as binary.
/// </remarks>
public sealed class HttpCopyQualificationTests
{
    /// <summary>
    /// Verifies: Http Live Loopback Streams Large Binary Exact Sha256.
    /// </summary>
    [Fact]
    public async Task Http_LiveLoopback_StreamsLargeBinary_ExactSha256()
    {
        var payload = CopyBinaryIntegrityQualificationTests.BuildQualificationPayload(100_000);
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload));

        using var listener = new HttpListener();
        var prefix = $"http://127.0.0.1:{GetFreePort()}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var serve = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Request.HttpMethod.Should().Be("GET");
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = payload.Length;
            await ctx.Response.OutputStream.WriteAsync(payload);
            ctx.Response.Close();
        });

        try
        {
            var sourceUri = new CopySourceUri
            {
                Kind = CopySourceKind.Http,
                DisplayUri = prefix + "fw.bin",
                Host = "127.0.0.1",
                Port = new Uri(prefix).Port,
                Path = "/fw.bin"
            };

            await using var source = await HttpCopyFileSource.OpenAsync(
                sourceUri,
                NullLogger.Instance,
                CancellationToken.None);

            source.ContentLength.Should().Be(payload.Length);
            var ms = new MemoryStream();
            await source.Content.CopyToAsync(ms);
            Convert.ToHexString(SHA256.HashData(ms.ToArray())).Should().Be(expectedHash);
            await serve;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Verifies: Http Empty File Succeeds.
    /// </summary>
    [Fact]
    public async Task Http_EmptyFile_Succeeds()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentLength = 0;
            return Task.FromResult(response);
        });

        await using var source = await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/empty"),
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        var buf = new MemoryStream();
        await source.Content.CopyToAsync(buf);
        buf.Length.Should().Be(0);
    }

    /// <summary>
    /// Verifies: Http Non Success Rejected.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Http_NonSuccess_Rejected(HttpStatusCode status)
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status)));

        var act = async () => await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/x"),
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{(int)status}*");
    }

    /// <summary>
    /// Verifies: Http Redirect Not Followed.
    /// </summary>
    [Fact]
    public async Task Http_Redirect_NotFollowed()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://evil.example/steal");
            return Task.FromResult(response);
        });

        var act = async () => await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/x", user: "u", pass: "p"),
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*302*");
    }

    /// <summary>
    /// Verifies: Http Basic Auth Failure Path Does Not Log Password In Exception.
    /// </summary>
    [Fact]
    public async Task Http_BasicAuth_FailurePath_DoesNotLogPassword_InException()
    {
        var handler = new StubHandler((req, _) =>
        {
            req.Headers.Authorization.Should().NotBeNull();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });

        var act = async () => await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/x", user: "alice", pass: "s3cret-value"),
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().NotContain("s3cret");
    }

    /// <summary>
    /// Verifies: Http Mid Stream Failure Surfaces On Read.
    /// </summary>
    [Fact]
    public async Task Http_MidStreamFailure_SurfacesOnRead()
    {
        var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowAfterBytesStream(new byte[] { 1, 2, 3, 4 }))
            };
            return Task.FromResult(response);
        });

        await using var source = await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/x"),
            NullLogger.Instance,
            CancellationToken.None,
            handler);

        var act = async () =>
        {
            var buf = new byte[64];
            while (await source.Content.ReadAsync(buf) > 0)
            {
            }
        };

        await act.Should().ThrowAsync<IOException>();
    }

    /// <summary>
    /// Verifies: Http Cancellation During Open.
    /// </summary>
    [Fact]
    public async Task Http_Cancellation_DuringOpen()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(async (_, ct) =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var act = async () => await HttpCopyFileSource.OpenAsync(
            Uri("http", "h", "/x"),
            NullLogger.Instance,
            cts.Token,
            handler);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Builds a parsed HTTP(S) <see cref="CopySourceUri"/> for tests (password kept off DisplayUri).
    /// </summary>
    /// <param name="scheme"><c>http</c> or <c>https</c>.</param>
    /// <param name="host">Host name.</param>
    /// <param name="path">Absolute path.</param>
    /// <param name="user">Optional username.</param>
    /// <param name="pass">Optional password for Basic auth tests.</param>
    /// <returns>Configured source URI.</returns>
    private static CopySourceUri Uri(
        string scheme,
        string host,
        string path,
        string? user = null,
        string? pass = null) =>
        new()
        {
            Kind = scheme == "https" ? CopySourceKind.Https : CopySourceKind.Http,
            DisplayUri = $"{scheme}://{host}{path}",
            Host = host,
            Path = path,
            Username = user,
            Password = pass
        };

    /// <summary>
    /// Binds an ephemeral loopback TCP port and returns its number.
    /// </summary>
    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Test <see cref="HttpMessageHandler"/> that invokes a supplied callback.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        /// <summary>
        /// Field <c>_handler</c> used by this implementation.
        /// </summary>
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        /// <summary>
        /// Creates an <see cref="HttpMessageHandler"/> that delegates to a test callback.
        /// </summary>
        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        /// <summary>
        /// Forwards the HTTP request to the test callback.
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }

    /// <summary>
    /// Stream that fails with <see cref="IOException"/> once its initial bytes are consumed.
    /// </summary>
    private sealed class ThrowAfterBytesStream : Stream
    {
        /// <summary>
        /// Field <c>_data</c> used by this implementation.
        /// </summary>
        private readonly byte[] _data;
        /// <summary>
        /// Field <c>_offset</c> used by this implementation.
        /// </summary>
        private int _offset;

        /// <summary>
        /// Creates a stream that throws <see cref="IOException"/> after emitting its buffered bytes.
        /// </summary>
        public ThrowAfterBytesStream(byte[] data) => _data = data;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <inheritdoc />
        public override bool CanSeek => false;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _data.Length;

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
            if (_offset >= _data.Length)
            {
                throw new IOException("connection reset");
            }

            var n = Math.Min(2, Math.Min(buffer.Length, _data.Length - _offset));
            _data.AsSpan(_offset, n).CopyTo(buffer.Span);
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
