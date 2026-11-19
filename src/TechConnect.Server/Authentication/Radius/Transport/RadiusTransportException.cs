// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusTransportException.cs
//
// Purpose:
// Exception type for RADIUS endpoint resolution and UDP transport failures,
// distinct from protocol InvalidResponse exchange outcomes.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Transport;

/// <summary>
/// Transport-level failure resolving endpoints or operating the UDP socket.
/// </summary>
/// <remarks>
/// Distinct from RADIUS protocol InvalidResponse exchange outcomes.
/// Callers typically map this to unavailable authentication rather than
/// treating it as a validated Access-Reject.
/// </remarks>
internal sealed class RadiusTransportException : Exception
{
    /// <summary>
    /// Creates an exception with a transport failure message.
    /// </summary>
    /// <param name="message">Description of the transport failure.</param>
    public RadiusTransportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception with a message and inner cause.
    /// </summary>
    /// <param name="message">Description of the transport failure.</param>
    /// <param name="innerException">Underlying socket or DNS failure.</param>
    public RadiusTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
