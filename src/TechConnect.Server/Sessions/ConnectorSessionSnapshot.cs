// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorSessionSnapshot.cs
//
// Purpose:
// Defines the immutable CLI/display projection of a connector session assembled
// from registry, attachment, serial cache, and pending baud state.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Sessions;

/// <summary>
/// Immutable display snapshot assembled from registry, attachment, serial cache, and pending baud.
/// </summary>
/// <remarks>
/// Connection state for live registry sessions is always the string <c>Connected</c>;
/// operator attachment is exposed separately via <see cref="OperatorAttached"/> /
/// <see cref="TerminalActive"/>. This type does not own lifetimes.
/// </remarks>
internal sealed class ConnectorSessionSnapshot
{
    /// <summary>Registered connector session ID.</summary>
    public required string SessionId { get; init; }

    /// <summary>Full certificate thumbprint (abbreviate only when formatting for CLI).</summary>
    public required string ConnectorIdentity { get; init; }

    /// <summary>Hostname from the registry session, if any.</summary>
    public string? Hostname { get; init; }

    /// <summary>Application version from the registry session, if any.</summary>
    public string? ApplicationVersion { get; init; }

    /// <summary>Remote endpoint from the registry session, if any.</summary>
    public string? RemoteEndpoint { get; init; }

    /// <summary>TLS protocol name from the registry session, if any.</summary>
    public string? TlsProtocol { get; init; }

    /// <summary>UTC registration time.</summary>
    public DateTimeOffset ConnectedAtUtc { get; init; }

    /// <summary>Last activity timestamp from the registry, if any.</summary>
    public DateTimeOffset? LastActivityUtc { get; init; }

    /// <summary>Computed at snapshot time from <see cref="ConnectedAtUtc"/>.</summary>
    public TimeSpan ConnectionDuration { get; init; }

    /// <summary>Always <c>Connected</c> for sessions still present in the registry.</summary>
    public string ConnectionState { get; init; } = "Connected";

    /// <summary>True when an <see cref="TechConnect.Server.Attachment.OperatorAttachment"/> exists for this session.</summary>
    public bool OperatorAttached { get; init; }

    /// <summary>Attached operator username when known.</summary>
    public string? OperatorUsername { get; init; }

    /// <summary>UTC attach time when an operator is attached.</summary>
    public DateTimeOffset? AttachedAtUtc { get; init; }

    /// <summary>True when attached and the operator is currently in terminal mode.</summary>
    public bool TerminalActive { get; init; }

    /// <summary>True when the serial configuration cache has an entry for this session.</summary>
    public bool SerialConfigurationReported { get; init; }

    /// <summary>Port name from serial cache or Hello, depending on assembly rules.</summary>
    public string? PortName { get; init; }

    /// <summary>Cached baud rate when reported; otherwise null.</summary>
    public int? BaudRate { get; init; }

    /// <summary>Cached data bits when reported; otherwise null.</summary>
    public int? DataBits { get; init; }

    /// <summary>Cached parity when reported; otherwise null.</summary>
    public string? Parity { get; init; }

    /// <summary>Cached stop bits when reported; otherwise null.</summary>
    public string? StopBits { get; init; }

    /// <summary>Cached handshake when reported; otherwise null.</summary>
    public string? Handshake { get; init; }

    /// <summary>When the serial cache entry was last written, if reported.</summary>
    public DateTimeOffset? SerialReportedAtUtc { get; init; }

    /// <summary>True when a SetBaud request is outstanding for this connector session.</summary>
    public bool BaudRequestPending { get; init; }

    /// <summary>Requested baud rate of the outstanding SetBaud, if any.</summary>
    public int? PendingBaudRate { get; init; }
}
