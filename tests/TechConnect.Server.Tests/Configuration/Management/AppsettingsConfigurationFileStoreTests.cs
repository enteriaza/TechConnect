// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AppsettingsConfigurationFileStoreTests.cs
//
// Purpose:
// Verifies the atomic appsettings.json store: temp-file + replace/move,
// temp cleanup on success and failure, failure modes, cancellation, and the
// fixed content-root path. Uses temporary directories only.
//
// -----------------------------------------------------------------------------

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TechConnect.Server.Configuration.Management;

namespace TechConnect.Server.Tests.Configuration.Management;

/// <summary>
/// Tests for <see cref="AppsettingsConfigurationFileStore"/>.
/// </summary>
public sealed class AppsettingsConfigurationFileStoreTests : IDisposable
{
    /// <summary>Temp content root.</summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-store-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the temp root.</summary>
    public AppsettingsConfigurationFileStoreTests() => Directory.CreateDirectory(_root);

    /// <summary>Target path.</summary>
    private string Target => Path.Combine(_root, "appsettings.json");

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (File.Exists(Target))
            {
                File.SetAttributes(Target, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Temp files left in the content root.</summary>
    private string[] TempFiles() => Directory.GetFiles(_root, "*.tmp");

    /// <summary>The path is always appsettings.json directly under the content root.</summary>
    [Fact]
    public void FilePath_IsFixedUnderContentRoot()
    {
        var store = new AppsettingsConfigurationFileStore(Path.Combine(_root, "sub", ".."));

        store.FilePath.Should().Be(Path.GetFullPath(Target));
    }

    /// <summary>The new content is fully written to a sibling temp file before the target is replaced.</summary>
    [Fact]
    public async Task Write_ExistingTarget_WritesTempThenReplaces_NoTempLeft()
    {
        File.WriteAllText(Target, "{\"old\":true}");
        string? tempSeen = null;
        string? targetAtReplace = null;
        string? tempAtReplace = null;
        var store = new AppsettingsConfigurationFileStore(_root, temp =>
        {
            tempSeen = temp;
            targetAtReplace = File.ReadAllText(Target);
            tempAtReplace = File.ReadAllText(temp);
        });

        await store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{\"new\":true}"), CancellationToken.None);

        Path.GetDirectoryName(tempSeen).Should().Be(_root);
        targetAtReplace.Should().Be("{\"old\":true}");
        tempAtReplace.Should().Be("{\"new\":true}");
        File.ReadAllText(Target).Should().Be("{\"new\":true}");
        TempFiles().Should().BeEmpty();
        Directory.GetFiles(_root).Should().ContainSingle();
    }

    /// <summary>An absent target is created by moving the completed temp file into place.</summary>
    [Fact]
    public async Task Write_AbsentTarget_MovesIntoPlace()
    {
        var store = new AppsettingsConfigurationFileStore(_root);

        await store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        File.ReadAllText(Target).Should().Be("{}");
        TempFiles().Should().BeEmpty();
    }

    /// <summary>A failure before the replace leaves the target untouched and removes the temp file.</summary>
    [Fact]
    public async Task Write_FailureBeforeReplace_TargetUnchanged_TempRemoved()
    {
        File.WriteAllText(Target, "{\"old\":true}");
        var store = new AppsettingsConfigurationFileStore(_root, _ => throw new IOException("injected"));

        var act = () => store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{\"new\":true}"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        File.ReadAllText(Target).Should().Be("{\"old\":true}");
        TempFiles().Should().BeEmpty();
    }

    /// <summary>A target that cannot be replaced (held open exclusively) fails the real File.Replace; target unchanged, temp removed.</summary>
    [Fact]
    public async Task Write_TargetLockedDuringReplace_Fails_TargetUnchanged_TempRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Target, "{\"old\":true}");
        FileStream? holder = null;
        var store = new AppsettingsConfigurationFileStore(_root, _ =>
            holder = new FileStream(Target, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        try
        {
            var act = () => store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{\"new\":true}"), CancellationToken.None);

            var thrown = await act.Should().ThrowAsync<Exception>();
            thrown.Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        }
        finally
        {
            holder?.Dispose();
        }

        File.ReadAllText(Target).Should().Be("{\"old\":true}");
        TempFiles().Should().BeEmpty();
    }

    /// <summary>A read-only target cannot be replaced; the original content survives and no temp file remains.</summary>
    [Fact]
    public async Task Write_ReadOnlyTarget_Fails_TargetUnchanged_TempRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        File.WriteAllText(Target, "{\"old\":true}");
        File.SetAttributes(Target, FileAttributes.ReadOnly);
        var store = new AppsettingsConfigurationFileStore(_root);

        var act = () => store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{\"new\":true}"), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        File.ReadAllText(Target).Should().Be("{\"old\":true}");
        TempFiles().Should().BeEmpty();
    }

    /// <summary>A missing content-root directory fails without creating anything.</summary>
    [Fact]
    public async Task Write_MissingDirectory_Fails_NothingCreated()
    {
        var missing = Path.Combine(_root, "missing");
        var store = new AppsettingsConfigurationFileStore(missing);

        var act = () => store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        await act.Should().ThrowAsync<DirectoryNotFoundException>();
        Directory.Exists(missing).Should().BeFalse();
    }

    /// <summary>Cancellation before the replace leaves the target unchanged and removes the temp file.</summary>
    [Fact]
    public async Task Write_Cancelled_TargetUnchanged_TempRemoved()
    {
        File.WriteAllText(Target, "{\"old\":true}");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var store = new AppsettingsConfigurationFileStore(_root);

        var act = () => store.WriteAtomicallyAsync(Encoding.UTF8.GetBytes("{\"new\":true}"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(Target).Should().Be("{\"old\":true}");
        TempFiles().Should().BeEmpty();
    }

    /// <summary>Through the service, a non-replaceable target reports Failed, leaves file and running state unchanged, and a retry succeeds.</summary>
    [Fact]
    public async Task Service_ReadOnlyTarget_ReportsFailure_ThenRetrySucceeds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ConfigurationFixture();
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigPaths.Set(edit, "ssh", "port", JsonValue.Create(2200L));
        var before = fixture.ReadFile();
        var running = service.RenderRunning();
        File.SetAttributes(fixture.FilePath, FileAttributes.ReadOnly);

        var failed = await service.WriteAsync(edit, CancellationToken.None);

        failed.Status.Should().Be(ConfigurationWriteStatus.Failed);
        string.Join("\n", failed.Messages).Should().Contain("existing file is unchanged");
        fixture.ReadFile().Should().Be(before);
        service.RenderRunning().Should().Be(running);
        Directory.GetFiles(fixture.ContentRoot, "*.tmp").Should().BeEmpty();

        File.SetAttributes(fixture.FilePath, FileAttributes.Normal);
        var retried = await service.WriteAsync(edit, CancellationToken.None);

        retried.Status.Should().Be(ConfigurationWriteStatus.Saved);
        JsonNode.Parse(fixture.ReadFile())!["TechConnectServer"]!["Ssh"]!["Port"]!.GetValue<int>().Should().Be(2200);
    }

    /// <summary>Writing preserves unrelated JSON (unknown sections and unmanaged keys).</summary>
    [Fact]
    public async Task Service_Write_PreservesUnrelatedJson()
    {
        var json = JsonNode.Parse(ConfigurationFixture.ShippedAppsettings())!.AsObject();
        json["CustomSection"] = new JsonObject { ["Keep"] = "me", ["Nested"] = new JsonArray(1, 2, 3) };
        json["TechConnectServer"]!["UnmanagedKey"] = "still-here";
        using var fixture = new ConfigurationFixture(json.ToJsonString());
        var service = fixture.CreateService();
        var edit = service.BeginEdit(ConfigurationFixture.Admin(), out _);
        ConfigPaths.Set(edit, "ssh", "port", JsonValue.Create(2201L));

        (await service.WriteAsync(edit, CancellationToken.None)).Status.Should().Be(ConfigurationWriteStatus.Saved);

        var saved = JsonNode.Parse(fixture.ReadFile())!;
        saved["CustomSection"]!["Keep"]!.GetValue<string>().Should().Be("me");
        saved["CustomSection"]!["Nested"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(1, 2, 3);
        saved["TechConnectServer"]!["UnmanagedKey"]!.GetValue<string>().Should().Be("still-here");
        saved["TechConnectServer"]!["Ssh"]!["Port"]!.GetValue<int>().Should().Be(2201);
    }
}
