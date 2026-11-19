// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshKeyMaterialTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (SshKeyMaterialTests).
//
// -----------------------------------------------------------------------------
using FluentAssertions;
using TechConnect.Server.Ssh.Authentication;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for SSH key import/export/fingerprint helpers.
/// </summary>
public sealed class SshKeyMaterialTests
{
    /// <summary>
    /// Verifies: Import Host Private Key Loads Valid Pem.
    /// </summary>
    [Fact]
    public void ImportHostPrivateKey_LoadsValidPem()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair("host");
        var pem = SshTestKeys.ExportPrivateKeyPem(key);

        var loaded = SshKeyMaterial.ImportHostPrivateKey(pem);

        loaded.HasPrivateKey.Should().BeTrue();
        SshKeyMaterial.PublicKeysEqual(loaded, key).Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Import Host Private Key Invalid Material Fails Clearly.
    /// </summary>
    [Fact]
    public void ImportHostPrivateKey_InvalidMaterial_FailsClearly()
    {
        var act = () => SshKeyMaterial.ImportHostPrivateKey("this is not a private key");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*invalid*")
            .And.Message.Should().Contain("will not replace");
    }

    /// <summary>
    /// Verifies: Import Host Private Key Empty Fails Clearly.
    /// </summary>
    [Fact]
    public void ImportHostPrivateKey_Empty_FailsClearly()
    {
        var act = () => SshKeyMaterial.ImportHostPrivateKey("  ");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*empty*");
    }

    /// <summary>
    /// Verifies: Load Authorized Public Keys Accepts Valid Keys.
    /// </summary>
    [Fact]
    public void LoadAuthorizedPublicKeys_AcceptsValidKeys()
    {
        var operatorKey = SshTestKeys.CreateEcdsaKeyPair("operator");
        var line = SshTestKeys.ExportPublicKeyLine(operatorKey, "ops");

        var loaded = SshKeyMaterial.LoadAuthorizedPublicKeys([line]);

        loaded.Should().HaveCount(1);
        SshKeyMaterial.PublicKeysEqual(loaded[0].Key, operatorKey).Should().BeTrue();
        loaded[0].Comment.Should().Be("ops");
    }

    /// <summary>
    /// Verifies: Load Authorized Public Keys Empty Fails Closed.
    /// </summary>
    [Fact]
    public void LoadAuthorizedPublicKeys_Empty_FailsClosed()
    {
        var act = () => SshKeyMaterial.LoadAuthorizedPublicKeys([]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no usable public keys*");
    }

    /// <summary>
    /// Verifies: Load Authorized Public Keys Malformed Fails Clearly.
    /// </summary>
    [Fact]
    public void LoadAuthorizedPublicKeys_Malformed_FailsClearly()
    {
        var act = () => SshKeyMaterial.LoadAuthorizedPublicKeys(["not-a-valid-key"]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AuthorizedPublicKeys[0]*");
    }

    /// <summary>
    /// Verifies: Public Keys Equal Ignores Comments.
    /// </summary>
    [Fact]
    public void PublicKeysEqual_IgnoresComments()
    {
        var key = SshTestKeys.CreateEcdsaKeyPair();
        var a = SshKeyMaterial.LoadAuthorizedPublicKeys([SshTestKeys.ExportPublicKeyLine(key, "alice")]);
        var b = SshKeyMaterial.LoadAuthorizedPublicKeys([SshTestKeys.ExportPublicKeyLine(key, "bob")]);

        SshKeyMaterial.PublicKeysEqual(a[0].Key, b[0].Key).Should().BeTrue();
    }

    /// <summary>
    /// Verifies: Generate And Export Round Trips.
    /// </summary>
    [Fact]
    public void GenerateAndExport_RoundTrips()
    {
        var generated = SshKeyMaterial.GenerateHostKeyPair();
        var pem = SshKeyMaterial.ExportPrivateKeyPem(generated);
        var imported = SshKeyMaterial.ImportHostPrivateKey(pem);

        SshKeyMaterial.ComputeFingerprint(imported)
            .Should().Be(SshKeyMaterial.ComputeFingerprint(generated));
    }
}
