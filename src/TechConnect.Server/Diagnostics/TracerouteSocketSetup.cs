// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    TracerouteSocketSetup.cs
//
// Purpose:
// Builds ICMP bind endpoints and binds unbound raw traceroute sockets to an
// explicit source or Any / IPv6Any.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Bind endpoint helpers for the traceroute ICMP send/receive socket.
/// </summary>
/// <remarks>
/// The caller owns the <see cref="Socket"/> lifetime. This type does not open
/// or dispose sockets; it only constructs endpoints and performs <see cref="Socket.Bind"/>.
/// </remarks>
internal static class TracerouteSocketSetup
{
    /// <summary>
    /// Creates an ICMP bind endpoint for the given address family.
    /// </summary>
    /// <param name="family">
    /// <see cref="AddressFamily.InterNetwork"/> or <see cref="AddressFamily.InterNetworkV6"/>.
    /// </param>
    /// <param name="source">
    /// Explicit local source when requested; otherwise <see cref="IPAddress.Any"/> /
    /// <see cref="IPAddress.IPv6Any"/>. Must match <paramref name="family"/> when set.
    /// </param>
    /// <returns>An <see cref="IPEndPoint"/> with port 0 for ephemeral bind.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> address family does not match <paramref name="family"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="family"/> is unsupported.</exception>
    public static IPEndPoint CreateIcmpBindEndpoint(AddressFamily family, IPAddress? source)
    {
        if (family == AddressFamily.InterNetworkV6)
        {
            if (source is not null && source.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw new ArgumentException("Source address must be IPv6.", nameof(source));
            }

            return new IPEndPoint(source ?? IPAddress.IPv6Any, 0);
        }

        if (family != AddressFamily.InterNetwork)
        {
            throw new ArgumentOutOfRangeException(nameof(family), family, "Unsupported address family.");
        }

        if (source is not null && source.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Source address must be IPv4.", nameof(source));
        }

        return new IPEndPoint(source ?? IPAddress.Any, 0);
    }

    /// <summary>
    /// Binds an unbound ICMP socket to <paramref name="endpoint"/>.
    /// </summary>
    /// <param name="icmp">Raw ICMP / ICMPv6 socket that must not already be bound.</param>
    /// <param name="endpoint">Local endpoint from <see cref="CreateIcmpBindEndpoint"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="icmp"/> or <paramref name="endpoint"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The socket was already bound, or bind completed without setting <see cref="Socket.IsBound"/>.
    /// </exception>
    /// <exception cref="SocketException">The underlying bind failed.</exception>
    public static void BindIcmpSocket(Socket icmp, IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(icmp);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (icmp.IsBound)
        {
            throw new InvalidOperationException("Traceroute ICMP socket must not be bound before setup.");
        }

        icmp.Bind(endpoint);

        if (!icmp.IsBound)
        {
            throw new InvalidOperationException("Traceroute ICMP socket bind did not complete.");
        }
    }
}
