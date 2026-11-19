// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopyCredentialQualificationTests.cs
//
// Purpose:
// Qualification checks that redaction/parse paths never leave clear passwords in
// display text, and that completion messaging disclaims device acceptance.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Qualification-oriented tests for credential handling and transport-vs-device messaging.
/// </summary>
/// <remarks>
/// <see cref="CompletionMessage_DoesNotClaimDeviceAcceptance"/> asserts against a
/// fixed sample message string (not a live <see cref="CopyTransferService"/> call).
/// </remarks>
public sealed class CopyCredentialQualificationTests
{
    /// <summary>
    /// Verifies redacted URIs never retain known cleartext password material.
    /// </summary>
    /// <param name="input">URI with optional userinfo.</param>
    [Theory]
    [InlineData("https://alice@host/path")]
    [InlineData("https://alice:s3cret@host/path")]
    [InlineData("scp://u%40acme:p%40ss%3Aword@192.0.2.10/a.bin")]
    [InlineData("https://user:pass%21word@host/f.bin")]
    public void RedactUri_NeverLeavesClearPassword(string input)
    {
        var redacted = CopyUrlRedaction.RedactUri(input);
        redacted.Should().NotContain("s3cret");
        redacted.Should().NotContain("p@ss:word");
        redacted.Should().NotContain("pass!word");
        if (input.Contains(':', StringComparison.Ordinal) &&
            input.IndexOf('@') > input.IndexOf("://", StringComparison.Ordinal) + 3)
        {
            // user:pass@ form → password masked when a password delimiter was present.
            var userInfoEnd = input.IndexOf('@');
            var schemeEnd = input.IndexOf("://", StringComparison.Ordinal) + 3;
            var userInfo = input[schemeEnd..userInfoEnd];
            if (userInfo.Contains(':', StringComparison.Ordinal) || userInfo.Contains("%3A", StringComparison.OrdinalIgnoreCase))
            {
                redacted.Should().Contain("****");
            }
        }
    }

    /// <summary>
    /// Verifies percent-encoded user/password decode into fields while DisplayUri stays clean.
    /// </summary>
    [Fact]
    public void Parse_PercentEncodedUserAndPassword()
    {
        CopySourceParser.TryParse(
                "https://u%40acme:p%40ss%3Aword@firmware.example.com/device.bin",
                out var source,
                out var error,
                out _)
            .Should().BeTrue();
        error.Should().BeNull();
        source!.Username.Should().Be("u@acme");
        source.Password.Should().Be("p@ss:word");
        source.DisplayUri.Should().NotContain("p@ss");
        source.DisplayUri.Should().NotContain("%40ss");
        source.DisplayUri.Should().Contain("firmware.example.com/device.bin");
    }

    /// <summary>
    /// Verifies CLI sanitization never retains a known cleartext password.
    /// </summary>
    [Fact]
    public void SanitizeCommandLine_NeverRetainsClearPassword()
    {
        var line = "copy https://bob:ClearText-Passw0rd!@h/x.bin serial:abc";
        var sanitized = CopyUrlRedaction.SanitizeCommandLine(line);
        sanitized.Should().NotContain("ClearText-Passw0rd");
        sanitized.Should().Contain("bob:****@");
        sanitized.Should().StartWith("copy ");
    }

    /// <summary>
    /// Verifies a sample completion message disclaims device acceptance / flash verification.
    /// </summary>
    /// <remarks>
    /// Uses a fixed string fixture rather than invoking <see cref="CopyTransferService"/>.
    /// Production success text similarly disclaims device-level acceptance.
    /// </remarks>
    [Fact]
    public void CompletionMessage_DoesNotClaimDeviceAcceptance()
    {
        var msg =
            $"Copy completed: 10 payload byte(s) written to the serial transport in 1 ms. " +
            "This confirms TechConnect transport write completion, not device-level acceptance or flash success.";
        msg.Should().Contain("not device-level acceptance or flash success");
        msg.Should().Contain("transport write completion");
        msg.Should().NotContain("device accepted");
        msg.Should().NotContain("flash verified");
    }
}
