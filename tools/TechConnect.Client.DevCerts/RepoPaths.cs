namespace TechConnect.Client.DevCerts;

internal static class RepoPaths
{
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TechConnect.slnx")) ||
                Directory.Exists(Path.Combine(dir.FullName, "src", "TechConnect.Client")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // tools/TechConnect.Client.DevCerts/bin/Debug/net10.0-windows → repo root is ../../../../..
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    public static string RegistryPath(string repoRoot) =>
        Path.Combine(repoRoot, "tools", "TechConnect.Client.DevCerts", DevCertIdentities.RegistryFileName);

    public static string TestCompanionPath(string repoRoot) =>
        Path.Combine(repoRoot, "tests", "TechConnect.Client.Tests", DevCertIdentities.TestCompanionFileName);

    public static string AppsettingsDevelopmentPath(string repoRoot) =>
        Path.Combine(repoRoot, "src", "TechConnect.Client", "appsettings.Development.json");
}
