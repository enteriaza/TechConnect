// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    WindowChangeRequestMessage.cs
//
// Purpose:
// Typed SSH window-change channel request (RFC 4254) for DevTunnels ConvertTo
// re-reading of RawBytes into columns/rows/pixel fields.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.IO;
using Microsoft.DevTunnels.Ssh.Messages;

namespace TechConnect.Server.Ssh;

/// <summary>
/// Typed SSH <c>window-change</c> channel request (RFC 4254).
/// </summary>
/// <remarks>
/// DevTunnels does not ship this type; <c>ChannelRequestMessage.ConvertTo&lt;T&gt;</c>
/// re-reads <c>RawBytes</c> through <see cref="OnRead"/>.
/// </remarks>
internal sealed class WindowChangeRequestMessage : ChannelRequestMessage
{
    /// <summary>
    /// Terminal width in characters.
    /// </summary>
    public uint Columns { get; private set; }

    /// <summary>
    /// Terminal height in rows.
    /// </summary>
    public uint Rows { get; private set; }

    /// <summary>
    /// Terminal width in pixels (may be zero when unknown).
    /// </summary>
    public uint PixelWidth { get; private set; }

    /// <summary>
    /// Terminal height in pixels (may be zero when unknown).
    /// </summary>
    public uint PixelHeight { get; private set; }

    /// <summary>
    /// Reads the base channel-request fields then the four uint32 window-change fields.
    /// </summary>
    /// <param name="reader">SSH data reader positioned at the message body.</param>
    protected override void OnRead(ref SshDataReader reader)
    {
        base.OnRead(ref reader);
        Columns = reader.ReadUInt32();
        Rows = reader.ReadUInt32();
        PixelWidth = reader.ReadUInt32();
        PixelHeight = reader.ReadUInt32();
    }

    /// <summary>
    /// Writes the base channel-request fields then the four uint32 window-change fields.
    /// </summary>
    /// <param name="writer">SSH data writer.</param>
    protected override void OnWrite(ref SshDataWriter writer)
    {
        base.OnWrite(ref writer);
        writer.Write(Columns);
        writer.Write(Rows);
        writer.Write(PixelWidth);
        writer.Write(PixelHeight);
    }
}
