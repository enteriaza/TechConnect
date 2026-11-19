// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuthenticationMethodKind.cs
//
// Purpose:
// Enumerates the SSH authentication method used to establish an operator
// identity (password versus public key).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Models;

/// <summary>
/// SSH authentication method that produced an <see cref="AuthenticatedOperator"/>.
/// </summary>
internal enum AuthenticationMethodKind
{
    /// <summary>
    /// Password authentication (local hash verification or RADIUS PAP).
    /// </summary>
    Password = 0,

    /// <summary>
    /// SSH public-key authentication against a local authorized-key entry.
    /// </summary>
    PublicKey = 1
}
