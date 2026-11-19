// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorCliSessionRecord.cs
//
// Purpose:
// Mutable presence record for an active SSH CLI operator session used by
// show users / show terminal and administrative session listing.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Ssh.Sessions;

/// <summary>
/// Live SSH CLI operator presence for <c>show users</c> / <c>show terminal</c>.
/// </summary>
/// <remarks>
/// Registered with <see cref="SshOperatorSessionTracker"/> when a shell CLI starts
/// and unregistered when it ends. <see cref="ActivePrivilege"/> and attachment /
/// terminal fields are updated as the session evolves.
/// </remarks>
internal sealed class OperatorCliSessionRecord
{
    /// <summary>
    /// Unique operator session identifier (matches authenticated operator session id).
    /// </summary>
    public required string OperatorSessionId { get; init; }

    /// <summary>
    /// Authenticated username.
    /// </summary>
    public required string Username { get; init; }

    /// <summary>
    /// Privilege level at login (immutable for the record lifetime).
    /// </summary>
    public required PrivilegeLevel LoginPrivilege { get; init; }

    /// <summary>
    /// Effective privilege after enable/disable.
    /// </summary>
    public PrivilegeLevel ActivePrivilege { get; set; }

    /// <summary>
    /// Formatted SSH remote endpoint when known.
    /// </summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>
    /// UTC time the CLI session was registered.
    /// </summary>
    public DateTimeOffset ConnectedAtUtc { get; init; }

    /// <summary>
    /// Connector session id currently attached, if any.
    /// </summary>
    public string? AttachedConnectorSessionId { get; set; }

    /// <summary>
    /// Whether the operator is currently in serial terminal mode (vs CLI prompt).
    /// </summary>
    public bool TerminalModeActive { get; set; }

    /// <summary>
    /// TERM type from PTY negotiation, when available.
    /// </summary>
    public string? TermType { get; set; }

    /// <summary>
    /// Current terminal columns.
    /// </summary>
    public int Columns { get; set; }

    /// <summary>
    /// Current terminal rows.
    /// </summary>
    public int Rows { get; set; }
}
