// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorAuthorizationService.cs
//
// Purpose:
// Authorizes parsed CLI commands against an authenticated operator's privilege.
//
// -----------------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Models;

namespace TechConnect.Server.Authorization;

/// <summary>
/// Central authorization gate for CLI commands and terminal operations.
/// </summary>
/// <remarks>
/// Uses the immutable <see cref="AuthenticatedOperator"/> only — never usernames or client claims alone.
/// Denials are audited. Concurrent calls are safe with respect to this type's fields.
/// </remarks>
internal sealed class OperatorAuthorizationService
{
    /// <summary>
    /// Operator-facing message used by CLI paths when authorization is denied.
    /// </summary>
    public const string PermissionDeniedMessage = "Permission denied.";

    /// <summary>
    /// Logger for allow/deny decisions (no secrets).
    /// </summary>
    private readonly ILogger<OperatorAuthorizationService> _logger;

    /// <summary>
    /// Audit writer for denial events.
    /// </summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>
    /// Creates the authorization service.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="audit">Optional audit writer; defaults to no-op.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public OperatorAuthorizationService(
        ILogger<OperatorAuthorizationService> logger,
        IAuditEventWriter? audit = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? new NoOpAuditEventWriter();
    }

    /// <summary>
    /// Authorizes <paramref name="command"/> for <paramref name="identity"/> using <see cref="CliCommandPolicy"/>.
    /// </summary>
    /// <param name="identity">Authenticated operator; <see langword="null"/> is always denied.</param>
    /// <param name="command">Command to authorize.</param>
    /// <returns>Allow or deny decision with reason on denial.</returns>
    public AuthorizationDecision Authorize(AuthenticatedOperator? identity, CliCommandId command)
    {
        if (identity is null)
        {
            _logger.LogWarning(
                "Authorization denied: missing authenticated identity command={Command}",
                CliCommandPolicy.ToAuditName(command));
            RecordDenial(null, command, "missing identity");
            return new AuthorizationDecision(AuthorizationOutcome.Denied, command, "missing identity");
        }

        if (!PrivilegeLevel.IsValid(identity.Privilege.Value))
        {
            _logger.LogWarning(
                "Authorization denied: unknown privilege operator={Username} privilege={Privilege} command={Command} session={SessionId}",
                identity.Username,
                identity.Privilege,
                CliCommandPolicy.ToAuditName(command),
                identity.OperatorSessionId);
            RecordDenial(identity, command, "unknown privilege");
            return new AuthorizationDecision(AuthorizationOutcome.Denied, command, "unknown privilege");
        }

        if (!CliCommandPolicy.IsDefined(command))
        {
            _logger.LogWarning(
                "Authorization denied: command has no policy operator={Username} command={Command} session={SessionId}",
                identity.Username,
                command,
                identity.OperatorSessionId);
            RecordDenial(identity, command, "no policy");
            return new AuthorizationDecision(AuthorizationOutcome.Denied, command, "no policy");
        }

        if (!CliCommandPolicy.IsAllowed(identity.Privilege, command))
        {
            _logger.LogInformation(
                "Authorization denied operator={Username} provider={Provider} method={Method} privilege={Privilege} command={Command} session={SessionId}",
                identity.Username,
                identity.Provider,
                identity.Method,
                identity.Privilege,
                CliCommandPolicy.ToAuditName(command),
                identity.OperatorSessionId);
            RecordDenial(identity, command, "insufficient privilege");
            return new AuthorizationDecision(AuthorizationOutcome.Denied, command, "insufficient privilege");
        }

        _logger.LogDebug(
            "Authorization allowed operator={Username} provider={Provider} method={Method} privilege={Privilege} command={Command} session={SessionId}",
            identity.Username,
            identity.Provider,
            identity.Method,
            identity.Privilege,
            CliCommandPolicy.ToAuditName(command),
            identity.OperatorSessionId);

        return new AuthorizationDecision(AuthorizationOutcome.Allowed, command, null);
    }

    /// <summary>
    /// Writes an authorization-denied audit event for <paramref name="command"/>.
    /// </summary>
    private void RecordDenial(AuthenticatedOperator? identity, CliCommandId command, string reason)
    {
        var commandName = CliCommandPolicy.ToAuditName(command);
        _audit.TryWrite(
            AuditEventType.AuthorizationDenied,
            AuditEventOutcome.Denied,
            $"Authorization denied for '{commandName}'",
            new AuditEventContext
            {
                ActorUsername = identity?.Username,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["command"] = commandName,
                    ["reason"] = reason
                }
            });
    }

    /// <summary>
    /// Convenience wrapper that returns whether <see cref="Authorize"/> allowed the command.
    /// </summary>
    /// <param name="identity">Authenticated operator; may be <see langword="null"/>.</param>
    /// <param name="command">Command to authorize.</param>
    /// <param name="decision">Full decision from <see cref="Authorize"/>.</param>
    /// <returns><see langword="true"/> when the outcome is <see cref="AuthorizationOutcome.Allowed"/>.</returns>
    public bool TryAuthorize(
        AuthenticatedOperator? identity,
        CliCommandId command,
        out AuthorizationDecision decision)
    {
        decision = Authorize(identity, command);
        return decision.Outcome == AuthorizationOutcome.Allowed;
    }
}
