// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    OperatorAuthenticationService.cs
//
// Purpose:
// Selects local public-key, RADIUS password, or local password authentication
// and builds AuthenticatedOperator identities after successful SSH auth.
//
// -----------------------------------------------------------------------------
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging;
using TechConnect.Server.Audit;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius;

namespace TechConnect.Server.Authentication;

/// <summary>
/// Selects local public-key, RADIUS password, or local password authentication.
/// </summary>
/// <remarks>
/// Privilege is returned on the identity but not enforced against CLI commands here.
/// Concurrent calls are supported insofar as dependencies allow; this type holds no
/// mutable per-call state. Cancellation is honored on the RADIUS password path.
/// Failures return <see langword="null"/> and write audit events; they do not throw
/// for ordinary reject/fail outcomes.
/// </remarks>
internal sealed class OperatorAuthenticationService
{
    /// <summary>Local account store for password and public-key paths.</summary>
    private readonly LocalOperatorAccountStore _localAccounts;

    /// <summary>RADIUS password provider; used when enabled instead of local password.</summary>
    private readonly RadiusPasswordAuthenticationProvider _radiusPasswords;

    /// <summary>Clock used for <see cref="AuthenticatedOperator.AuthenticatedAtUtc"/>.</summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>Audit writer; defaults to <see cref="NoOpAuditEventWriter"/> when omitted.</summary>
    private readonly IAuditEventWriter _audit;

    /// <summary>Logger for authentication outcomes (usernames only; no secrets).</summary>
    private readonly ILogger<OperatorAuthenticationService> _logger;

    /// <summary>
    /// Creates the authentication façade over local and RADIUS providers.
    /// </summary>
    /// <param name="localAccounts">Local operator account store.</param>
    /// <param name="radiusPasswords">RADIUS password authentication provider.</param>
    /// <param name="timeProvider">UTC clock for authentication timestamps.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="audit">Optional audit writer; when <see langword="null"/>, a no-op writer is used.</param>
    /// <exception cref="ArgumentNullException">A required dependency is <see langword="null"/>.</exception>
    public OperatorAuthenticationService(
        LocalOperatorAccountStore localAccounts,
        RadiusPasswordAuthenticationProvider radiusPasswords,
        TimeProvider timeProvider,
        ILogger<OperatorAuthenticationService> logger,
        IAuditEventWriter? audit = null)
    {
        _localAccounts = localAccounts ?? throw new ArgumentNullException(nameof(localAccounts));
        _radiusPasswords = radiusPasswords ?? throw new ArgumentNullException(nameof(radiusPasswords));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _audit = audit ?? new NoOpAuditEventWriter();
    }

    /// <summary>
    /// <see langword="true"/> when RADIUS password auth is enabled or local password auth is enabled.
    /// </summary>
    public bool PasswordAuthenticationAvailable =>
        _radiusPasswords.Enabled || _localAccounts.PasswordAuthenticationEnabled;

    /// <summary>
    /// <see langword="true"/> when local public-key authentication is enabled.
    /// </summary>
    public bool PublicKeyAuthenticationAvailable =>
        _localAccounts.PublicKeyAuthenticationEnabled;

    /// <summary>
    /// Lightweight public-key probe used by SSH key-exchange query paths.
    /// </summary>
    /// <param name="username">Requested username.</param>
    /// <param name="publicKey">Presented key; <see langword="null"/> fails.</param>
    /// <param name="fingerprint">Fingerprint string (empty when unavailable).</param>
    /// <returns><see langword="true"/> when the key would authenticate for the username.</returns>
    public bool TryAuthenticatePublicKeyQuery(
        string? username,
        IKeyPair? publicKey,
        out string fingerprint)
    {
        fingerprint = string.Empty;
        if (!PublicKeyAuthenticationAvailable || publicKey is null)
        {
            return false;
        }

        return _localAccounts.TryAuthenticatePublicKey(username, publicKey, out _, out fingerprint);
    }

    /// <summary>
    /// Authenticates via local SSH public key and builds an operator identity on success.
    /// </summary>
    /// <param name="username">Requested username.</param>
    /// <param name="publicKey">Presented SSH public key; must not be <see langword="null"/>.</param>
    /// <returns>Authenticated identity, or <see langword="null"/> when rejected or disabled.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="publicKey"/> is <see langword="null"/>.</exception>
    public AuthenticatedOperator? AuthenticatePublicKey(string? username, IKeyPair publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (!PublicKeyAuthenticationAvailable)
        {
            _logger.LogInformation("SSH public-key authentication rejected: method disabled");
            return null;
        }

        if (!_localAccounts.TryAuthenticatePublicKey(username, publicKey, out var account, out var fingerprint) ||
            account is null)
        {
            _logger.LogInformation(
                "SSH public-key authentication rejected for user {Username} (fingerprint {Fingerprint})",
                username ?? "(unknown)",
                fingerprint);
            _audit.TryWrite(
                AuditEventType.SshPublicKeyAuthFailure,
                AuditEventOutcome.Failure,
                "SSH public-key authentication failed",
                new AuditEventContext { RequestedUsername = username });
            return null;
        }

        var identity = new AuthenticatedOperator(
            Username: account.Username,
            Provider: AuthenticationProviderKind.Local,
            Method: AuthenticationMethodKind.PublicKey,
            Privilege: account.Privilege,
            AuthenticatedAtUtc: _timeProvider.GetUtcNow(),
            OperatorSessionId: Guid.NewGuid().ToString("N"),
            LocalAccountId: account.AccountId,
            PublicKeyFingerprint: fingerprint);

        _logger.LogInformation(
            "SSH public-key authentication succeeded for user {Username} privilege={Privilege} session={SessionId}",
            identity.Username,
            identity.Privilege,
            identity.OperatorSessionId);

        _audit.TryWrite(
            AuditEventType.SshPublicKeyAuthSuccess,
            AuditEventOutcome.Success,
            "SSH public-key authentication succeeded",
            new AuditEventContext { ActorUsername = identity.Username, RequestedUsername = username });

        return identity;
    }

    /// <summary>
    /// Authenticates via RADIUS password when enabled; otherwise local password verification.
    /// </summary>
    /// <param name="username">Requested username.</param>
    /// <param name="password">Candidate password; never logged.</param>
    /// <param name="cancellationToken">Cancellation for the RADIUS exchange path.</param>
    /// <returns>
    /// Authenticated identity, or <see langword="null"/> when the method is disabled,
    /// credentials fail, or cancellation occurs on the RADIUS path.
    /// </returns>
    public async Task<AuthenticatedOperator?> AuthenticatePasswordAsync(
        string? username,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!PasswordAuthenticationAvailable)
        {
            _logger.LogInformation("SSH password authentication rejected: method disabled");
            return null;
        }

        if (_radiusPasswords.Enabled)
        {
            try
            {
                // RADIUS provider records its own audit events for the password path.
                return await _radiusPasswords
                    .AuthenticateAsync(username, password, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "RADIUS password authentication cancelled for user {Username}",
                    username ?? "(unknown)");
                return null;
            }
        }

        var verifySw = System.Diagnostics.Stopwatch.StartNew();
        var ok = _localAccounts.TryAuthenticatePassword(username, password, out var account);
        var verifyMs = verifySw.ElapsedMilliseconds;
        if (!ok || account is null)
        {
            _logger.LogInformation(
                "SSH password authentication failed for user {Username} (local lookup+verify {ElapsedMs}ms)",
                username ?? "(unknown)",
                verifyMs);
            _audit.TryWrite(
                AuditEventType.SshPasswordAuthFailure,
                AuditEventOutcome.Failure,
                "SSH password authentication failed",
                new AuditEventContext { RequestedUsername = username });
            return null;
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
            "SSH local password authentication succeeded for user {Username} privilege={Privilege} session={SessionId} (local lookup+verify {ElapsedMs}ms)",
            identity.Username,
            identity.Privilege,
            identity.OperatorSessionId,
            verifyMs);

        _audit.TryWrite(
            AuditEventType.SshPasswordAuthSuccess,
            AuditEventOutcome.Success,
            "SSH password authentication succeeded",
            new AuditEventContext { ActorUsername = identity.Username, RequestedUsername = username });

        return identity;
    }
}
