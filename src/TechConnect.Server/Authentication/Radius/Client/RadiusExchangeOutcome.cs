// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusExchangeOutcome.cs
//
// Purpose:
// Enumerates semantic outcomes of a single RADIUS request/response exchange for
// failover and fallback decisions.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Client;

/// <summary>
/// Semantic outcome of a single RADIUS request/response exchange.
/// </summary>
/// <remarks>
/// Distinctions are preserved for later primary/secondary failover and local fallback.
/// </remarks>
internal enum RadiusExchangeOutcome
{
    /// <summary>Validated Access-Accept.</summary>
    Accept = 0,

    /// <summary>Validated Access-Reject.</summary>
    Reject = 1,

    /// <summary>No valid response: timeout or genuine transport failure.</summary>
    Unavailable = 2,

    /// <summary>A datagram was received but failed structural, endpoint, or cryptographic validation.</summary>
    InvalidResponse = 3,

    /// <summary>Caller cancellation or client disposal aborted the exchange.</summary>
    Cancelled = 4
}