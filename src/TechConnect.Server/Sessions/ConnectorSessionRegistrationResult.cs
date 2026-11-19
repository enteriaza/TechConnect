// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionRegistrationResult.cs
//
// Purpose:
// Carries the outcome of Hello registration against ConnectorSessionRegistry,
// including duplicate-identity rejection and idempotent same-connection re-register.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// Status codes for <see cref="ConnectorSessionRegistry.TryRegister"/>.
/// </summary>
internal enum ConnectorSessionRegistrationStatus
{
    /// <summary>A new session was allocated and indexed.</summary>
    Registered = 0,

    /// <summary>
    /// Another live connection already holds this certificate identity (reject-new policy).
    /// </summary>
    DuplicateIdentity = 1,

    /// <summary>
    /// This connection ID already has a registered session; treated as success by
    /// <see cref="ConnectorSessionRegistrationResult.Succeeded"/>.
    /// </summary>
    AlreadyRegisteredOnConnection = 2
}

/// <summary>
/// Result of attempting to register a connector session after Hello.
/// </summary>
internal sealed class ConnectorSessionRegistrationResult
{
    /// <summary>
    /// Initializes a registration result.
    /// </summary>
    /// <param name="status">Registration status.</param>
    /// <param name="session">Session when registered or already registered; otherwise null.</param>
    /// <param name="error">Operator/connector-facing error when rejected.</param>
    private ConnectorSessionRegistrationResult(
        ConnectorSessionRegistrationStatus status,
        ConnectorSession? session,
        string? error)
    {
        Status = status;
        Session = session;
        Error = error;
    }

    /// <summary>Registration status.</summary>
    public ConnectorSessionRegistrationStatus Status { get; }

    /// <summary>Session for success / already-registered outcomes; null on duplicate identity.</summary>
    public ConnectorSession? Session { get; }

    /// <summary>Rejection text for duplicate identity (and similar failures).</summary>
    public string? Error { get; }

    /// <summary>
    /// True for <see cref="ConnectorSessionRegistrationStatus.Registered"/> or
    /// <see cref="ConnectorSessionRegistrationStatus.AlreadyRegisteredOnConnection"/>.
    /// </summary>
    public bool Succeeded =>
        Status is ConnectorSessionRegistrationStatus.Registered
            or ConnectorSessionRegistrationStatus.AlreadyRegisteredOnConnection;

    /// <summary>
    /// Creates a newly registered result.
    /// </summary>
    /// <param name="session">Allocated session.</param>
    /// <returns>Result with <see cref="ConnectorSessionRegistrationStatus.Registered"/>.</returns>
    public static ConnectorSessionRegistrationResult Registered(ConnectorSession session) =>
        new(ConnectorSessionRegistrationStatus.Registered, session, null);

    /// <summary>
    /// Creates an idempotent already-registered-on-connection result.
    /// </summary>
    /// <param name="session">Existing session for that connection ID.</param>
    /// <returns>Result with <see cref="ConnectorSessionRegistrationStatus.AlreadyRegisteredOnConnection"/>.</returns>
    public static ConnectorSessionRegistrationResult AlreadyRegistered(ConnectorSession session) =>
        new(ConnectorSessionRegistrationStatus.AlreadyRegisteredOnConnection, session, null);

    /// <summary>
    /// Creates a duplicate-identity rejection.
    /// </summary>
    /// <param name="error">Human-readable rejection reason.</param>
    /// <returns>Result with null <see cref="Session"/>.</returns>
    public static ConnectorSessionRegistrationResult DuplicateIdentity(string error) =>
        new(ConnectorSessionRegistrationStatus.DuplicateIdentity, null, error);
}
