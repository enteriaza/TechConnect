// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteTransportUnavailableException.cs
//
// Purpose:
// Non-fatal signal that the remote transport cannot connect or is not open,
// used by reconnect coordination rather than as a technician-facing crash.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Client.Transport;

/// <summary>
/// Predictable, non-fatal signal that the remote transport cannot connect yet
/// or is not currently open.
/// </summary>
/// <remarks>
/// Used by the reconnect coordinator; not a technician-facing crash.
/// </remarks>
public sealed class RemoteTransportUnavailableException : Exception
{
    /// <summary>
    /// Initializes a new instance with the specified message.
    /// </summary>
    /// <param name="message">Human-readable unavailability description.</param>
    public RemoteTransportUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception.
    /// </summary>
    /// <param name="message">Human-readable unavailability description.</param>
    /// <param name="innerException">Underlying failure, when available.</param>
    public RemoteTransportUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
