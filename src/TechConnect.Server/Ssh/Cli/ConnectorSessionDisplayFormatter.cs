// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionDisplayFormatter.cs
//
// Purpose:
// Formats connector session list/detail/serial snapshots for SSH CLI
// show sessions / show serial output.
//
// -----------------------------------------------------------------------------
using System.Globalization;
using System.Text;
using TechConnect.Server.Sessions;

namespace TechConnect.Server.Ssh.Cli;

/// <summary>Formats <see cref="ConnectorSessionSnapshot"/> for the SSH CLI.</summary>
internal static class ConnectorSessionDisplayFormatter
{
    /// <summary>Fallback terminal width when the SSH client reports fewer than 40 columns.</summary>
    public const int DefaultColumns = 80;

    /// <summary>
    /// Formats the compact <c>show sessions</c> table (narrow stacked lines vs wide columns)
    /// and an active-session count footer.
    /// </summary>
    public static string FormatSessionList(IReadOnlyList<ConnectorSessionSnapshot> sessions, int terminalColumns)
    {
        if (sessions.Count == 0)
        {
            return "No active connector sessions.";
        }

        var width = NormalizeWidth(terminalColumns);
        var sb = new StringBuilder();

        if (width < 70)
        {
            foreach (var s in sessions)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(CultureInfo.InvariantCulture, $"{s.SessionId}  ");
                sb.Append(Truncate(DisplayConnector(s), Math.Max(8, width / 3)));
                sb.Append(' ');
                sb.Append(s.ConnectionState);
                if (s.OperatorAttached)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  op={s.OperatorUsername ?? "-"}");
                }
            }
        }
        else
        {
            // Session  Connector  Version  Remote  Connected  State  Operator
            var idW = 10;
            var connW = width >= 100 ? 16 : 14;
            var verW = 10;
            var remW = width >= 100 ? 22 : 18;
            var connectedW = 10;
            var stateW = 10;
            var opW = Math.Max(8, width - (idW + connW + verW + remW + connectedW + stateW + 6));

            sb.Append(Pad("SESSION", idW)).Append(' ');
            sb.Append(Pad("CONNECTOR", connW)).Append(' ');
            sb.Append(Pad("VERSION", verW)).Append(' ');
            sb.Append(Pad("REMOTE", remW)).Append(' ');
            sb.Append(Pad("CONNECTED", connectedW)).Append(' ');
            sb.Append(Pad("STATE", stateW)).Append(' ');
            sb.AppendLine(Pad("OPERATOR", opW));

            foreach (var s in sessions)
            {
                sb.Append(Pad(s.SessionId, idW)).Append(' ');
                sb.Append(Pad(Truncate(DisplayConnector(s), connW), connW)).Append(' ');
                sb.Append(Pad(Truncate(s.ApplicationVersion ?? "-", verW), verW)).Append(' ');
                sb.Append(Pad(Truncate(s.RemoteEndpoint ?? "-", remW), remW)).Append(' ');
                sb.Append(Pad(FormatCompactDuration(s.ConnectionDuration), connectedW)).Append(' ');
                sb.Append(Pad(s.ConnectionState, stateW)).Append(' ');
                sb.AppendLine(
                    Pad(
                        Truncate(
                            s.OperatorAttached ? (s.OperatorUsername ?? "attached") : "-",
                            opW),
                        opW));
            }
        }

        sb.AppendLine();
        sb.Append(
            CultureInfo.InvariantCulture,
            $"{sessions.Count} active session{(sessions.Count == 1 ? "" : "s")}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats <c>show session</c> / detail blocks: connector, serial, attachment, and pending baud.
    /// </summary>
    public static string FormatSessionDetail(ConnectorSessionSnapshot s)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Connector Information");
        sb.AppendLine(Field("Session ID", s.SessionId));
        sb.AppendLine(Field("Identity", AbbreviateIdentity(s.ConnectorIdentity)));
        sb.AppendLine(Field("Hostname", Reported(s.Hostname)));
        sb.AppendLine(Field("Version", Reported(s.ApplicationVersion)));
        sb.AppendLine(Field("Remote Endpoint", Optional(s.RemoteEndpoint)));
        sb.AppendLine(Field("TLS Protocol", Optional(s.TlsProtocol)));
        sb.AppendLine(Field("Connected", FormatTimestamp(s.ConnectedAtUtc)));
        sb.AppendLine(Field("Duration", FormatDuration(s.ConnectionDuration) + " (computed)"));
        sb.AppendLine(Field("Last Activity", s.LastActivityUtc is { } a ? FormatTimestamp(a) : "-"));
        sb.AppendLine(Field("State", s.ConnectionState));
        sb.AppendLine();

        sb.AppendLine("Serial Information");
        if (!s.SerialConfigurationReported)
        {
            sb.AppendLine(Field("Configuration", "Not reported"));
            if (!string.IsNullOrWhiteSpace(s.PortName))
            {
                sb.AppendLine(Field("Port (from Hello)", s.PortName));
            }
        }
        else
        {
            sb.AppendLine(Field("Port", Reported(s.PortName)));
            sb.AppendLine(Field("Baud Rate", s.BaudRate?.ToString(CultureInfo.InvariantCulture) ?? "Not reported"));
            sb.AppendLine(Field("Data Bits", s.DataBits?.ToString(CultureInfo.InvariantCulture) ?? "Not reported"));
            sb.AppendLine(Field("Parity", Reported(s.Parity)));
            sb.AppendLine(Field("Stop Bits", FormatStopBits(s.StopBits)));
            sb.AppendLine(Field("Handshake", Reported(s.Handshake)));
            sb.AppendLine(
                Field(
                    "Reported At",
                    s.SerialReportedAtUtc is { } r ? FormatTimestamp(r) : "-"));
        }

        sb.AppendLine();
        sb.AppendLine("Operator Attachment");
        sb.AppendLine(Field("Attached", s.OperatorAttached ? "Yes" : "No"));
        sb.AppendLine(Field("Operator", s.OperatorAttached ? Optional(s.OperatorUsername) : "-"));
        sb.AppendLine(
            Field(
                "Attached At",
                s.AttachedAtUtc is { } t ? FormatTimestamp(t) : "-"));
        sb.AppendLine(Field("Terminal Active", s.OperatorAttached ? (s.TerminalActive ? "Yes" : "No") : "-"));

        sb.AppendLine();
        sb.AppendLine("Pending Baud Request");
        sb.AppendLine(Field("Pending", s.BaudRequestPending ? "Yes" : "No"));
        if (s.BaudRequestPending)
        {
            sb.Append(
                Field(
                    "Requested Rate",
                    s.PendingBaudRate?.ToString(CultureInfo.InvariantCulture) ?? "-"));
        }
        else
        {
            sb.Append(Field("Requested Rate", "-"));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats <c>show serial</c> for the attached connector, or an error when not attached / not reported.
    /// </summary>
    public static string FormatSerial(ConnectorSessionSnapshot? attachedSnapshot, bool hasAttachment)
    {
        if (!hasAttachment || attachedSnapshot is null)
        {
            return "% Not attached to a connector. Use 'connect <session-id>' first.";
        }

        if (!attachedSnapshot.SerialConfigurationReported)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Serial configuration");
            sb.AppendLine(Field("Configuration", "Not reported"));
            if (!string.IsNullOrWhiteSpace(attachedSnapshot.PortName))
            {
                sb.Append(Field("Port (from Hello)", attachedSnapshot.PortName));
            }

            return sb.ToString().TrimEnd();
        }

        var body = new StringBuilder();
        body.AppendLine("Serial configuration");
        body.AppendLine(Field("Port", Reported(attachedSnapshot.PortName)));
        body.AppendLine(
            Field(
                "Baud Rate",
                attachedSnapshot.BaudRate?.ToString(CultureInfo.InvariantCulture) ?? "Not reported"));
        body.AppendLine(
            Field(
                "Data Bits",
                attachedSnapshot.DataBits?.ToString(CultureInfo.InvariantCulture) ?? "Not reported"));
        body.AppendLine(Field("Parity", Reported(attachedSnapshot.Parity)));
        body.AppendLine(Field("Stop Bits", FormatStopBits(attachedSnapshot.StopBits)));
        body.AppendLine(Field("Handshake", Reported(attachedSnapshot.Handshake)));
        body.Append(
            Field(
                "Reported At",
                attachedSnapshot.SerialReportedAtUtc is { } r ? FormatTimestamp(r) : "-"));
        return body.ToString();
    }

    /// <summary>Prefers hostname; otherwise abbreviated connector identity thumbprint.</summary>
    private static string DisplayConnector(ConnectorSessionSnapshot s) =>
        !string.IsNullOrWhiteSpace(s.Hostname)
            ? s.Hostname!
            : AbbreviateIdentity(s.ConnectorIdentity);

    /// <summary>Indented label/value line with an 18-character label column.</summary>
    private static string Field(string label, string value) =>
        "  " + label.PadRight(18) + ": " + value;

    /// <summary>Trims a reported value or returns <c>Not reported</c> when blank.</summary>
    private static string Reported(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not reported" : value.Trim();

    /// <summary>Trims an optional value or returns <c>-</c> when blank.</summary>
    private static string Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    /// <summary>Formats a UTC timestamp as <c>yyyy-MM-dd HH:mm:ss UTC</c>.</summary>
    private static string FormatTimestamp(DateTimeOffset utc) =>
        utc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Human duration for detail views (days/hours/minutes/seconds as appropriate).</summary>
    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)duration.TotalDays}d {duration.Hours:D2}h {duration.Minutes:D2}m");
        }

        if (duration.TotalHours >= 1)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)duration.TotalHours}h {duration.Minutes:D2}m {duration.Seconds:D2}s");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{duration.Minutes:D2}m {duration.Seconds:D2}s");
    }

    /// <summary>Compact duration for the wide session list CONNECTED column.</summary>
    private static string FormatCompactDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalDays}d{duration.Hours:D2}h");
        }

        if (duration.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}h{duration.Minutes:D2}m");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes:D2}m{duration.Seconds:D2}s");
    }

    /// <summary>Maps enum-style stop-bit names to operator-facing 1 / 1.5 / 2 labels.</summary>
    private static string FormatStopBits(string? stopBits) =>
        stopBits switch
        {
            null or "" => "Not reported",
            "One" => "1",
            "Two" => "2",
            "OnePointFive" => "1.5",
            _ => stopBits
        };

    /// <summary>Shortens a long connector thumbprint to <c>xxxx…yyyy</c> for display.</summary>
    internal static string AbbreviateIdentity(string thumbprint)
    {
        if (string.IsNullOrEmpty(thumbprint))
        {
            return "-";
        }

        return thumbprint.Length <= 8
            ? thumbprint
            : thumbprint[..4] + "…" + thumbprint[^4..];
    }

    /// <summary>Clamps terminal width to [<see cref="DefaultColumns"/>, 200] when below 40.</summary>
    private static int NormalizeWidth(int columns) =>
        columns < 40 ? DefaultColumns : Math.Min(columns, 200);

    /// <summary>Right-pads or truncates a cell to a fixed column width.</summary>
    private static string Pad(string value, int width) =>
        value.Length >= width ? Truncate(value, width) : value.PadRight(width);

    /// <summary>Truncates with an ellipsis when the value exceeds <paramref name="max"/>.</summary>
    private static string Truncate(string value, int max)
    {
        if (max <= 0)
        {
            return string.Empty;
        }

        if (value.Length <= max)
        {
            return value;
        }

        return max <= 1 ? value[..max] : value[..(max - 1)] + "…";
    }
}
