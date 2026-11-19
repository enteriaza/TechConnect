using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class CertificateInspector
{
    public static bool MatchesExpectedIdentity(
        X509Certificate2 certificate,
        string expectedSubject,
        string? expectedIssuer,
        string? requiredEkuOid,
        bool requirePrivateKey,
        bool requireCaBasicConstraints,
        out string reason)
    {
        if (!SubjectsEqual(certificate.Subject, expectedSubject))
        {
            reason = $"Subject mismatch. Expected '{expectedSubject}', found '{certificate.Subject}'.";
            return false;
        }

        if (expectedIssuer is not null && !SubjectsEqual(certificate.Issuer, expectedIssuer))
        {
            reason = $"Issuer mismatch. Expected '{expectedIssuer}', found '{certificate.Issuer}'.";
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            reason = "Certificate is outside its validity period.";
            return false;
        }

        if (requirePrivateKey && !certificate.HasPrivateKey)
        {
            reason = "Private key is missing.";
            return false;
        }

        if (requireCaBasicConstraints)
        {
            var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (basic is null || !basic.CertificateAuthority)
            {
                reason = "CA basic constraints missing or CertificateAuthority=false.";
                return false;
            }
        }

        if (requiredEkuOid is not null && !HasEku(certificate, requiredEkuOid))
        {
            reason = $"Required EKU {requiredEkuOid} is missing.";
            return false;
        }

        reason = "OK";
        return true;
    }

    public static bool HasEku(X509Certificate2 certificate, string oidValue)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            return false;
        }

        foreach (Oid oid in eku.EnhancedKeyUsages)
        {
            if (string.Equals(oid.Value, oidValue, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string DescribeSans(X509Certificate2 certificate)
    {
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (san is null)
        {
            return "(none)";
        }

        try
        {
            return san.Format(true).Replace(Environment.NewLine, "; ", StringComparison.Ordinal);
        }
        catch
        {
            return "(present)";
        }
    }

    public static string DescribeEkus(X509Certificate2 certificate)
    {
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (eku is null)
        {
            return "(none)";
        }

        var values = eku.EnhancedKeyUsages.Cast<Oid>().Select(o => o.Value ?? o.FriendlyName ?? "?").ToArray();
        return values.Length == 0 ? "(none)" : string.Join(", ", values);
    }

    private static bool SubjectsEqual(string left, string right) =>
        string.Equals(NormalizeSubject(left), NormalizeSubject(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSubject(string subject) =>
        string.Join(",", subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
