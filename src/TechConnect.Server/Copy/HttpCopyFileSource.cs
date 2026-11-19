// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    HttpCopyFileSource.cs
//
// Purpose:
// Opens an HTTP or HTTPS GET response as a streaming binary ICopyFileSource with
// optional Basic auth and no automatic redirects.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace TechConnect.Server.Copy;

/// <summary>
/// HTTP/HTTPS copy source that streams response body bytes without buffering the full file.
/// </summary>
/// <remarks>
/// Request URIs are built from host/path/port only — credentials are sent via the
/// Basic <c>Authorization</c> header, never embedded in the request URI.
/// Auto-redirect is disabled; non-success status codes fail open with an operator
/// message that does not include passwords. Response body is raw binary.
/// </remarks>
internal sealed class HttpCopyFileSource : ICopyFileSource
{
    /// <summary>Shared production client (no redirects, no automatic decompression).</summary>
    private static readonly HttpClient SharedClient = CreateClient();

    /// <summary>Owning response; disposed with the source.</summary>
    private readonly HttpResponseMessage _response;

    /// <summary>Response body stream.</summary>
    private readonly Stream _stream;

    /// <summary>Optional test-owned client when a custom handler was supplied.</summary>
    private readonly HttpClient? _ownedClient;

    /// <summary>
    /// Constructs a source wrapping an already-successful HTTP response stream.
    /// </summary>
    /// <param name="displayName">Credential-free display URI.</param>
    /// <param name="length">Content-Length when present.</param>
    /// <param name="response">Response message to dispose with the source.</param>
    /// <param name="stream">Readable body stream.</param>
    /// <param name="ownedClient">Optional owned client for test handlers.</param>
    private HttpCopyFileSource(
        string displayName,
        long? length,
        HttpResponseMessage response,
        Stream stream,
        HttpClient? ownedClient)
    {
        DisplayName = displayName;
        ContentLength = length;
        _response = response;
        _stream = stream;
        _ownedClient = ownedClient;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public long? ContentLength { get; }

    /// <inheritdoc />
    public Stream Content => _stream;

    /// <summary>
    /// Issues an HTTP GET and returns a streaming file source on success.
    /// </summary>
    /// <param name="source">Parsed HTTP or HTTPS source.</param>
    /// <param name="logger">Logger; uses <see cref="CopySourceUri.DisplayUri"/> only.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <param name="handler">
    /// Optional handler for tests. When provided, a dedicated <see cref="HttpClient"/> is owned
    /// by the returned source. Production code passes null and uses the shared client.
    /// </param>
    /// <returns>Opened source owning the response stream.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is not HTTP/HTTPS.</exception>
    /// <exception cref="InvalidOperationException">Transport failure or non-success status.</exception>
    public static async Task<ICopyFileSource> OpenAsync(
        CopySourceUri source,
        ILogger logger,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Kind is not (CopySourceKind.Http or CopySourceKind.Https))
        {
            throw new ArgumentException("HTTP/HTTPS source required.", nameof(source));
        }

        var uri = BuildRequestUri(source);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(source.Username))
        {
            var token = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{source.Username}:{source.Password ?? string.Empty}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        HttpClient client;
        HttpClient? owned = null;
        if (handler is null)
        {
            client = SharedClient;
        }
        else
        {
            owned = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            client = owned;
        }

        HttpResponseMessage response;
        try
        {
            response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            owned?.Dispose();
            logger.LogWarning(ex, "HTTP copy failed for {Uri}", source.DisplayUri);
            throw new InvalidOperationException("% Unable to retrieve remote file over HTTP(S).", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var code = (int)response.StatusCode;
            response.Dispose();
            owned?.Dispose();
            throw new InvalidOperationException($"% Remote HTTP(S) server returned {code}.");
        }

        var length = response.Content.Headers.ContentLength;
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return new HttpCopyFileSource(source.DisplayUri, length, response, stream, owned);
    }

    /// <summary>
    /// Builds a request URI without embedding username/password in the authority.
    /// </summary>
    /// <param name="source">Parsed HTTP(S) source.</param>
    /// <returns>Absolute request URI.</returns>
    private static Uri BuildRequestUri(CopySourceUri source)
    {
        var builder = new UriBuilder
        {
            Scheme = source.Kind == CopySourceKind.Https ? "https" : "http",
            Host = source.Host!,
            Path = source.Path ?? "/"
        };
        if (source.Port is { } port)
        {
            builder.Port = port;
        }

        return builder.Uri;
    }

    /// <summary>
    /// Creates the shared production <see cref="HttpClient"/> (no redirects, no decompression).
    /// </summary>
    /// <returns>Configured shared client with a 10-minute timeout.</returns>
    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _response.Dispose();
        _ownedClient?.Dispose();
    }
}
