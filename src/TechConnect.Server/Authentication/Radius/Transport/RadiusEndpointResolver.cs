// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusEndpointResolver.cs
//
// Purpose:
// Resolves a RADIUS server host or IP literal into an IPEndPoint for UDP
// authentication traffic, preferring IPv4 when DNS returns both families.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Authentication.Radius.Transport;

/// <summary>
/// Resolves RADIUS server addresses for UDP client construction.
/// </summary>
/// <remarks>
/// IP literals bypass DNS. DNS failures surface as
/// <see cref="RadiusTransportException"/>. Cancellation is honored on
/// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.
/// </remarks>
internal static class RadiusEndpointResolver
{
    /// <summary>
    /// Default RADIUS authentication UDP port (1812).
    /// </summary>
    public const int DefaultAuthenticationPort = 1812;

    /// <summary>
    /// Resolves <paramref name="hostOrAddress"/> to an <see cref="IPEndPoint"/>.
    /// </summary>
    /// <param name="hostOrAddress">Hostname or IP literal.</param>
    /// <param name="port">UDP port in 1..65535.</param>
    /// <param name="cancellationToken">Token that can cancel DNS resolution.</param>
    /// <returns>Endpoint using a parsed IP or a preferred DNS address.</returns>
    /// <exception cref="ArgumentException"><paramref name="hostOrAddress"/> is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is outside 1..65535.</exception>
    /// <exception cref="RadiusTransportException">DNS resolution failed or returned no addresses.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<IPEndPoint> ResolveAsync(
        string hostOrAddress,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostOrAddress);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        if (IPAddress.TryParse(hostOrAddress, out var parsed))
        {
            return new IPEndPoint(parsed, port);
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(hostOrAddress, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new RadiusTransportException(
                $"Failed to resolve RADIUS host '{hostOrAddress}'.",
                ex);
        }

        if (addresses.Length == 0)
        {
            throw new RadiusTransportException($"RADIUS host '{hostOrAddress}' resolved to no addresses.");
        }

        // Prefer IPv4 when both families are present for broader loopback/test compatibility;
        // otherwise use the first address returned by DNS.
        var selected = addresses.FirstOrDefault(static a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addresses[0];
        return new IPEndPoint(selected, port);
    }
}
