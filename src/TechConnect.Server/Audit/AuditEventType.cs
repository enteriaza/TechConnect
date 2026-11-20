// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuditEventType.cs
//
// Purpose:
// Enumerates audit event types and outcomes recorded by TechConnect.Server
// authentication, authorization, attachment, and enable paths.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Audit;

/// <summary>
/// Discriminator for persisted/logged audit events.
/// </summary>
/// <remarks>
/// Values are stable identifiers for history formatting and filtering; do not
/// reorder existing numeric assignments without a migration plan.
/// </remarks>
internal enum AuditEventType
{
    /// <summary>SSH password authentication succeeded.</summary>
    SshPasswordAuthSuccess = 1,

    /// <summary>SSH password authentication failed.</summary>
    SshPasswordAuthFailure = 2,

    /// <summary>SSH public-key authentication succeeded.</summary>
    SshPublicKeyAuthSuccess = 3,

    /// <summary>SSH public-key authentication failed.</summary>
    SshPublicKeyAuthFailure = 4,

    /// <summary>RADIUS Access-Accept authentication succeeded.</summary>
    RadiusAuthSuccess = 5,

    /// <summary>RADIUS Access-Reject received.</summary>
    RadiusAuthRejected = 6,

    /// <summary>RADIUS servers unavailable (timeout/transport).</summary>
    RadiusUnavailable = 7,

    /// <summary>Local password fallback succeeded after RADIUS unavailability.</summary>
    LocalFallbackSuccess = 8,

    /// <summary>Local password fallback failed after RADIUS unavailability.</summary>
    LocalFallbackFailure = 9,

    /// <summary>CLI authorization denied a command.</summary>
    AuthorizationDenied = 10,

    /// <summary>Connector registration accepted.</summary>
    ConnectorRegistered = 11,

    /// <summary>Connector registration rejected.</summary>
    ConnectorRegistrationRejected = 12,

    /// <summary>Connector disconnected.</summary>
    ConnectorDisconnected = 13,

    /// <summary>Operator attached to a connector session.</summary>
    OperatorAttached = 14,

    /// <summary>Operator detached from a connector session.</summary>
    OperatorDetached = 15,

    /// <summary>Attachment released because the SSH session disconnected.</summary>
    AttachmentReleasedSshDisconnect = 16,

    /// <summary>Attachment released because the connector disconnected.</summary>
    AttachmentReleasedConnectorDisconnect = 17,

    /// <summary>Interactive terminal mode entered.</summary>
    TerminalModeEntered = 18,

    /// <summary>Interactive terminal mode exited.</summary>
    TerminalModeExited = 19,

    /// <summary>Baud-rate change request started.</summary>
    BaudRequestStarted = 20,

    /// <summary>Baud-rate change request succeeded.</summary>
    BaudRequestSucceeded = 21,

    /// <summary>Baud-rate change request failed.</summary>
    BaudRequestFailed = 22,

    /// <summary>Baud-rate change request timed out.</summary>
    BaudRequestTimedOut = 23,

    /// <summary>Administrative disconnect declined by policy or operator.</summary>
    AdministrativeDisconnectDeclined = 24,

    /// <summary>Administrative disconnect cancelled.</summary>
    AdministrativeDisconnectCancelled = 25,

    /// <summary>Administrative disconnect initiated.</summary>
    AdministrativeDisconnectInitiated = 26,

    /// <summary>Administrative disconnect succeeded.</summary>
    AdministrativeDisconnectSucceeded = 27,

    /// <summary>Administrative disconnect failed.</summary>
    AdministrativeDisconnectFailed = 28,

    /// <summary>Enable authentication attempted.</summary>
    EnableAuthAttempted = 29,

    /// <summary>Enable authentication succeeded.</summary>
    EnableAuthSucceeded = 30,

    /// <summary>Enable authentication failed.</summary>
    EnableAuthFailed = 31,

    /// <summary>Enable authentication unavailable (not configured/disabled).</summary>
    EnableAuthUnavailable = 32,

    /// <summary>Operator active privilege level changed.</summary>
    PrivilegeChanged = 33,

    /// <summary>Operator privilege restored to level 1 via disable.</summary>
    DisableToLevel1 = 34,

    /// <summary>Enable authorization denied (requested privilege exceeds authorized).</summary>
    EnableAuthorizationDenied = 35,

    /// <summary>Operator entered configuration mode.</summary>
    ConfigurationModeEntered = 36,

    /// <summary>Candidate configuration written to startup-config (appsettings.json).</summary>
    ConfigurationSaved = 37,

    /// <summary>Configuration write refused or failed (validation, conflict, or I/O).</summary>
    ConfigurationSaveFailed = 38,

    /// <summary>Unsaved candidate configuration changes discarded.</summary>
    ConfigurationDiscarded = 39
}

/// <summary>
/// Coarse outcome classification accompanying an <see cref="AuditEventType"/>.
/// </summary>
internal enum AuditEventOutcome
{
    /// <summary>Operation completed successfully.</summary>
    Success = 1,

    /// <summary>Operation failed (error, mismatch, unavailable mapped to failure).</summary>
    Failure = 2,

    /// <summary>Operation explicitly denied (authorization or Access-Reject).</summary>
    Denied = 3,

    /// <summary>Informational milestone (attempt/start) without success/failure.</summary>
    Informational = 4
}
