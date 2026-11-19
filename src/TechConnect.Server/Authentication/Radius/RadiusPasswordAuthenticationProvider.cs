// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusPasswordAuthenticationProvider.cs
//
// Purpose:
// RADIUS password authentication with primary/secondary failover,
// optional local fallback, and enable re-authentication without local fallback.
//
// -----------------------------------------------------------------------------
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius.Client;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// RADIUS password authentication with primary/secondary failover and optional local fallback.
/// </summary>
/// <remarks>
/// Enable re-authentication (<see cref="AuthenticateEnableAsync"/>) never uses local fallback.
/// Password login may fall back to local verification only when every configured RADIUS server
/// is <see cref="Client.RadiusExchangeOutcome.Unavailable"/> and local fallback is enabled.
/// Cancellation returns <see langword="null"/> / cancelled enable results without throwing to callers
/// that already catch <see cref="OperationCanceledException"/>.
/// </remarks>
internal sealed class RadiusPasswordAuthenticationProvider
{
    /// <summary>
    /// Snapshot of RADIUS authentication options from configuration.
    /// </summary>
    private readonly RadiusAuthenticationOptions _options;

    /// <summary>
    /// Owned primary/secondary RADIUS client factory.
    /// </summary>
    private readonly RadiusClientFactory _clients;

    /// <summary>
    /// Maps Access-Accept Cisco-AVPair privilege into <see cref="PrivilegeLevel"/>.
    /// </summary>
    private readonly CiscoAvPairPrivilegeMapper _privilegeMapper;

    /// <summary>
    /// Local account store used only for optional password fallback.
    /// </summary>
    private readonly LocalOperatorAccountStore _localAccounts;

    /// <summary>
    /// Clock for authenticated-at timestamps.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Audit writer for RADIUS and fallback outcomes.
    /// </summary>
    private readonly IAuditEventWriter _audit;
    /// <summary>
    /// logger backing member.
    /// </summary>
    private readonly ILogger<RadiusPasswordAuthenticationProvider> _logger;

    /// <summary>
    /// Radius Password Authentication Provider.
    /// </summary>
    public RadiusPasswordAuthenticationProvider(
        IOptions<TechConnectServerOptions> options,
        RadiusClientFactory clients,
        CiscoAvPairPrivilegeMapper privilegeMapper,
        LocalOperatorAccountStore localAccounts,
        TimeProvider timeProvider,
        ILogger<RadiusPasswordAuthenticationProvider> logger,
        IAuditEventWriter? audit = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Authentication?.Radius ?? new RadiusAuthenticationOptions();
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _privilegeMapper = privilegeMapper ?? throw new ArgumentNullException(nameof(privilegeMapper));
        _localAccounts = localAccounts ?? throw new ArgumentNullException(nameof(localAccounts));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? new NoOpAuditEventWriter();
    }

    /// <summary>
    /// True when RADIUS options are enabled and a primary UDP client is configured.
    /// </summary>
    public bool Enabled => _options.Enabled && _clients.Enabled && _clients.Primary is not null;

    /// <summary>
    /// Authenticates <paramref name="username"/>/<paramref name="password"/> via RADIUS when enabled;
    /// returns <see langword="null"/> when disabled, credentials are blank, or RADIUS rejects/fails closed.
    /// </summary>
    public async Task<AuthenticatedOperator?> AuthenticateAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var trimmedUser = username.Trim();
        var primary = _clients.Primary!;
        RadiusExchangeResult primaryResult;
        try
        {
            primaryResult = await ExchangeWithRetriesAsync(primary, trimmedUser, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Primary RADIUS authentication cancelled for user {Username}", trimmedUser);
            return null;
        }

        _logger.LogInformation(
            "Primary RADIUS result for user {Username} endpoint={Endpoint} outcome={Outcome}",
            trimmedUser,
            primary.EndPoint,
            primaryResult.Outcome);

        switch (primaryResult.Outcome)
        {
            case RadiusExchangeOutcome.Accept:
                return TryCreateRadiusIdentity(trimmedUser, primaryResult, "primary");

            case RadiusExchangeOutcome.Reject:
                RecordReject(trimmedUser);
                return null;

            case RadiusExchangeOutcome.InvalidResponse:
                RecordPasswordFailure(trimmedUser, "RADIUS response validation failed");
                return null;

            case RadiusExchangeOutcome.Cancelled:
                return null;

            case RadiusExchangeOutcome.Unavailable:
                break;

            default:
                RecordPasswordFailure(trimmedUser, "RADIUS authentication failed");
                return null;
        }

        // Primary Unavailable — secondary only when configured.
        if (_clients.Secondary is null)
        {
            return await TryLocalFallbackAsync(trimmedUser, password, "primary unavailable; no secondary")
                .ConfigureAwait(false);
        }

        var secondary = _clients.Secondary;
        RadiusExchangeResult secondaryResult;
        try
        {
            secondaryResult = await ExchangeWithRetriesAsync(secondary, trimmedUser, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Secondary RADIUS authentication cancelled for user {Username}", trimmedUser);
            return null;
        }

        _logger.LogInformation(
            "Secondary RADIUS result for user {Username} endpoint={Endpoint} outcome={Outcome}",
            trimmedUser,
            secondary.EndPoint,
            secondaryResult.Outcome);

        switch (secondaryResult.Outcome)
        {
            case RadiusExchangeOutcome.Accept:
                return TryCreateRadiusIdentity(trimmedUser, secondaryResult, "secondary");

            case RadiusExchangeOutcome.Reject:
                RecordReject(trimmedUser);
                return null;

            case RadiusExchangeOutcome.InvalidResponse:
                RecordPasswordFailure(trimmedUser, "RADIUS response validation failed");
                return null;

            case RadiusExchangeOutcome.Cancelled:
                return null;

            case RadiusExchangeOutcome.Unavailable:
                return await TryLocalFallbackAsync(trimmedUser, password, "primary and secondary unavailable")
                    .ConfigureAwait(false);

            default:
                RecordPasswordFailure(trimmedUser, "RADIUS authentication failed");
                return null;
        }
    }

    /// <summary>
    /// Enable re-authentication: RADIUS exchange + Cisco-AVPair privilege mapping only.
    /// No local fallback and no <see cref="AuthenticatedOperator"/> session creation.
    /// </summary>
    public async Task<RadiusEnableAuthResult> AuthenticateEnableAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!Enabled)
        {
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Unavailable);
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Failed);
        }

        var trimmedUser = username.Trim();
        var primary = _clients.Primary!;
        RadiusExchangeResult primaryResult;
        try
        {
            primaryResult = await ExchangeWithRetriesAsync(primary, trimmedUser, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Primary RADIUS enable authentication cancelled for user {Username}", trimmedUser);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Cancelled);
        }

        _logger.LogInformation(
            "Primary RADIUS enable result for user {Username} endpoint={Endpoint} outcome={Outcome}",
            trimmedUser,
            primary.EndPoint,
            primaryResult.Outcome);

        var primaryMapped = TryMapEnablePrivilege(trimmedUser, primaryResult, "primary");
        if (primaryMapped is not null)
        {
            return primaryMapped.Value;
        }

        if (primaryResult.Outcome is RadiusExchangeOutcome.Reject
            or RadiusExchangeOutcome.InvalidResponse
            or RadiusExchangeOutcome.Cancelled)
        {
            return MapEnableFailure(primaryResult.Outcome);
        }

        if (primaryResult.Outcome != RadiusExchangeOutcome.Unavailable)
        {
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Failed);
        }

        // Primary Unavailable — secondary only when configured; never local fallback.
        if (_clients.Secondary is null)
        {
            _logger.LogInformation(
                "RADIUS enable unavailable for user {Username}: primary unavailable; no secondary",
                trimmedUser);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Unavailable);
        }

        var secondary = _clients.Secondary;
        RadiusExchangeResult secondaryResult;
        try
        {
            secondaryResult = await ExchangeWithRetriesAsync(secondary, trimmedUser, password, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Secondary RADIUS enable authentication cancelled for user {Username}",
                trimmedUser);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Cancelled);
        }

        _logger.LogInformation(
            "Secondary RADIUS enable result for user {Username} endpoint={Endpoint} outcome={Outcome}",
            trimmedUser,
            secondary.EndPoint,
            secondaryResult.Outcome);

        var secondaryMapped = TryMapEnablePrivilege(trimmedUser, secondaryResult, "secondary");
        if (secondaryMapped is not null)
        {
            return secondaryMapped.Value;
        }

        if (secondaryResult.Outcome == RadiusExchangeOutcome.Unavailable)
        {
            _logger.LogInformation(
                "RADIUS enable unavailable for user {Username}: primary and secondary unavailable",
                trimmedUser);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Unavailable);
        }

        return MapEnableFailure(secondaryResult.Outcome);
    }

    /// <summary>
    /// Attempts to Map Enable Privilege without throwing for ordinary failures.
    /// </summary>
    private RadiusEnableAuthResult? TryMapEnablePrivilege(
        string username,
        RadiusExchangeResult result,
        string serverRole)
    {
        if (result.Outcome != RadiusExchangeOutcome.Accept)
        {
            return null;
        }

        if (result.Packet is null)
        {
            _logger.LogWarning(
                "RADIUS enable Accept from {ServerRole} for user {Username} lacked a validated packet",
                serverRole,
                username);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Failed);
        }

        if (!_privilegeMapper.TryMap(result.Packet, out var privilege, out var failureReason))
        {
            _logger.LogWarning(
                "RADIUS enable privilege mapping failed for user {Username} server={ServerRole}: {Reason}",
                username,
                serverRole,
                failureReason);
            return RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Failed);
        }

        _logger.LogInformation(
            "RADIUS enable authentication succeeded for user {Username} server={ServerRole} privilege={Privilege}",
            username,
            serverRole,
            privilege);

        return RadiusEnableAuthResult.Ok(privilege);
    }

    /// <summary>
    /// Maps Enable Failure.
    /// </summary>
    private static RadiusEnableAuthResult MapEnableFailure(RadiusExchangeOutcome outcome) =>
        outcome switch
        {
            RadiusExchangeOutcome.Reject => RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Rejected),
            RadiusExchangeOutcome.Cancelled => RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Cancelled),
            RadiusExchangeOutcome.Unavailable => RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Unavailable),
            _ => RadiusEnableAuthResult.Fail(EnableAuthFailureKind.Failed)
        };

    /// <summary>
    /// Performs a RADIUS request/response exchange with validation and timeout.
    /// </summary>
    private async Task<RadiusExchangeResult> ExchangeWithRetriesAsync(
        RadiusServerClientHandle server,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(100, _options.TimeoutMilliseconds));
        var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 5);
        RadiusExchangeResult? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation(
                "{Role} RADIUS request for user {Username} endpoint={Endpoint} attempt={Attempt}/{MaxAttempts}",
                server.Role,
                username,
                server.EndPoint,
                attempt,
                maxAttempts);

            var stopwatch = Stopwatch.StartNew();
            var (packet, requestAuthenticator) = RadiusAccessRequestFactory.Create(
                username,
                password,
                _options,
                server.SharedSecret.Span);

            try
            {
                var result = await server.Client.ExchangeAsync(
                        packet,
                        requestAuthenticator,
                        server.SharedSecret,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);

                stopwatch.Stop();
                _logger.LogDebug(
                    "{Role} RADIUS exchange attempt {Attempt} outcome={Outcome} durationMs={DurationMs}",
                    server.Role,
                    attempt,
                    result.Outcome,
                    stopwatch.ElapsedMilliseconds);

                // Retry only genuine Unavailable. Never retry Reject/InvalidResponse/Cancelled/Accept.
                if (result.Outcome != RadiusExchangeOutcome.Unavailable || attempt == maxAttempts)
                {
                    return result;
                }

                last = result;
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(packet);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(requestAuthenticator);
            }
        }

        return last ?? RadiusExchangeResult.Unavailable("No RADIUS attempts completed.");
    }

    /// <summary>
    /// Attempts to Create Radius Identity without throwing for ordinary failures.
    /// </summary>
    private AuthenticatedOperator? TryCreateRadiusIdentity(
        string username,
        RadiusExchangeResult result,
        string serverRole)
    {
        if (result.Packet is null)
        {
            _logger.LogWarning(
                "RADIUS Accept from {ServerRole} for user {Username} lacked a validated packet",
                serverRole,
                username);
            RecordPasswordFailure(username, "RADIUS Accept could not be completed");
            return null;
        }

        if (!_privilegeMapper.TryMap(result.Packet, out var privilege, out var failureReason))
        {
            _logger.LogWarning(
                "RADIUS privilege mapping failed for user {Username} server={ServerRole}: {Reason}",
                username,
                serverRole,
                failureReason);
            // Do not expose mapping internals to audit/CLI readers.
            RecordPasswordFailure(username, "RADIUS privilege mapping failed");
            return null;
        }

        var identity = new AuthenticatedOperator(
            Username: username,
            Provider: AuthenticationProviderKind.Radius,
            Method: AuthenticationMethodKind.Password,
            Privilege: privilege,
            AuthenticatedAtUtc: _timeProvider.GetUtcNow(),
            OperatorSessionId: Guid.NewGuid().ToString("N"),
            LocalAccountId: null,
            PublicKeyFingerprint: null);

        _logger.LogInformation(
            "RADIUS authentication succeeded for user {Username} server={ServerRole} privilege={Privilege} session={SessionId}",
            identity.Username,
            serverRole,
            identity.Privilege,
            identity.OperatorSessionId);

        _audit.TryWrite(
            AuditEventType.RadiusAuthSuccess,
            AuditEventOutcome.Success,
            "RADIUS authentication succeeded",
            new AuditEventContext
            {
                ActorUsername = identity.Username,
                RequestedUsername = username,
                Details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["server"] = serverRole
                }
            });

        return identity;
    }

    /// <summary>
    /// Attempts to Local Fallback Async without throwing for ordinary failures.
    /// </summary>
    private Task<AuthenticatedOperator?> TryLocalFallbackAsync(
        string username,
        string password,
        string reason)
    {
        if (!_options.LocalFallbackEnabled)
        {
            _logger.LogInformation(
                "RADIUS servers unavailable ({Reason}); local fallback disabled for user {Username}",
                reason,
                username);
            _audit.TryWrite(
                AuditEventType.RadiusUnavailable,
                AuditEventOutcome.Failure,
                "RADIUS unavailable; authentication failed",
                new AuditEventContext { RequestedUsername = username });
            return Task.FromResult<AuthenticatedOperator?>(null);
        }

        _logger.LogInformation(
            "RADIUS servers unavailable ({Reason}); attempting local password fallback for user {Username}",
            reason,
            username);

        _audit.TryWrite(
            AuditEventType.RadiusUnavailable,
            AuditEventOutcome.Informational,
            "RADIUS unavailable; attempting local fallback",
            new AuditEventContext { RequestedUsername = username });

        if (!_localAccounts.TryVerifyPassword(username, password, out var account) || account is null)
        {
            _logger.LogInformation("Local password fallback failed for user {Username}", username);
            _audit.TryWrite(
                AuditEventType.LocalFallbackFailure,
                AuditEventOutcome.Failure,
                "Local password fallback failed",
                new AuditEventContext { RequestedUsername = username });
            return Task.FromResult<AuthenticatedOperator?>(null);
        }

        var identity = new AuthenticatedOperator(
            Username: account.Username,
            Provider: AuthenticationProviderKind.Local,
            Method: AuthenticationMethodKind.Password,
            Privilege: account.Privilege,
            AuthenticatedAtUtc: _timeProvider.GetUtcNow(),
            OperatorSessionId: Guid.NewGuid().ToString("N"),
            LocalAccountId: account.AccountId,
            PublicKeyFingerprint: null);

        _logger.LogInformation(
            "Local password fallback succeeded for user {Username} privilege={Privilege} session={SessionId}",
            identity.Username,
            identity.Privilege,
            identity.OperatorSessionId);

        _audit.TryWrite(
            AuditEventType.LocalFallbackSuccess,
            AuditEventOutcome.Success,
            "Local password fallback succeeded",
            new AuditEventContext
            {
                ActorUsername = identity.Username,
                RequestedUsername = username
            });

        return Task.FromResult<AuthenticatedOperator?>(identity);
    }

    /// <summary>
    /// Records Reject for audit/logging.
    /// </summary>
    private void RecordReject(string username) =>
        _audit.TryWrite(
            AuditEventType.RadiusAuthRejected,
            AuditEventOutcome.Denied,
            "RADIUS Access-Reject",
            new AuditEventContext { RequestedUsername = username });

    /// <summary>
    /// Records Password Failure for audit/logging.
    /// </summary>
    private void RecordPasswordFailure(string username, string description) =>
        _audit.TryWrite(
            AuditEventType.SshPasswordAuthFailure,
            AuditEventOutcome.Failure,
            description,
            new AuditEventContext { RequestedUsername = username });
}
