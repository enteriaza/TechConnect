// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteTransportConfigurationException.cs
//
// Purpose:
// Signals permanent remote transport misconfiguration so reconnect loops stop
// rather than retrying indefinitely.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Transport;

/// <summary>
/// Permanent remote transport misconfiguration (missing endpoint, certificate, etc.).
/// </summary>
/// <remarks>
/// Reconnect loops should stop rather than retry indefinitely when this is thrown.
/// </remarks>
public sealed class RemoteTransportConfigurationException : Exception
{
    /// <summary>
    /// Initializes a new instance with the specified message.
    /// </summary>
    /// <param name="message">Human-readable configuration failure description.</param>
    public RemoteTransportConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception.
    /// </summary>
    /// <param name="message">Human-readable configuration failure description.</param>
    /// <param name="innerException">Underlying failure, when available.</param>
    public RemoteTransportConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
