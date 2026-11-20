// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    AuditHistoryFormatter.cs
//
// Purpose:
// Formats stored audit events into operator-facing history text.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Text;

namespace TechConnect.Server.Audit;

/// <summary>
/// Formats audit snapshots for CLI display. Callers must pass an already-materialized snapshot
/// so the audit store lock is not held during formatting.
/// </summary>
internal static class AuditHistoryFormatter
{
    /// <summary>
    /// Default Limit backing member.
    /// </summary>
    public const int DefaultLimit = InMemoryAuditEventStore.DefaultCliLimit;

    /// <summary>
    /// Format Disabled.
    /// </summary>
    public static string FormatDisabled() =>
        "% Audit history is unavailable because auditing is disabled.";

    /// <summary>
    /// Format Empty.
    /// </summary>
    public static string FormatEmpty(string? filterDescription = null) =>
        filterDescription is null
            ? "No audit events recorded."
            : $"No audit events matching {filterDescription}.";

    /// <summary>
    /// Format.
    /// </summary>
    public static string Format(
        IReadOnlyList<AuditEvent> events,
        bool hasEvictedEvents,
        int maximumCapacity,
        int terminalColumns,
        string? emptyFilterDescription = null)
    {
        if (events.Count == 0)
        {
            return FormatEmpty(emptyFilterDescription);
        }

        var width = terminalColumns < 40 ? 80 : terminalColumns;
        var sb = new StringBuilder();

        if (hasEvictedEvents)
        {
            sb.AppendLine(
                $"Note: older events were evicted (capacity {maximumCapacity}). Showing newest retained matches.");
        }

        // Narrow terminals: stack fields. Wider: table.
        if (width < 72)
        {
            FormatNarrow(sb, events);
        }
        else
        {
            FormatTable(sb, events, width);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Format Table.
    /// </summary>
    private static void FormatTable(StringBuilder sb, IReadOnlyList<AuditEvent> events, int width)
    {
        // Time(19) Actor Event Subject Outcome Description
        var timeW = 19;
        var actorW = 12;
        var eventW = 18;
        var subjectW = 12;
        var outcomeW = 12;
        var fixedW = timeW + actorW + eventW + subjectW + outcomeW + 5; // spaces
        var descW = Math.Max(12, width - fixedW);

        sb.Append(Pad("Time", timeW)).Append(' ');
        sb.Append(Pad("Actor", actorW)).Append(' ');
        sb.Append(Pad("Event", eventW)).Append(' ');
        sb.Append(Pad("Subject", subjectW)).Append(' ');
        sb.Append(Pad("Outcome", outcomeW)).Append(' ');
        sb.AppendLine("Description");

        foreach (var evt in events)
        {
            sb.Append(Pad(FormatTime(evt.TimestampUtc), timeW)).Append(' ');
            sb.Append(Pad(Truncate(DisplayActor(evt), actorW), actorW)).Append(' ');
            sb.Append(Pad(Truncate(DisplayEventType(evt.EventType), eventW), eventW)).Append(' ');
            sb.Append(Pad(Truncate(DisplaySubject(evt), subjectW), subjectW)).Append(' ');
            sb.Append(Pad(Truncate(evt.Outcome.ToString(), outcomeW), outcomeW)).Append(' ');
            sb.AppendLine(Truncate(SanitizeForDisplay(evt.Description), descW));
        }
    }

    /// <summary>
    /// Format Narrow.
    /// </summary>
    private static void FormatNarrow(StringBuilder sb, IReadOnlyList<AuditEvent> events)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
            {
                sb.AppendLine("---");
            }

            var evt = events[i];
            sb.AppendLine(FormatTime(evt.TimestampUtc));
            sb.Append("Actor: ").AppendLine(DisplayActor(evt));
            sb.Append("Event: ").AppendLine(DisplayEventType(evt.EventType));
            sb.Append("Subject: ").AppendLine(DisplaySubject(evt));
            sb.Append("Outcome: ").AppendLine(evt.Outcome.ToString());
            sb.Append("Desc: ").AppendLine(SanitizeForDisplay(evt.Description));
        }
    }

    /// <summary>
    /// Display Actor.
    /// </summary>
    private static string DisplayActor(AuditEvent evt) =>
        !string.IsNullOrEmpty(evt.ActorUsername)
            ? evt.ActorUsername
            : !string.IsNullOrEmpty(evt.RequestedUsername)
                ? evt.RequestedUsername!
                : "-";

    /// <summary>
    /// Display Subject.
    /// </summary>
    private static string DisplaySubject(AuditEvent evt)
    {
        if (!string.IsNullOrEmpty(evt.SessionId))
        {
            return evt.SessionId!;
        }

        if (!string.IsNullOrEmpty(evt.ConnectorIdentity))
        {
            return evt.ConnectorIdentity!;
        }

        return "-";
    }

    /// <summary>
    /// Display Event Type.
    /// </summary>
    internal static string DisplayEventType(AuditEventType type) => type switch
    {
        AuditEventType.SshPasswordAuthSuccess => "ssh-password-ok",
        AuditEventType.SshPasswordAuthFailure => "ssh-password-fail",
        AuditEventType.SshPublicKeyAuthSuccess => "ssh-pubkey-ok",
        AuditEventType.SshPublicKeyAuthFailure => "ssh-pubkey-fail",
        AuditEventType.RadiusAuthSuccess => "radius-ok",
        AuditEventType.RadiusAuthRejected => "radius-reject",
        AuditEventType.RadiusUnavailable => "radius-unavail",
        AuditEventType.LocalFallbackSuccess => "local-fallback-ok",
        AuditEventType.LocalFallbackFailure => "local-fallback-fail",
        AuditEventType.AuthorizationDenied => "authz-denied",
        AuditEventType.ConnectorRegistered => "connector-reg",
        AuditEventType.ConnectorRegistrationRejected => "connector-rej",
        AuditEventType.ConnectorDisconnected => "connector-disc",
        AuditEventType.OperatorAttached => "op-attach",
        AuditEventType.OperatorDetached => "op-detach",
        AuditEventType.AttachmentReleasedSshDisconnect => "attach-ssh-drop",
        AuditEventType.AttachmentReleasedConnectorDisconnect => "attach-conn-drop",
        AuditEventType.TerminalModeEntered => "term-enter",
        AuditEventType.TerminalModeExited => "term-exit",
        AuditEventType.BaudRequestStarted => "baud-start",
        AuditEventType.BaudRequestSucceeded => "baud-ok",
        AuditEventType.BaudRequestFailed => "baud-fail",
        AuditEventType.BaudRequestTimedOut => "baud-timeout",
        AuditEventType.AdministrativeDisconnectDeclined => "admin-disc-no",
        AuditEventType.AdministrativeDisconnectCancelled => "admin-disc-cancel",
        AuditEventType.AdministrativeDisconnectInitiated => "admin-disc-start",
        AuditEventType.AdministrativeDisconnectSucceeded => "admin-disc-ok",
        AuditEventType.AdministrativeDisconnectFailed => "admin-disc-fail",
        AuditEventType.EnableAuthAttempted => "enable-auth-try",
        AuditEventType.EnableAuthSucceeded => "enable-auth-ok",
        AuditEventType.EnableAuthFailed => "enable-auth-fail",
        AuditEventType.EnableAuthUnavailable => "enable-auth-unavail",
        AuditEventType.PrivilegeChanged => "priv-changed",
        AuditEventType.DisableToLevel1 => "disable-1",
        AuditEventType.EnableAuthorizationDenied => "enable-authz-deny",
        AuditEventType.ConfigurationModeEntered => "config-enter",
        AuditEventType.ConfigurationSaved => "config-write",
        AuditEventType.ConfigurationSaveFailed => "config-write-fail",
        AuditEventType.ConfigurationDiscarded => "config-discard",
        // cleanup-incomplete is recorded as AdministrativeDisconnectFailed with reason detail
        _ => type.ToString()
    };

    /// <summary>
    /// Format Time.
    /// </summary>
    private static string FormatTime(DateTimeOffset utc) =>
        utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Pad.
    /// </summary>
    private static string Pad(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadRight(width);

    /// <summary>
    /// Truncate.
    /// </summary>
    private static string Truncate(string value, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= width)
        {
            return value;
        }

        return width <= 1 ? value[..1] : value[..(width - 1)] + "…";
    }

    /// <summary>
    /// Sanitize For Display.
    /// </summary>
    private static string SanitizeForDisplay(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 240)];
        var written = 0;
        foreach (var ch in value)
        {
            if (written >= buffer.Length)
            {
                break;
            }

            if (ch < '\u0020' || (ch >= '\u007F' && ch <= '\u009F'))
            {
                continue;
            }

            buffer[written++] = ch;
        }

        return written == 0 ? "-" : new string(buffer[..written]);
    }
}
