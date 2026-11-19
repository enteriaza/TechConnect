// -----------------------------------------------------------------------------
// Project: TechConnect.Versioning.Tests
// File:    VersioningTests.cs
// -----------------------------------------------------------------------------
// Verifies the central 1.1.YYDDD UTC date versioning policy via MSBuild.
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using FluentAssertions;

namespace TechConnect.Versioning.Tests;

public sealed class VersioningTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ProtocolProject =
        Path.Combine(RepoRoot, "src", "TechConnect.Protocol", "TechConnect.Protocol.csproj");
    private static readonly string ClientProject =
        Path.Combine(RepoRoot, "src", "TechConnect.Client", "TechConnect.Client.csproj");
    private static readonly string ServerProject =
        Path.Combine(RepoRoot, "src", "TechConnect.Server", "TechConnect.Server.csproj");

    [Theory]
    [InlineData("2026-11-19", "26323")]
    [InlineData("2027-01-01", "27001")]
    [InlineData("2024-02-29", "24060")]
    public void UtcDate_MapsToExpectedYyDdd(string utcDate, string expectedBuildNumber)
    {
        FormatBuildNumber(DateTime.Parse(utcDate + "T12:00:00Z", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal))
            .Should().Be(expectedBuildNumber);
    }

    [Fact]
    public void SameUtcDay_SharesBuildNumber_DifferentDaysDiffer()
    {
        var morning = new DateTime(2026, 11, 19, 0, 5, 0, DateTimeKind.Utc);
        var evening = new DateTime(2026, 11, 19, 23, 55, 0, DateTimeKind.Utc);
        var nextDay = new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);

        FormatBuildNumber(morning).Should().Be(FormatBuildNumber(evening));
        FormatBuildNumber(morning).Should().NotBe(FormatBuildNumber(nextDay));
    }

    [Fact]
    public void BuildNumber_UsesUtc_NotLocalCalendarDay()
    {
        // 19 Nov 2026 22:30 UTC is still day 323 UTC, even if local is already the 20th in UTC+2.
        var utc = new DateTime(2026, 11, 19, 22, 30, 0, DateTimeKind.Utc);
        FormatBuildNumber(utc).Should().Be("26323");
        FormatBuildNumber(utc.ToLocalTime()).Should().Be("26323");
    }

    [Fact]
    public void ExplicitBuildNumber_AppliesToAllProjects_WithStableAssemblyVersion()
    {
        const string build = "26323";
        var expected = $"1.1.{build}";

        BuildProject(ProtocolProject, build);
        BuildProject(ClientProject, build);
        BuildProject(ServerProject, build);

        AssertMetadata(FindOutput(ProtocolProject, "net10.0", "TechConnect.Protocol.dll"), expected);
        AssertMetadata(FindOutput(ClientProject, "net10.0-windows", "TechConnect.Client.dll"), expected);
        AssertMetadata(FindOutput(ServerProject, "net10.0", "TechConnect.Server.dll"), expected);
    }

    [Fact]
    public void DefaultBuild_MatchesCurrentUtcDay_AndExcludesGitMetadata()
    {
        var expectedBuild = FormatBuildNumber(DateTime.UtcNow);
        var expected = $"1.1.{expectedBuild}";

        BuildProject(
            ProtocolProject,
            buildNumber: null,
            extraArgs:
            [
                "-p:SourceRevisionId=deadbeefcafebabe",
                "-p:InformationalVersionSuffix=gdeadbeef",
                "-p:RepositoryBranch=feature/fake",
            ]);

        var meta = ReadMetadata(FindOutput(ProtocolProject, "net10.0", "TechConnect.Protocol.dll"));
        meta.InformationalVersion.Should().Be(expected);
        meta.FileVersion.Should().Be($"{expected}.0");
        meta.AssemblyVersion.Should().Be(new Version(1, 1, 0, 0));
        meta.InformationalVersion.Should().NotContain("deadbeef");
        meta.InformationalVersion.Should().NotContain("+g");
        meta.InformationalVersion.Should().NotContain("feature");
    }

    [Fact]
    public void Deterministic_RemainsEnabled()
    {
        var value = GetMsBuildProperty(ProtocolProject, "Deterministic");
        value.Should().Be("true");
    }

    private static string FormatBuildNumber(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc
            ? timestamp
            : timestamp.ToUniversalTime();
        return string.Create(CultureInfo.InvariantCulture, $"{utc:yy}{utc.DayOfYear:D3}");
    }

    private static void AssertMetadata(string path, string expectedVersion)
    {
        var meta = ReadMetadata(path);
        meta.AssemblyVersion.Should().Be(new Version(1, 1, 0, 0));
        meta.FileVersion.Should().Be($"{expectedVersion}.0");
        meta.InformationalVersion.Should().Be(expectedVersion);
        meta.ProductVersion.Should().Be(expectedVersion);
    }

    private static void BuildProject(string projectPath, string? buildNumber, string[]? extraArgs = null)
    {
        var args = new List<string>
        {
            "build",
            projectPath,
            "-c", "Release",
            "-v", "q",
            "--nologo",
            "-p:IncludeSourceRevisionInInformationalVersion=false",
        };

        if (buildNumber is not null)
        {
            args.Add($"-p:TechConnectBuildNumber={buildNumber}");
        }

        if (extraArgs is not null)
        {
            args.AddRange(extraArgs);
        }

        RunDotNet(args.ToArray());
    }

    private static string GetMsBuildProperty(string projectPath, string propertyName)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"msbuild \"{projectPath}\" -nologo -getProperty:{propertyName} -p:Configuration=Release",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"msbuild -getProperty failed: {stderr}\n{stdout}");
        return stdout.Trim();
    }

    private static void RunDotNet(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"dotnet {string.Join(' ', args)} failed:\n{stderr}\n{stdout}");
    }

    private static string FindOutput(string projectPath, string tfm, string fileName)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var path = Path.Combine(projectDir, "bin", "Release", tfm, fileName);
        File.Exists(path).Should().BeTrue($"expected output at {path}");
        return path;
    }

    private static AssemblyMetadata ReadMetadata(string path)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(path);
        var assemblyName = AssemblyName.GetAssemblyName(path);
        var informational = ReadInformationalFromAssemblyInfoNearby(path)
            ?? fileVersion.ProductVersion
            ?? string.Empty;

        return new AssemblyMetadata(
            assemblyName.Version ?? new Version(0, 0),
            fileVersion.FileVersion ?? string.Empty,
            informational,
            fileVersion.ProductVersion ?? string.Empty);
    }

    private static string? ReadInformationalFromAssemblyInfoNearby(string assemblyPath)
    {
        // Prefer the generated AssemblyInfo when present (avoids loading the assembly).
        var dir = Path.GetDirectoryName(assemblyPath)!;
        var objCandidates = Directory.GetFiles(
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "obj", "Release")),
            "*AssemblyInfo.cs",
            SearchOption.AllDirectories);

        foreach (var candidate in objCandidates)
        {
            if (!candidate.Contains(Path.GetFileNameWithoutExtension(assemblyPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var line in File.ReadLines(candidate))
            {
                const string prefix = "AssemblyInformationalVersionAttribute(\"";
                var idx = line.IndexOf(prefix, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                var start = idx + prefix.Length;
                var end = line.IndexOf('"', start);
                if (end > start)
                {
                    return line[start..end];
                }
            }
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TechConnect.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private sealed record AssemblyMetadata(
        Version AssemblyVersion,
        string FileVersion,
        string InformationalVersion,
        string ProductVersion);
}
