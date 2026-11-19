namespace TechConnect.Client.DevCerts;

/// <summary>
/// Fixed development PKI identity strings. The server subject retains the historical
/// <c>CN=TechConnector Development WSS</c> name so existing certificates and thumbprints stay valid.
/// </summary>
internal static class DevCertIdentities
{
    public const string CaSubject = "CN=TechConnector Development CA";

    /// <summary>
    /// Development TCP/mTLS server certificate subject (historical name; do not change).
    /// </summary>
    public const string ServerSubject = "CN=TechConnector Development WSS";

    public const string ClientSubject = "CN=TechConnector Development Client";

    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static readonly TimeSpan Validity = TimeSpan.FromDays(3650); // 10 years

    public const string RegistryFileName = "dev-certs.local.json";
    public const string TestCompanionFileName = "dev-certs.json";
}
