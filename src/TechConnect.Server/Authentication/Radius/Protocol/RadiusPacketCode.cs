// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusPacketCode.cs
//
// Purpose:
// RADIUS packet codes required for TechConnect authentication and future
// accounting (RFC 2865 / RFC 2866).
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Protocol;

/// <summary>
/// RADIUS packet codes required for TechConnect authentication and future accounting
/// (RFC 2865 §3, RFC 2866 §3). Derived from enteriaza/RadiusClient @ fd27424.
/// </summary>
internal enum RadiusPacketCode : byte
{
    /// <summary>Access-Request (1).</summary>
    AccessRequest = 1,

    /// <summary>Access-Accept (2).</summary>
    AccessAccept = 2,

    /// <summary>Access-Reject (3).</summary>
    AccessReject = 3,

    /// <summary>Accounting-Request (4); reserved for future accounting use.</summary>
    AccountingRequest = 4,

    /// <summary>Accounting-Response (5); reserved for future accounting use.</summary>
    AccountingResponse = 5
}