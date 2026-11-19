namespace TechConnect.Client.DevCerts;

internal static class DevCertIdentities
{
    public const string CaSubject = "CN=TechConnector Development CA";
    public const string ServerSubject = "CN=TechConnector Development WSS";
    public const string ClientSubject = "CN=TechConnector Development Client";

    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    public static readonly TimeSpan Validity = TimeSpan.FromDays(3650); // 10 years

    public const string RegistryFileName = "dev-certs.local.json";
    public const string TestCompanionFileName = "dev-certs.json";
}
