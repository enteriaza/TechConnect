// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CopySourceParserTests.cs
//
// Purpose:
// Verifies copy source scheme-only completion flags, credential stripping from
// DisplayUri, and serial: destination parsing rules.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Copy;

namespace TechConnect.Server.Tests.Copy;

/// <summary>
/// Unit tests for <see cref="CopySourceParser"/>.
/// </summary>
public sealed class CopySourceParserTests
{
    /// <summary>
    /// Verifies scheme-only tokens parse, with interactive completion required except for modems.
    /// </summary>
    /// <param name="token">Scheme-only token.</param>
    /// <param name="kindValue">Expected <see cref="CopySourceKind"/> ordinal.</param>
    [Theory]
    [InlineData("http:", (int)CopySourceKind.Http)]
    [InlineData("https:", (int)CopySourceKind.Https)]
    [InlineData("tftp:", (int)CopySourceKind.Tftp)]
    [InlineData("scp:", (int)CopySourceKind.Scp)]
    [InlineData("xmodem:", (int)CopySourceKind.Xmodem)]
    [InlineData("ymodem:", (int)CopySourceKind.Ymodem)]
    public void SchemeOnly_NeedsInteractive(string token, int kindValue)
    {
        var kind = (CopySourceKind)kindValue;
        CopySourceParser.TryParse(token, out var source, out var error, out var needs)
            .Should().BeTrue();
        error.Should().BeNull();
        source!.Kind.Should().Be(kind);
        if (kind is CopySourceKind.Xmodem or CopySourceKind.Ymodem)
        {
            needs.Should().BeFalse();
        }
        else
        {
            needs.Should().BeTrue();
        }
    }

    /// <summary>
    /// Verifies HTTPS URLs keep credentials for auth but omit the password from <see cref="CopySourceUri.DisplayUri"/>.
    /// </summary>
    [Fact]
    public void FullHttpsUrl_WithCredentials_StripsPasswordFromDisplay()
    {
        CopySourceParser.TryParse(
                "https://alice:s3cret@firmware.example.com/releases/device.bin",
                out var source,
                out var error,
                out var needs)
            .Should().BeTrue();
        error.Should().BeNull();
        needs.Should().BeFalse();
        source!.Kind.Should().Be(CopySourceKind.Https);
        source.Host.Should().Be("firmware.example.com");
        source.Username.Should().Be("alice");
        source.Password.Should().Be("s3cret");
        source.DisplayUri.Should().NotContain("s3cret");
        source.DisplayUri.Should().Contain("alice@");
    }

    /// <summary>
    /// Verifies destinations must use <c>serial:&lt;session-id&gt;</c> and reject other prefixes.
    /// </summary>
    [Fact]
    public void Destination_MustBeSerialSession()
    {
        CopySourceParser.TryParseDestination("serial:ff530f5b", out var id, out var error)
            .Should().BeTrue();
        id.Should().Be("ff530f5b");
        error.Should().BeNull();

        CopySourceParser.TryParseDestination("flash:", out _, out error).Should().BeFalse();
        error.Should().Contain("serial:");
    }
}
