// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    RadiusProtocolReferenceVectors.cs
//
// Purpose:
// Regression tests and helpers covering RadiusProtocolReferenceVectors behaviour.
// Fixture: RadiusProtocolReferenceVectors.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Tests.Authentication.Radius;

/// <summary>
/// Independently computed RADIUS protocol vectors (PowerShell / .NET <c>MD5</c> + <c>HMACMD5</c>,
/// not the TechConnect implementation under test).
/// Shared secret ASCII <c>xyzzy5461</c>; request authenticator
/// <c>f3a45c8d9e2b1a0f6748392a1c5d6e7b</c>.
/// </summary>
internal static class RadiusProtocolReferenceVectors
{
    /// <summary>
    /// Shared Secret test helper member.
    /// </summary>
    public static readonly byte[] SharedSecret =
        "xyzzy5461"u8.ToArray();
    /// <summary>
    /// Request Authenticator test helper member.
    /// </summary>

    public static readonly byte[] RequestAuthenticator =
        Convert.FromHexString("f3a45c8d9e2b1a0f6748392a1c5d6e7b");

    /// <summary>PAP encoding of ASCII password <c>arctangent</c> (single 16-byte block).</summary>
    public static readonly byte[] PapArctangent =
        Convert.FromHexString("bdcfb54913afa8e08d9cc800e61acd99");

    /// <summary>PAP encoding of ASCII <c>password-is-long!!</c> (18 bytes → 32-byte padded).</summary>
    public static readonly byte[] PapLongPassword =
        Convert.FromHexString("acdca54e05aebde1ce81bb2d8a75a3fe78355e30748d5c28f52b30d5dff50ec4");

    /// <summary>Response Authenticator for Access-Accept id=1, length=20, no attributes.</summary>
    public static readonly byte[] ResponseAuthenticatorEmpty =
        Convert.FromHexString("dd6c750d4be50cb270a3216addca7722");

    /// <summary>Response Authenticator for Access-Accept with Filter-Id techconnect:Operator.</summary>
    public static readonly byte[] ResponseAuthenticatorWithFilterId =
        Convert.FromHexString("98e3857e06edefdeb430558a2ae9e8c4");
    /// <summary>
    /// Filter Id Attribute Wire test helper member.
    /// </summary>

    public static readonly byte[] FilterIdAttributeWire =
        Convert.FromHexString("0b1674656368636f6e6e6563743a4f70657261746f72");

    /// <summary>Accounting-Request authenticator for User-Name=alice, id=7.</summary>
    public static readonly byte[] AccountingRequestAuthenticator =
        Convert.FromHexString("ddacc5bee8434243ff4444a9fa9b3cf2");

    /// <summary>
    /// HMAC-MD5 Message-Authenticator for Access-Accept id=1 with MA attr only,
    /// Authenticator field = request authenticator, MA value zeroed during HMAC.
    /// </summary>
    public static readonly byte[] MessageAuthenticator =
        Convert.FromHexString("060cc535e29384aae942a637613994e7");

    /// <summary>
    /// Fixed 16-octet Request Authenticator material for deterministic Access-Request tests
    /// (RFC 2865: these bytes are used directly — they are not MD5-hashed with the secret).
    /// </summary>
    public static readonly byte[] FixedRequestAuthenticator =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16
    ];
}
