// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusAttributeType.cs
//
// Purpose:
// Trimmed set of RADIUS attribute type codes used by TechConnect authentication
// (RFC 2865 / RFC 3579).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Protocol;

/// <summary>
/// Trimmed RADIUS attribute types for TechConnect authentication (RFC 2865 / RFC 3579).
/// Derived from enteriaza/RadiusClient @ fd27424.
/// </summary>
internal enum RadiusAttributeType : byte
{
    /// <summary>User-Name (1).</summary>
    UserName = 1,

    /// <summary>User-Password (2) — PAP-hidden password octets.</summary>
    UserPassword = 2,

    /// <summary>NAS-IP-Address (4).</summary>
    NasIpAddress = 4,

    /// <summary>NAS-Port (5).</summary>
    NasPort = 5,

    /// <summary>Service-Type (6).</summary>
    ServiceType = 6,

    /// <summary>Filter-Id (11); ignored for privilege mapping.</summary>
    FilterId = 11,

    /// <summary>Reply-Message (18).</summary>
    ReplyMessage = 18,

    /// <summary>State (24).</summary>
    State = 24,

    /// <summary>Class (25).</summary>
    Class = 25,

    /// <summary>Vendor-Specific (26); Cisco-AVPair privilege rides here.</summary>
    VendorSpecific = 26,

    /// <summary>NAS-Identifier (32).</summary>
    NasIdentifier = 32,

    /// <summary>NAS-Port-Type (61).</summary>
    NasPortType = 61,

    /// <summary>Message-Authenticator (80) per RFC 3579.</summary>
    MessageAuthenticator = 80
}