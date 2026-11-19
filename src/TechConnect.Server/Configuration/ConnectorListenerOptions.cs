// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConnectorListenerOptions.cs
//
// Purpose:
// Defines connector TLS listener bind settings, Windows certificate store
// selection, and mutual-TLS client certificate validation options.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Server.Configuration;

/// <summary>
/// Options for the TechConnect.Client TLS listener that accepts connector sessions.
/// </summary>
/// <remarks>
/// Bound under <c>TechConnectServer:Connector</c>. When <see cref="Enabled"/> is
/// false, connector-related validation is skipped so SSH-only deployments are not
/// blocked by missing TLS configuration.
/// </remarks>
public sealed class ConnectorListenerOptions
{
    /// <summary>
    /// When <see langword="true"/>, the connector TLS listener starts (requires certificate configuration).
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/> so SSH-only deployments and tests are not
    /// blocked by missing TLS config. Enable explicitly once
    /// <see cref="ServerCertificateThumbprint"/> is set.
    /// </remarks>
    /// <value><see langword="false"/> by default.</value>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Bind address for the connector TLS listener (for example <c>0.0.0.0</c> or <c>127.0.0.1</c>).
    /// </summary>
    /// <value>Defaults to <c>0.0.0.0</c>.</value>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// TCP port for the connector TLS listener.
    /// </summary>
    /// <value>Defaults to 9443. Validated to 1–65535 when enabled.</value>
    public int Port { get; set; } = 9443;

    /// <summary>
    /// Thumbprint of the TLS server certificate in the Windows store (never a private key).
    /// </summary>
    /// <value>Empty by default; required as 40- or 64-character hex when the listener is enabled.</value>
    public string ServerCertificateThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Windows X509 store name that holds the server certificate.
    /// </summary>
    /// <value>Defaults to <c>My</c>.</value>
    public string ServerCertificateStoreName { get; set; } = "My";

    /// <summary>
    /// Windows X509 store location for the server certificate.
    /// </summary>
    /// <value>Defaults to <see cref="StoreLocation.CurrentUser"/>.</value>
    public StoreLocation ServerCertificateStoreLocation { get; set; } = StoreLocation.CurrentUser;

    /// <summary>
    /// Optional exact subject match (for example the development TCP/mTLS server subject
    /// <c>CN=TechConnector Development WSS</c>).
    /// </summary>
    /// <value>Empty means no subject constraint.</value>
    public string ExpectedServerSubject { get; set; } = string.Empty;

    /// <summary>
    /// Optional exact issuer match (for example <c>CN=TechConnector Development CA</c>).
    /// </summary>
    /// <value>Empty means no issuer constraint.</value>
    public string ExpectedServerIssuer { get; set; } = string.Empty;

    /// <summary>
    /// Mutual-TLS client certificate validation settings.
    /// </summary>
    public ClientCertificateValidationOptions ClientCertificateValidation { get; set; } = new();

    /// <summary>
    /// Maximum concurrent connector TCP/TLS connections.
    /// </summary>
    /// <value>Defaults to 100; must be at least 1 when the listener is enabled.</value>
    public int MaximumConcurrentConnections { get; set; } = 100;

    /// <summary>
    /// Handshake timeout in seconds for new connector connections.
    /// </summary>
    /// <value>Defaults to 30; must be at least 1 when the listener is enabled.</value>
    public int HandshakeTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Graceful shutdown timeout in seconds for the connector listener.
    /// </summary>
    /// <value>Defaults to 10; must be at least 1 when the listener is enabled.</value>
    public int ShutdownTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Client certificate requirements for connector mutual TLS.
/// </summary>
public sealed class ClientCertificateValidationOptions
{
    /// <summary>
    /// When <see langword="true"/>, connector clients must present a certificate (required at startup when the listener is enabled).
    /// </summary>
    /// <remarks>
    /// Startup validation requires this to be <see langword="true"/> when the connector listener is enabled.
    /// </remarks>
    /// <value>Defaults to <see langword="true"/>.</value>
    public bool RequireClientCertificate { get; set; } = true;

    /// <summary>
    /// Optional CA thumbprints that must appear in the client certificate chain.
    /// </summary>
    /// <remarks>
    /// When empty, the system trust store is used for chain building.
    /// </remarks>
    /// <value>Empty list by default.</value>
    public List<string> AllowedCertificateAuthorities { get; set; } = [];
}
