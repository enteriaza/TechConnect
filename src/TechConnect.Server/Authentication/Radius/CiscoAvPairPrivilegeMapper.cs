// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    CiscoAvPairPrivilegeMapper.cs
//
// Purpose:
// Extracts Cisco-AVPair shell:priv-lvl privilege from RADIUS Vendor-Specific
// attributes, failing closed on missing, malformed, or conflicting values.
//
// -----------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Authentication.Radius;

/// <summary>
/// Extracts Cisco-AVPair <c>shell:priv-lvl</c> from RADIUS Vendor-Specific attributes (Vendor-Id 9).
/// </summary>
/// <remarks>
/// Fail-closed: missing, malformed, out-of-range, or conflicting values reject authentication.
/// Filter-Id is ignored and never grants privilege. Instances are stateless and thread-safe.
/// </remarks>
internal sealed class CiscoAvPairPrivilegeMapper
{
    /// <summary>
    /// Cisco enterprise Vendor-Id carried in Vendor-Specific attributes.
    /// </summary>
    public const uint CiscoVendorId = 9;

    /// <summary>
    /// Cisco-AVPair vendor type within a Cisco VSA.
    /// </summary>
    public const byte CiscoAvPairVendorType = 1;

    /// <summary>
    /// Case-insensitive prefix of the privilege AVPair value.
    /// </summary>
    public const string ShellPrivLvlPrefix = "shell:priv-lvl=";

    /// <summary>
    /// Attempts to map privilege from Cisco-AVPair attributes on an Access-Accept packet.
    /// </summary>
    /// <param name="packet">RADIUS response packet to inspect.</param>
    /// <param name="privilege">Mapped privilege when the method returns <see langword="true"/>.</param>
    /// <param name="failureReason">Operator-safe reason when mapping fails; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a single consistent in-range priv-lvl was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="packet"/> is <see langword="null"/>.</exception>
    public bool TryMap(RadiusPacket packet, out PrivilegeLevel privilege, out string? failureReason)
    {
        privilege = default;
        failureReason = null;
        ArgumentNullException.ThrowIfNull(packet);

        var vsas = packet.GetAttributes(RadiusAttributeType.VendorSpecific);
        if (vsas.Count == 0)
        {
            failureReason = "Access-Accept is missing Cisco Vendor-Specific (Cisco-AVPair) privilege information.";
            return false;
        }

        PrivilegeLevel? resolved = null;
        var sawCiscoAvPair = false;
        var sawPrivLvlAttribute = false;

        foreach (var attribute in vsas)
        {
            if (!TryEnumerateCiscoAvPairs(attribute.Value, out var pairs, out var parseError))
            {
                failureReason = parseError;
                return false;
            }

            if (pairs.Count > 0)
            {
                sawCiscoAvPair = true;
            }

            foreach (var pair in pairs)
            {
                if (!pair.Trim().StartsWith(ShellPrivLvlPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                sawPrivLvlAttribute = true;
                if (!TryParsePrivLvlValue(pair, out var level, out var valueError))
                {
                    failureReason = valueError;
                    return false;
                }

                if (resolved is null)
                {
                    resolved = level;
                    continue;
                }

                if (resolved.Value != level)
                {
                    failureReason = "Access-Accept contains conflicting shell:priv-lvl privilege values.";
                    return false;
                }
            }
        }

        if (!sawCiscoAvPair)
        {
            failureReason = "Access-Accept is missing Cisco-AVPair (Vendor-Id 9, type 1).";
            return false;
        }

        if (!sawPrivLvlAttribute || resolved is null)
        {
            failureReason = "Access-Accept Cisco-AVPair does not contain shell:priv-lvl.";
            return false;
        }

        privilege = resolved.Value;
        return true;
    }

    /// <summary>
    /// Enumerates Cisco-AVPair UTF-8 strings from a Vendor-Specific attribute value.
    /// </summary>
    /// <param name="value">Raw VSA value including Vendor-Id.</param>
    /// <param name="pairs">Collected AVPair strings (may be empty for non-Cisco VSAs).</param>
    /// <param name="error">Parse error when the method returns <see langword="false"/>.</param>
    /// <returns>
    /// <see langword="true"/> when the attribute was skipped or parsed successfully;
    /// <see langword="false"/> on truncated/invalid Cisco sub-attributes.
    /// </returns>
    private static bool TryEnumerateCiscoAvPairs(
        ReadOnlySpan<byte> value,
        out List<string> pairs,
        out string? error)
    {
        pairs = [];
        error = null;

        if (value.Length < 6)
        {
            return true;
        }

        var vendorId = BinaryPrimitives.ReadUInt32BigEndian(value);
        if (vendorId != CiscoVendorId)
        {
            return true;
        }

        var remaining = value[4..];
        while (remaining.Length > 0)
        {
            if (remaining.Length < 2)
            {
                error = "Access-Accept contains a truncated Cisco Vendor-Specific attribute.";
                return false;
            }

            var vendorType = remaining[0];
            var vendorLength = remaining[1];
            if (vendorLength < 2 || vendorLength > remaining.Length)
            {
                error = "Access-Accept contains an invalid Cisco Vendor-Specific sub-attribute length.";
                return false;
            }

            var subValue = remaining.Slice(2, vendorLength - 2);
            if (vendorType == CiscoAvPairVendorType)
            {
                pairs.Add(Encoding.UTF8.GetString(subValue));
            }

            remaining = remaining[vendorLength..];
        }

        return true;
    }

    /// <summary>
    /// Parses the numeric privilege from a <c>shell:priv-lvl=</c> AVPair string.
    /// </summary>
    /// <param name="avPair">Full AVPair text beginning with <see cref="ShellPrivLvlPrefix"/>.</param>
    /// <param name="level">Parsed privilege when successful.</param>
    /// <param name="error">Failure reason when unsuccessful.</param>
    /// <returns><see langword="true"/> when the value is canonical decimal text in range.</returns>
    private static bool TryParsePrivLvlValue(string avPair, out PrivilegeLevel level, out string? error)
    {
        level = default;
        error = null;

        var trimmed = avPair.Trim();
        var numberText = trimmed[ShellPrivLvlPrefix.Length..].Trim();
        if (numberText.Length == 0)
        {
            error = "Access-Accept shell:priv-lvl value is empty.";
            return false;
        }

        if (!int.TryParse(numberText, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) ||
            !string.Equals(numberText, numeric.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            error = $"Access-Accept shell:priv-lvl value '{numberText}' is malformed.";
            return false;
        }

        if (!PrivilegeLevel.IsValid(numeric))
        {
            error =
                $"Access-Accept shell:priv-lvl={numeric} is out of range " +
                $"({PrivilegeLevel.Minimum}-{PrivilegeLevel.Maximum}).";
            return false;
        }

        level = PrivilegeLevel.FromInt(numeric);
        return true;
    }
}
