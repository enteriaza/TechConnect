// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    EnablePrivilegeService.cs
//
// Purpose:
// Performs enable re-authentication and privilege authorization for local
// enable secrets and RADIUS Access-Accept Cisco-AVPair privilege.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication;

/// <summary>
/// Enable authentication and privilege authorization.
/// </summary>
/// <remarks>
/// Local: privilege-scoped enable secrets. RADIUS: fresh Access-Accept Cisco-AVPair is the authority;
/// requested privilege must be ≤ returned <c>shell:priv-lvl</c>.
/// Does not mutate session privilege; callers apply <see cref="EnablePrivilegeResult.Privilege"/>
/// on success. Concurrent calls are safe with respect to this type's fields (immutable options snapshot).
/// </remarks>
internal sealed class EnablePrivilegeService
{
    /// <summary>Local authentication options including enable secret hashes.</summary>
    private readonly LocalAuthenticationOptions _localOptions;

    /// <summary>Hasher used to verify local enable secrets.</summary>
    private readonly OperatorPasswordHasher _passwordHasher;

    /// <summary>RADIUS provider used for enable re-authentication when the identity is RADIUS-backed.</summary>
    private readonly RadiusPasswordAuthenticationProvider _radius;

    /// <summary>Audit writer for enable attempt/success/failure events.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Logger for enable outcomes (no secrets).</summary>
    private readonly ILogger<EnablePrivilegeService> _logger;

    /// <summary>
    /// Creates the enable privilege service from server options and dependencies.
    /// </summary>
    /// <param name="options">Server options supplying local enable-secret configuration.</param>
    /// <param name="passwordHasher">Password hasher for enable-secret verification.</param>
    /// <param name="radius">RADIUS enable authentication provider.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="audit">Optional audit writer; defaults to no-op.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public EnablePrivilegeService(
        IOptions<TechConnectServerOptions> options,
        OperatorPasswordHasher passwordHasher,
        RadiusPasswordAuthenticationProvider radius,
        ILogger<EnablePrivilegeService> logger,
        IAuditEventWriter? audit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _localOptions = options.Value.Authentication?.Local ?? new LocalAuthenticationOptions();
        _passwordHasher = passwordHasher ?? throw new ArgumentNullException(nameof(passwordHasher));
        _radius = radius ?? throw new ArgumentNullException(nameof(radius));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? new NoOpAuditEventWriter();
    }

    /// <summary>
    /// Re-authenticates the operator for an enable privilege change.
    /// </summary>
    /// <param name="identity">Already-authenticated operator whose provider selects the enable path.</param>
    /// <param name="secret">Enable secret or RADIUS password; never logged.</param>
    /// <param name="requestedLevel">Privilege level the operator is requesting.</param>
    /// <param name="cancellationToken">Cancellation for the RADIUS enable path.</param>
    /// <returns>
    /// Structured success or failure; on insufficient privilege,
    /// <see cref="EnableAuthFailureKind.InsufficientPrivilege"/> includes the authorized maximum.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
    public async Task<EnablePrivilegeResult> AuthenticateEnableAsync(
        AuthenticatedOperator identity,
        string? secret,
        PrivilegeLevel requestedLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!PrivilegeLevel.IsValid(requestedLevel.Value))
        {
            return EnablePrivilegeResult.Fail(EnableAuthFailureKind.Failed, "Invalid privilege level.");
        }

        _audit.TryWrite(
            AuditEventType.EnableAuthAttempted,
            AuditEventOutcome.Informational,
            "Enable authentication attempted",
            new AuditEventContext
            {
                ActorUsername = identity.Username,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["provider"] = identity.Provider.ToString(),
                    ["requestedLevel"] = requestedLevel.ToString()
                }
            });

        return identity.Provider switch
        {
            AuthenticationProviderKind.Local => AuthenticateLocal(identity, secret, requestedLevel),
            AuthenticationProviderKind.Radius => await AuthenticateRadiusAsync(
                    identity,
                    secret,
                    requestedLevel,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => FailClosed(identity, EnableAuthFailureKind.Unavailable, "Enable authentication unavailable")
        };
    }

    /// <summary>
    /// Verifies a local enable secret and authorizes <paramref name="requestedLevel"/>.
    /// </summary>
    /// <remarks>
    /// Fails closed when no secrets are configured, the secret is empty/mismatched,
    /// or the same secret hash matches multiple privilege levels.
    /// </remarks>
    private EnablePrivilegeResult AuthenticateLocal(
        AuthenticatedOperator identity,
        string? secret,
        PrivilegeLevel requestedLevel)
    {
        var secrets = CollectLocalEnableSecrets();
        if (secrets.Count == 0)
        {
            _logger.LogInformation(
                "Local enable unavailable for user {Username}: no enable secret configured",
                identity.Username);
            return FailClosed(identity, EnableAuthFailureKind.Unavailable, "Local enable secret is not configured");
        }

        if (string.IsNullOrEmpty(secret))
        {
            return RecordLocalAuthFailure(identity);
        }

        PrivilegeLevel? matchedAuthorized = null;
        foreach (var (authorizedMax, hash) in secrets)
        {
            if (!_passwordHasher.VerifyEnableSecret(hash, secret))
            {
                continue;
            }

            if (matchedAuthorized is not null && matchedAuthorized.Value != authorizedMax)
            {
                _logger.LogWarning(
                    "Local enable secret matched multiple privilege levels for user {Username}; failing closed",
                    identity.Username);
                _audit.TryWrite(
                    AuditEventType.EnableAuthFailed,
                    AuditEventOutcome.Failure,
                    "Local enable secret matched multiple privilege levels",
                    new AuditEventContext { ActorUsername = identity.Username });
                return EnablePrivilegeResult.Fail(
                    EnableAuthFailureKind.Failed,
                    "Enable authentication failed.");
            }

            matchedAuthorized = authorizedMax;
        }

        if (matchedAuthorized is null)
        {
            return RecordLocalAuthFailure(identity);
        }

        var authorized = matchedAuthorized.Value;
        if (requestedLevel > authorized)
        {
            return RecordInsufficientPrivilege(identity, requestedLevel, authorized, "local");
        }

        _logger.LogInformation(
            "Local enable authentication succeeded for user {Username} requested={Requested} authorized={Authorized}",
            identity.Username,
            requestedLevel,
            authorized);
        RecordSuccess(identity, requestedLevel, authorized, "local");
        return EnablePrivilegeResult.Success(requestedLevel, authorized);
    }

    /// <summary>
    /// Performs RADIUS enable re-auth and ensures <paramref name="requestedLevel"/> ≤ authorized privilege.
    /// </summary>
    private async Task<EnablePrivilegeResult> AuthenticateRadiusAsync(
        AuthenticatedOperator identity,
        string? secret,
        PrivilegeLevel requestedLevel,
        CancellationToken cancellationToken)
    {
        if (!_radius.Enabled)
        {
            _logger.LogInformation(
                "RADIUS enable unavailable for user {Username}: RADIUS is not enabled",
                identity.Username);
            return FailClosed(identity, EnableAuthFailureKind.Unavailable, "RADIUS enable authentication unavailable");
        }

        var radiusResult = await _radius
            .AuthenticateEnableAsync(identity.Username, secret, cancellationToken)
            .ConfigureAwait(false);

        if (!radiusResult.Succeeded)
        {
            var kind = radiusResult.FailureKind ?? EnableAuthFailureKind.Failed;
            if (kind == EnableAuthFailureKind.Unavailable)
            {
                return FailClosed(identity, kind, "RADIUS enable authentication unavailable");
            }

            if (kind == EnableAuthFailureKind.Cancelled)
            {
                return EnablePrivilegeResult.Fail(kind, "Enable authentication cancelled.");
            }

            _logger.LogInformation(
                "RADIUS enable authentication failed for user {Username} kind={Kind}",
                identity.Username,
                kind);
            _audit.TryWrite(
                AuditEventType.EnableAuthFailed,
                kind == EnableAuthFailureKind.Rejected ? AuditEventOutcome.Denied : AuditEventOutcome.Failure,
                "RADIUS enable authentication failed",
                new AuditEventContext
                {
                    ActorUsername = identity.Username,
                    Details = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["kind"] = kind.ToString()
                    }
                });
            return EnablePrivilegeResult.Fail(kind, "Enable authentication failed.");
        }

        var authorized = radiusResult.Privilege;
        if (requestedLevel > authorized)
        {
            return RecordInsufficientPrivilege(identity, requestedLevel, authorized, "radius");
        }

        _logger.LogInformation(
            "RADIUS enable authentication succeeded for user {Username} requested={Requested} authorized={Authorized}",
            identity.Username,
            requestedLevel,
            authorized);
        RecordSuccess(identity, requestedLevel, authorized, "radius");
        return EnablePrivilegeResult.Success(requestedLevel, authorized);
    }

    /// <summary>
    /// Collects configured enable secret hashes with their authorized maximum privilege levels.
    /// </summary>
    /// <returns>
    /// Entries from legacy <c>EnableSecretHash</c> (level 15) plus <c>EnableSecrets</c> list entries
    /// with parseable privilege and non-empty hashes.
    /// </returns>
    private List<(PrivilegeLevel AuthorizedMax, string Hash)> CollectLocalEnableSecrets()
    {
        var list = new List<(PrivilegeLevel, string)>();

        if (!string.IsNullOrWhiteSpace(_localOptions.EnableSecretHash))
        {
            list.Add((PrivilegeLevel.FromInt(15), _localOptions.EnableSecretHash.Trim()));
        }

        foreach (var entry in _localOptions.EnableSecrets ?? [])
        {
            if (entry is null ||
                !PrivilegeLevel.TryParse(entry.PrivilegeLevel, out var level, out _) ||
                string.IsNullOrWhiteSpace(entry.SecretHash))
            {
                continue;
            }

            list.Add((level, entry.SecretHash.Trim()));
        }

        return list;
    }

    /// <summary>
    /// Logs and audits a local enable authentication failure, returning a Failed result.
    /// </summary>
    private EnablePrivilegeResult RecordLocalAuthFailure(AuthenticatedOperator identity)
    {
        _logger.LogInformation("Local enable authentication failed for user {Username}", identity.Username);
        _audit.TryWrite(
            AuditEventType.EnableAuthFailed,
            AuditEventOutcome.Failure,
            "Local enable authentication failed",
            new AuditEventContext { ActorUsername = identity.Username });
        return EnablePrivilegeResult.Fail(EnableAuthFailureKind.Failed, "Enable authentication failed.");
    }

    /// <summary>
    /// Logs and audits an insufficient-privilege enable denial with an operator-facing message.
    /// </summary>
    private EnablePrivilegeResult RecordInsufficientPrivilege(
        AuthenticatedOperator identity,
        PrivilegeLevel requested,
        PrivilegeLevel authorized,
        string provider)
    {
        var message =
            $"% Authorization failed: requested privilege level {requested.Value} " +
            $"exceeds {(provider == "radius" ? "RADIUS" : "enable-secret")}-authorized level {authorized.Value}.";

        _logger.LogInformation(
            "Enable authorization denied for user {Username} requested={Requested} authorized={Authorized} provider={Provider}",
            identity.Username,
            requested,
            authorized,
            provider);

        _audit.TryWrite(
            AuditEventType.EnableAuthorizationDenied,
            AuditEventOutcome.Denied,
            "Enable authorization denied: requested privilege exceeds authorized privilege",
            new AuditEventContext
            {
                ActorUsername = identity.Username,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["provider"] = provider,
                    ["requestedLevel"] = requested.ToString(),
                    ["authorizedLevel"] = authorized.ToString()
                }
            });

        return EnablePrivilegeResult.Fail(
            EnableAuthFailureKind.InsufficientPrivilege,
            message,
            authorized);
    }

    /// <summary>
    /// Audits enable-unavailable and returns a failed result without attempting further auth.
    /// </summary>
    private EnablePrivilegeResult FailClosed(
        AuthenticatedOperator identity,
        EnableAuthFailureKind kind,
        string description)
    {
        _audit.TryWrite(
            AuditEventType.EnableAuthUnavailable,
            AuditEventOutcome.Failure,
            description,
            new AuditEventContext { ActorUsername = identity.Username });
        return EnablePrivilegeResult.Fail(kind, description);
    }

    /// <summary>
    /// Writes an enable-success audit event with requested/authorized levels and provider.
    /// </summary>
    private void RecordSuccess(
        AuthenticatedOperator identity,
        PrivilegeLevel requested,
        PrivilegeLevel authorized,
        string provider)
    {
        _audit.TryWrite(
            AuditEventType.EnableAuthSucceeded,
            AuditEventOutcome.Success,
            "Enable authentication succeeded",
            new AuditEventContext
            {
                ActorUsername = identity.Username,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["provider"] = provider,
                    ["requestedLevel"] = requested.Value.ToString(CultureInfo.InvariantCulture),
                    ["authorizedLevel"] = authorized.Value.ToString(CultureInfo.InvariantCulture)
                }
            });
    }
}
