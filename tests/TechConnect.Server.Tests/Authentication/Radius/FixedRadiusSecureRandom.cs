// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FixedRadiusSecureRandom.cs
//
// Purpose:
// Regression tests and helpers covering FixedRadiusSecureRandom behaviour.
// Fixture: FixedRadiusSecureRandom.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Radius.Cryptography;

namespace TechConnect.Server.Tests.Authentication.Radius;

/// <summary>
/// Narrow test double that fills destinations from a predetermined 16-byte sequence.
/// </summary>
internal sealed class FixedRadiusSecureRandom : IRadiusSecureRandom
{
    /// <summary>
    /// bytes test helper member.
    /// </summary>
    private readonly byte[] _bytes;
    /// <summary>
    /// call Count test helper member.
    /// </summary>
    private int _callCount;
    /// <summary>
    /// Creates a FixedRadiusSecureRandom test helper.
    /// </summary>

    public FixedRadiusSecureRandom(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length != RadiusAuthenticators.AuthenticatorLength)
        {
            throw new ArgumentException(
                $"Fixed random material must be exactly {RadiusAuthenticators.AuthenticatorLength} bytes.",
                nameof(bytes));
        }

        _bytes = (byte[])bytes.Clone();
    }
    /// <summary>
    /// call Count test helper member.
    /// </summary>

    public int CallCount => _callCount;
    /// <summary>
    /// Verifies Fill.
    /// </summary>

    public void Fill(Span<byte> destination)
    {
        if (destination.Length != _bytes.Length)
        {
            throw new ArgumentException(
                $"Destination must be exactly {_bytes.Length} bytes.",
                nameof(destination));
        }

        _bytes.CopyTo(destination);
        _callCount++;
    }
}
