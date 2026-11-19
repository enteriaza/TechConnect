using System.Security.Cryptography.X509Certificates;

namespace TechConnect.Client.DevCerts;

internal static class CertificateStoreAccess
{
    public static string NormalizeThumbprint(string thumbprint) =>
        thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    public static X509Certificate2? FindByThumbprint(
        StoreName storeName,
        StoreLocation location,
        string thumbprint,
        bool requirePrivateKey)
    {
        var normalized = NormalizeThumbprint(thumbprint);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, normalized, validOnly: false);
        var candidates = matches
            .OfType<X509Certificate2>()
            .Where(c => string.Equals(
                NormalizeThumbprint(c.Thumbprint ?? string.Empty),
                normalized,
                StringComparison.OrdinalIgnoreCase))
            .Where(c => !requirePrivateKey || c.HasPrivateKey)
            .ToArray();

        if (candidates.Length == 0)
        {
            return null;
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple certificates match thumbprint {normalized[..8]}… in {location}\\{storeName}.");
        }

        // Duplicate so the store can close without invalidating the handle.
        return new X509Certificate2(candidates[0]);
    }

    public static void Install(StoreName storeName, StoreLocation location, X509Certificate2 certificate)
    {
        using var store = new X509Store(storeName, location);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    public static bool IsTrustedInRoot(string caThumbprint)
    {
        using var found = FindByThumbprint(StoreName.Root, StoreLocation.CurrentUser, caThumbprint, requirePrivateKey: false);
        return found is not null;
    }
}
