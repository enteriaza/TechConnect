// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    RemoteServiceOptions.cs
//
// Purpose:
// Binds RemoteService (and nested reconnect/buffer/certificate) plus development
// certificate identity options from configuration without storing private keys.
//
// -----------------------------------------------------------------------------

using System.Security.Cryptography.X509Certificates;
using TechConnect.Protocol;

namespace TechConnect.Client.Configuration;

/// <summary>
/// Options for connecting TechConnect.Client to TechConnect.Server.
/// </summary>
/// <remarks>
/// Bound under <see cref="SectionName"/>. Configure <see cref="Host"/> + <see cref="Port"/>
/// for the native TCP/mTLS connector. Never place private keys in these options;
/// client identity is a Windows-store thumbprint via <see cref="ClientCertificate"/>.
/// </remarks>
public sealed class RemoteServiceOptions
{
    /// <summary>
    /// Configuration section name: <c>RemoteService</c>.
    /// </summary>
    public const string SectionName = "RemoteService";

    /// <summary>
    /// TechConnect.Server host name or IP for the native TCP/mTLS connector listener.
    /// </summary>
    /// <value>Empty until the server endpoint is configured.</value>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Connector TCP/TLS port.
    /// </summary>
    /// <value>Defaults to 9443. Validated to 1–65535 when <see cref="Host"/> is set.</value>
    public int Port { get; set; } = 9443;

    /// <summary>
    /// Hostname used for TLS server certificate name validation (for example <c>localhost</c>).
    /// </summary>
    /// <remarks>Defaults to <see cref="Host"/> when empty at connect time.</remarks>
    /// <value>Empty means use <see cref="Host"/>.</value>
    public string TlsHostName { get; set; } = string.Empty;

    /// <summary>
    /// Connect/handshake timeout in seconds.
    /// </summary>
    /// <value>Defaults to 30; must be at least 1.</value>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Minimum accepted <see cref="MaximumMessageBytes"/> (1 KiB).
    /// </summary>
    public const int MinimumMaximumMessageBytes = 1024;

    /// <summary>
    /// Hard upper bound for <see cref="MaximumMessageBytes"/> (64 KiB).
    /// </summary>
    /// <remarks>
    /// Aligned with <see cref="ProtocolLimits.DefaultMaxFramePayloadBytes"/> so the Client
    /// cannot accept larger frames than the protocol default ceiling used by TechConnect.Server.
    /// Serial console traffic is chunked well below this; raising it would only increase
    /// inbound allocation exposure on the connector.
    /// </remarks>
    public const int AbsoluteMaximumMessageBytes = ProtocolLimits.DefaultMaxFramePayloadBytes;

    /// <summary>
    /// Default <see cref="MaximumMessageBytes"/> (64 KiB).
    /// </summary>
    public const int DefaultMaximumMessageBytes = ProtocolLimits.DefaultMaxFramePayloadBytes;

    /// <summary>
    /// Maximum serial/control payload size in bytes accepted by the TCP/mTLS transport.
    /// </summary>
    /// <remarks>
    /// Applied directly as <see cref="ProtocolLimits.MaxFramePayloadBytes"/> and
    /// <see cref="ProtocolLimits.MaxSerialDataPayloadBytes"/> for the session.
    /// Must be between <see cref="MinimumMaximumMessageBytes"/> and
    /// <see cref="AbsoluteMaximumMessageBytes"/> inclusive.
    /// </remarks>
    /// <value>Defaults to <see cref="DefaultMaximumMessageBytes"/>.</value>
    public int MaximumMessageBytes { get; set; } = DefaultMaximumMessageBytes;

    /// <summary>
    /// TLS certificate revocation checking mode for the outbound mTLS handshake.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default is <see cref="X509RevocationMode.NoCheck"/>: an explicit field-deployment
    /// policy for connectors that often run on cellular or otherwise CRL/OCSP-unreachable
    /// networks. Chain trust, expiry, and hostname/SAN validation remain enforced by the
    /// platform regardless of this setting.
    /// </para>
    /// <para>
    /// Set <see cref="X509RevocationMode.Offline"/> or <see cref="X509RevocationMode.Online"/>
    /// when deployment PKI and network reachability support revocation checks. When those
    /// modes are selected, revocation check failures fail the TLS handshake; there is no
    /// silent fallback to <see cref="X509RevocationMode.NoCheck"/>.
    /// </para>
    /// </remarks>
    /// <value>Defaults to <see cref="X509RevocationMode.NoCheck"/>.</value>
    public X509RevocationMode CertificateRevocationMode { get; set; } = X509RevocationMode.NoCheck;

    /// <summary>
    /// Client certificate selection for mutual TLS.
    /// </summary>
    public ClientCertificateOptions ClientCertificate { get; set; } = new();

    /// <summary>
    /// Exponential backoff settings for automatic remote reconnect.
    /// </summary>
    public ReconnectOptions Reconnect { get; set; } = new();

    /// <summary>
    /// Serial→remote outbound buffer capacity settings.
    /// </summary>
    public RemoteBufferOptions Buffer { get; set; } = new();
}

/// <summary>
/// Windows certificate store selection for the TechConnect.Client client certificate.
/// </summary>
/// <remarks>
/// Thumbprints only — never a private key. Loaded by
/// <see cref="Abstractions.IClientCertificateProvider"/>.
/// </remarks>
public sealed class ClientCertificateOptions
{
    /// <summary>
    /// Certificate thumbprint (SHA-1 or SHA-256 hex). Never a private key.
    /// </summary>
    /// <value>
    /// Empty means no client certificate is configured. When set, must normalize to
    /// 40 or 64 hex characters.
    /// </value>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Windows X509 store name that holds the client certificate.
    /// </summary>
    /// <value>Defaults to <c>My</c>.</value>
    public string StoreName { get; set; } = "My";

    /// <summary>
    /// Windows X509 store location for the client certificate.
    /// </summary>
    /// <value>Defaults to <see cref="StoreLocation.CurrentUser"/>.</value>
    public StoreLocation StoreLocation { get; set; } = StoreLocation.CurrentUser;

    /// <summary>
    /// Optional expected subject (for example <c>CN=TechConnector Development Client</c>).
    /// </summary>
    /// <value>Empty means no subject constraint. When set, must match exactly (normalized).</value>
    public string ExpectedSubject { get; set; } = string.Empty;

    /// <summary>
    /// Optional expected issuer (for example <c>CN=TechConnector Development CA</c>).
    /// </summary>
    /// <value>Empty means no issuer constraint. When set, must match exactly (normalized).</value>
    public string ExpectedIssuer { get; set; } = string.Empty;
}

/// <summary>
/// Development-only certificate identities (thumbprints and subjects).
/// </summary>
/// <remarks>
/// Bound under <see cref="SectionName"/>. Never contains private keys.
/// Distinct from any future production PKI configuration. Written by local
/// DevCert tooling into ignored Development appsettings; not consumed by the
/// runtime <see cref="Abstractions.IClientCertificateProvider"/> path, which uses
/// <see cref="RemoteServiceOptions.ClientCertificate"/>.
/// </remarks>
public sealed class DevelopmentCertificatesOptions
{
    /// <summary>
    /// Configuration section name: <c>DevelopmentCertificates</c>.
    /// </summary>
    public const string SectionName = "DevelopmentCertificates";

    /// <summary>
    /// Development CA certificate thumbprint (hex).
    /// </summary>
    public string CaThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Development server certificate thumbprint (hex).
    /// </summary>
    public string ServerThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Development client certificate thumbprint (hex).
    /// </summary>
    public string ClientThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Expected development CA subject.
    /// </summary>
    /// <value>Defaults to <c>CN=TechConnector Development CA</c>.</value>
    public string CaSubject { get; set; } = "CN=TechConnector Development CA";

    /// <summary>
    /// Expected development server subject.
    /// </summary>
    /// <value>
    /// Defaults to <c>CN=TechConnector Development WSS</c>
    /// (historical subject of the development TCP/mTLS server certificate; identity unchanged).
    /// </value>
    public string ServerSubject { get; set; } = "CN=TechConnector Development WSS";

    /// <summary>
    /// Expected development client subject.
    /// </summary>
    /// <value>Defaults to <c>CN=TechConnector Development Client</c>.</value>
    public string ClientSubject { get; set; } = "CN=TechConnector Development Client";
}

/// <summary>
/// Exponential backoff parameters for <see cref="Abstractions.IReconnectCoordinator"/>.
/// </summary>
public sealed class ReconnectOptions
{
    /// <summary>
    /// Initial reconnect delay in seconds before the first retry after a failure.
    /// </summary>
    /// <value>Defaults to 2; must be zero or greater.</value>
    public int InitialDelaySeconds { get; set; } = 2;

    /// <summary>
    /// Maximum reconnect delay in seconds after repeated failures.
    /// </summary>
    /// <value>Defaults to 60; must be at least 1 and ≥ <see cref="InitialDelaySeconds"/>.</value>
    public int MaxDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Multiplier applied to the current delay after each failed attempt.
    /// </summary>
    /// <value>Defaults to 2.0; must be at least 1.0.</value>
    public double BackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Fraction of delay used as +/- random jitter (0.2 = ±20%).
    /// </summary>
    /// <value>Defaults to 0.2; must be between 0.0 and 1.0 inclusive.</value>
    public double JitterFactor { get; set; } = 0.2;
}

/// <summary>
/// Capacity limits for the serial→remote outbound buffer.
/// </summary>
public sealed class RemoteBufferOptions
{
    /// <summary>
    /// Maximum queued serial→remote bytes.
    /// </summary>
    /// <remarks>
    /// When exceeded, oldest queued chunks are dropped and counted in
    /// <see cref="Models.RemoteDeliveryStats.BytesDropped"/>.
    /// </remarks>
    /// <value>Defaults to 4 MiB; must be at least 4096.</value>
    public int MaximumQueuedBytes { get; set; } = 4 * 1024 * 1024;
}
