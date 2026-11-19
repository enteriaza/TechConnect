// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuthenticationProviderKind.cs
//
// Purpose:
// Enumerates which authentication backend established an operator identity
// (local account store versus RADIUS).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Models;

/// <summary>
/// Identifies the authentication backend that produced an
/// <see cref="AuthenticatedOperator"/>.
/// </summary>
internal enum AuthenticationProviderKind
{
    /// <summary>
    /// Identity came from the configured local operator account store
    /// (password and/or public key).
    /// </summary>
    Local = 0,

    /// <summary>
    /// Identity came from a successful RADIUS Access-Accept exchange.
    /// </summary>
    Radius = 1
}
