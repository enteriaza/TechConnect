// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuthorizationDecision.cs
//
// Purpose:
// Result types for CLI authorization decisions (allow/deny with optional
// command id and reason text).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authorization;

/// <summary>
/// Whether an authorization check allowed or denied the requested command.
/// </summary>
internal enum AuthorizationOutcome
{
    /// <summary>The command is authorized for the operator's privilege.</summary>
    Allowed = 0,

    /// <summary>The command is not authorized; see <see cref="AuthorizationDecision.Reason"/>.</summary>
    Denied = 1
}

/// <summary>
/// Structured authorization result for a parsed CLI command.
/// </summary>
/// <param name="Outcome">Allow or deny.</param>
/// <param name="Command">Command id when known; may be <see langword="null"/> on early denial.</param>
/// <param name="Reason">Optional human-readable denial or diagnostic reason.</param>
internal sealed record AuthorizationDecision(
    AuthorizationOutcome Outcome,
    CliCommandId? Command,
    string? Reason);
