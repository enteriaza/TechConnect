// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteEndpoint.cs
//
// Purpose:
// Validates and resolves remote-service endpoint settings for production TCP/mTLS
// (Host/Port/TlsHostName) and legacy WSS URI helpers retained for test fixtures.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Configuration;

namespace TechConnect.Client.Transport;

/// <summary>
/// Helpers for validating and resolving <see cref="RemoteServiceOptions"/> endpoints.
/// </summary>
/// <remarks>
/// Production connectivity uses <c>Host</c>/<c>Port</c>/<c>TlsHostName</c> with
/// <see cref="TcpMtlsRemoteConsoleTransport"/>. The <see cref="CreateWebSocketUri"/> path
/// is retained only so existing WSS test fixtures keep compiling and running.
/// </remarks>
internal static class RemoteEndpoint
{
    /// <summary>
    /// Returns whether the options describe a usable remote endpoint.
    /// </summary>
    /// <param name="options">Bound remote-service options.</param>
    /// <returns>
    /// <see langword="true"/> when a non-placeholder <c>Host</c> and valid port are set,
    /// or when a legacy absolute HTTPS <c>BaseUrl</c> with a non-placeholder host is set.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static bool IsConfigured(RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.Host))
        {
            if (options.Port is < 1 or > 65535)
            {
                return false;
            }

            return !RemoteServiceOptionsValidator.IsPlaceholderHost(options.Host);
        }

        // Legacy BaseUrl path retained only so existing WSS test fixtures keep compiling/running.
        // Production connectivity uses Host/Port/TlsHostName via TcpMtlsRemoteConsoleTransport.
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !RemoteServiceOptionsValidator.IsPlaceholderHost(uri.Host);
    }

    /// <summary>
    /// Resolves the TLS target host name used during mutual TLS authentication.
    /// </summary>
    /// <param name="options">Bound remote-service options.</param>
    /// <returns>
    /// Trimmed <c>TlsHostName</c> when set; otherwise the trimmed <c>Host</c> value.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RemoteTransportConfigurationException">
    /// Neither <c>TlsHostName</c> nor <c>Host</c> is configured.
    /// </exception>
    public static string ResolveTlsHostName(RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.TlsHostName))
        {
            return options.TlsHostName.Trim();
        }

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            throw new RemoteTransportConfigurationException(
                "Remote service endpoint is not configured. Set RemoteService:Host when the central service is available.");
        }

        return options.Host.Trim();
    }

    /// <summary>
    /// Builds a <c>wss://</c> URI from the legacy HTTPS <c>BaseUrl</c> and optional path.
    /// </summary>
    /// <remarks>
    /// Retained only for existing test fixtures that still exercise the frozen
    /// <see cref="WebSocketRemoteConsoleTransport"/>. Production DI uses
    /// <see cref="TcpMtlsRemoteConsoleTransport"/>.
    /// </remarks>
    /// <param name="options">Bound remote-service options containing <c>BaseUrl</c>.</param>
    /// <returns>Absolute WebSocket Secure URI derived from HTTPS.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="RemoteTransportConfigurationException">
    /// <c>BaseUrl</c> is missing, not absolute HTTPS, or uses a placeholder host.
    /// </exception>
    public static Uri CreateWebSocketUri(RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new RemoteTransportConfigurationException(
                "Remote service endpoint is not configured. Set RemoteService:Host for TCP/mTLS (BaseUrl is obsolete).");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new RemoteTransportConfigurationException("RemoteService:BaseUrl is not a valid absolute URI.");
        }

        if (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new RemoteTransportConfigurationException("RemoteService:BaseUrl must use HTTPS (WSS is derived from HTTPS).");
        }

        if (RemoteServiceOptionsValidator.IsPlaceholderHost(baseUri.Host))
        {
            throw new RemoteTransportConfigurationException(
                "Remote service endpoint is a placeholder and will not be contacted.");
        }

        var builder = new UriBuilder(baseUri)
        {
            Scheme = "wss",
            Port = baseUri.IsDefaultPort ? -1 : baseUri.Port
        };

        if (!string.IsNullOrWhiteSpace(options.WebSocketPath))
        {
            builder.Path = options.WebSocketPath;
        }

        return builder.Uri;
    }
}
