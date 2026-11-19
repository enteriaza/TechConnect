// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    SshWindowChangeParserTests.cs
//
// Purpose:
// Regression tests for TechConnect.Server SSH/CLI behaviour (SshWindowChangeParserTests).
//
// -----------------------------------------------------------------------------
using System.Text;
using FluentAssertions;
using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;
using TechConnect.Server.Ssh;

namespace TechConnect.Server.Tests.Ssh;

/// <summary>
/// Regression tests for SSH window-change payload parsing.
/// </summary>
public sealed class SshWindowChangeParserTests
{
    /// <summary>
    /// Verifies: Try Parse Valid Window Change Reads Columns And Rows.
    /// </summary>
    [Fact]
    public void TryParse_ValidWindowChange_ReadsColumnsAndRows()
    {
        var request = CreateChannelRequest(120, 40, 800, 600);
        SshWindowChangeParser.TryParse(request, out var cols, out var rows).Should().BeTrue();
        cols.Should().Be(120);
        rows.Should().Be(40);
    }

    /// <summary>
    /// Verifies: Try Parse Wrong Request Type Returns False.
    /// </summary>
    [Fact]
    public void TryParse_WrongRequestType_ReturnsFalse()
    {
        var writer = new SshDataWriter();
        writer.Write((byte)98);
        writer.Write((uint)1);
        writer.Write("pty-req", Encoding.ASCII);
        writer.Write(false);
        var request = new ChannelRequestMessage();
        var reader = new SshDataReader(writer.ToBuffer());
        request.Read(ref reader);
        SshWindowChangeParser.TryParse(request, out _, out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies: Try Update Dimensions Rejects Invalid Keeps Previous.
    /// </summary>
    [Fact]
    public void TryUpdateDimensions_RejectsInvalid_KeepsPrevious()
    {
        var info = new SshTerminalInfo("xterm", 80, 24);
        info.TryUpdateDimensions(0, 24).Should().BeFalse();
        info.Columns.Should().Be(80);
        info.TryUpdateDimensions(100, -1).Should().BeFalse();
        info.Rows.Should().Be(24);
        info.TryUpdateDimensions(132, 43).Should().BeTrue();
        info.Columns.Should().Be(132);
        info.Rows.Should().Be(43);
    }

    /// <summary>
    /// Verifies: Default Dimensions Are80x24.
    /// </summary>
    [Fact]
    public void DefaultDimensions_Are80x24()
    {
        var info = new SshTerminalInfo(null, 0, 0);
        info.Columns.Should().Be(80);
        info.Rows.Should().Be(24);
    }
    /// <summary>
    /// Performs create channel request.
    /// </summary>
    /// <param name="columns">The columns.</param>
    /// <param name="rows">The rows.</param>
    /// <param name="pixelWidth">The pixel width.</param>
    /// <param name="pixelHeight">The pixel height.</param>
    /// <returns>The operation result.</returns>

    private static ChannelRequestMessage CreateChannelRequest(
        uint columns,
        uint rows,
        uint pixelWidth,
        uint pixelHeight)
    {
        var writer = new SshDataWriter();
        writer.Write((byte)98); // SSH_MSG_CHANNEL_REQUEST
        writer.Write((uint)1);
        writer.Write("window-change", Encoding.ASCII);
        writer.Write(false);
        writer.Write(columns);
        writer.Write(rows);
        writer.Write(pixelWidth);
        writer.Write(pixelHeight);

        var request = new ChannelRequestMessage();
        var reader = new SshDataReader(writer.ToBuffer());
        request.Read(ref reader);
        return request;
    }
}
