// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshWindowChangeParser.cs
//
// Purpose:
// Parses SSH window-change channel-request payloads into column/row dimensions
// via typed ConvertTo of WindowChangeRequestMessage.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.Messages;

namespace TechConnect.Server.Ssh;

/// <summary>
/// Parses SSH <c>window-change</c> channel-request payloads via typed ConvertTo.
/// </summary>
internal static class SshWindowChangeParser
{
    /// <summary>
    /// Attempts to read columns and rows from a channel-request message.
    /// </summary>
    /// <param name="request">Channel request (expected request type <c>window-change</c>).</param>
    /// <param name="columns">Parsed column count when successful.</param>
    /// <param name="rows">Parsed row count when successful.</param>
    /// <returns>
    /// <see langword="true"/> when the request type matches and both columns and rows are non-zero;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryParse(ChannelRequestMessage request, out int columns, out int rows)
    {
        columns = 0;
        rows = 0;
        if (request is null)
        {
            return false;
        }

        if (!string.Equals(request.RequestType, "window-change", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var typed = request as WindowChangeRequestMessage ??
                        request.ConvertTo<WindowChangeRequestMessage>(copy: true);
            if (typed.Columns == 0 || typed.Rows == 0)
            {
                return false;
            }

            columns = (int)typed.Columns;
            rows = (int)typed.Rows;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
