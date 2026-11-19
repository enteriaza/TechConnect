// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteTransportException.cs
//
// Purpose:
// Base exception for remote-console transport failures (connect, TLS, framing
// send path) that are not permanent configuration or transient unavailability.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Transport;

/// <summary>
/// Base exception for remote-console transport failures.
/// </summary>
/// <remarks>
/// Prefer more specific types when applicable:
/// <see cref="RemoteTransportConfigurationException"/> for permanent misconfiguration,
/// <see cref="RemoteTransportUnavailableException"/> for expected disconnect / not-yet-ready states.
/// </remarks>
public class RemoteTransportException : Exception
{
    /// <summary>
    /// Initializes a new instance with the specified message.
    /// </summary>
    /// <param name="message">Human-readable failure description (must not include secrets).</param>
    public RemoteTransportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception.
    /// </summary>
    /// <param name="message">Human-readable failure description (must not include secrets).</param>
    /// <param name="innerException">Underlying failure, when available.</param>
    public RemoteTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
