// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyUrlRedactionTests.cs
//
// Purpose:
// Verifies URI password masking, copy CLI line sanitization, and percent-encoded
// userinfo extraction without leaking cleartext credentials.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Unit tests for <see cref="CopyUrlRedaction"/> credential redaction and extraction.
/// </summary>
public sealed class CopyUrlRedactionTests
{
    /// <summary>
    /// Verifies that passwords in absolute URIs are replaced with <c>****</c>.
    /// </summary>
    /// <param name="input">URI that may contain userinfo credentials.</param>
    /// <param name="expected">Expected redacted form.</param>
    [Theory]
    [InlineData(
        "https://user:secret@host/path",
        "https://user:****@host/path")]
    [InlineData(
        "scp://admin:p%40ss@192.0.2.10/firmware.bin",
        "scp://admin:****@192.0.2.10/firmware.bin")]
    [InlineData("http://host/file", "http://host/file")]
    public void RedactUri_MasksPassword(string input, string expected)
    {
        CopyUrlRedaction.RedactUri(input).Should().Be(expected);
    }

    /// <summary>
    /// Verifies that a <c>copy</c> command line redacts URL passwords but keeps destination tokens.
    /// </summary>
    [Fact]
    public void SanitizeCommandLine_RedactsCopyUrlCredentials()
    {
        var line = "copy https://alice:s3cret@firmware.example.com/a.bin serial:abc123";
        var sanitized = CopyUrlRedaction.SanitizeCommandLine(line);
        sanitized.Should().Contain("alice:****@");
        sanitized.Should().NotContain("s3cret");
        sanitized.Should().Contain("serial:abc123");
    }

    /// <summary>
    /// Verifies percent-encoded passwords are decoded into memory while the URI without userinfo is clean.
    /// </summary>
    [Fact]
    public void TryExtractUserInfo_PercentEncodedPassword()
    {
        var ok = CopyUrlRedaction.TryExtractUserInfo(
            "https://user:p%40ss%3Aword@host/path",
            out var without,
            out var user,
            out var pass);
        ok.Should().BeTrue();
        without.Should().Be("https://host/path");
        user.Should().Be("user");
        pass.Should().Be("p@ss:word");
    }
}
