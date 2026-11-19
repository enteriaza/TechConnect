// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusAuthenticationOptions.cs
//
// Purpose:
// Defines RADIUS password authentication options including servers, timeouts,
// attempt budget, NAS identity, and local fallback behaviour.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Configuration;

/// <summary>
/// RADIUS Access-Request password authentication options for SSH operators.
/// </summary>
/// <remarks>
/// Privilege is obtained from Cisco-AVPair <c>shell:priv-lvl</c> on Access-Accept
/// (fail-closed). Bound under <c>TechConnectServer:Authentication:Radius</c>.
/// Never log <see cref="RadiusServerOptions.SharedSecret"/>.
/// </remarks>
public sealed class RadiusAuthenticationOptions
{
    /// <summary>
    /// Master switch for RADIUS password authentication under <c>TechConnectServer:Authentication:Radius</c>.
    /// </summary>
    /// <value><see langword="false"/> by default.</value>
    public bool Enabled { get; set; }

    /// <summary>
    /// When <see langword="true"/>, local Identity V3 password verification is attempted
    /// only after every configured RADIUS server returns a genuine Unavailable outcome.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="Enabled"/> to be <see langword="true"/>. Reject, InvalidResponse,
    /// and Cancelled outcomes do not trigger fallback.
    /// </remarks>
    public bool LocalFallbackEnabled { get; set; }

    /// <summary>
    /// Primary RADIUS authentication server (required when RADIUS is enabled).
    /// </summary>
    public RadiusServerOptions PrimaryServer { get; set; } = new();

    /// <summary>
    /// Optional secondary RADIUS authentication server.
    /// </summary>
    /// <value><see langword="null"/> or an unconfigured address omits the secondary server.</value>
    public RadiusServerOptions? SecondaryServer { get; set; }

    /// <summary>
    /// Per-attempt timeout for a single server exchange, in milliseconds.
    /// </summary>
    /// <value>Defaults to 3000. Validated to 100–120000 when RADIUS is enabled.</value>
    public int TimeoutMilliseconds { get; set; } = 3000;

    /// <summary>
    /// Maximum send attempts against an individual server.
    /// </summary>
    /// <remarks>
    /// Retries apply only to Unavailable (timeout/transport) outcomes — never Reject,
    /// InvalidResponse, or Cancelled. Total worst-case wait ≈
    /// <see cref="MaxAttempts"/> × <see cref="TimeoutMilliseconds"/> per server.
    /// </remarks>
    /// <value>Defaults to 2. Validated to 1–5 when RADIUS is enabled.</value>
    public int MaxAttempts { get; set; } = 2;

    /// <summary>
    /// NAS-Identifier attribute value sent with Access-Request.
    /// </summary>
    /// <value>Defaults to <c>techconnect</c>. Required and at most 253 characters when RADIUS is enabled.</value>
    public string NasIdentifier { get; set; } = "techconnect";

    /// <summary>
    /// Optional dotted-quad or IP literal for NAS-IP-Address.
    /// </summary>
    /// <value><see langword="null"/> or empty omits the attribute.</value>
    public string? NasIpAddress { get; set; }
}

/// <summary>
/// Address, port, and shared secret for one RADIUS authentication server.
/// </summary>
public sealed class RadiusServerOptions
{
    /// <summary>
    /// RADIUS server host name or IP address.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// RADIUS authentication UDP port.
    /// </summary>
    /// <value>Defaults to 1812.</value>
    public int Port { get; set; } = 1812;

    /// <summary>
    /// RADIUS shared secret.
    /// </summary>
    /// <remarks>Never log this value.</remarks>
    public string SharedSecret { get; set; } = string.Empty;
}
