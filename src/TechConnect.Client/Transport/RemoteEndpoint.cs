// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteEndpoint.cs
//
// Purpose:
// Validates and resolves remote-service endpoint settings for production TCP/mTLS
// (Host/Port/TlsHostName).
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Configuration;

namespace TechConnect.Client.Transport;

/// <summary>
/// Helpers for validating and resolving <see cref="RemoteServiceOptions"/> endpoints.
/// </summary>
/// <remarks>
/// Production connectivity uses <c>Host</c>/<c>Port</c>/<c>TlsHostName</c> with
/// <see cref="TcpMtlsRemoteConsoleTransport"/>.
/// </remarks>
internal static class RemoteEndpoint
{
    /// <summary>
    /// Returns whether the options describe a usable remote endpoint.
    /// </summary>
    /// <param name="options">Bound remote-service options.</param>
    /// <returns>
    /// <see langword="true"/> when a non-placeholder <c>Host</c> and valid port are set.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public static bool IsConfigured(RemoteServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return false;
        }

        if (options.Port is < 1 or > 65535)
        {
            return false;
        }

        return !RemoteServiceOptionsValidator.IsPlaceholderHost(options.Host);
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
}
