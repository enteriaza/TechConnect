// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusPacket.cs
//
// Purpose:
// RADIUS packet builder and parser (RFC 2865) for Access-Request/Accept/Reject
// and related authenticator finalization.
//
// -----------------------------------------------------------------------------
using System.Security.Cryptography;
using TechConnect.Server.Authentication.Radius.Cryptography;

namespace TechConnect.Server.Authentication.Radius.Protocol;

/// <summary>
/// RADIUS packet builder and parser (RFC 2865 §3).
/// Adapted from enteriaza/RadiusClient <c>RadiusPacket</c> @ fd27424 for TechConnect ownership.
/// </summary>
internal sealed class RadiusPacket
{
    /// <summary>
    /// Header Length backing member.
    /// </summary>
    public const int HeaderLength = 20;
    /// <summary>
    /// Maximum Packet Length backing member.
    /// </summary>
    public const int MaximumPacketLength = 4096;
    /// <summary>
    /// Minimum Packet Length backing member.
    /// </summary>
    public const int MinimumPacketLength = HeaderLength;

    /// <summary>
    /// Code Index backing member.
    /// </summary>
    private const int CodeIndex = 0;
    /// <summary>
    /// Identifier Index backing member.
    /// </summary>
    private const int IdentifierIndex = 1;
    /// <summary>
    /// Length Index backing member.
    /// </summary>
    private const int LengthIndex = 2;
    /// <summary>
    /// Authenticator Index backing member.
    /// </summary>
    private const int AuthenticatorIndex = 4;
    /// <summary>
    /// Authenticator Field Length backing member.
    /// </summary>
    private const int AuthenticatorFieldLength = 16;

    /// <summary>
    /// attributes backing member.
    /// </summary>
    private readonly List<RadiusAttribute> _attributes = [];
    /// <summary>
    /// authenticator backing member.
    /// </summary>
    private byte[] _authenticator = new byte[AuthenticatorFieldLength];
    /// <summary>
    /// raw Data backing member.
    /// </summary>
    private byte[] _rawData;

    /// <summary>
    /// Radius Packet.
    /// </summary>
    public RadiusPacket(RadiusPacketCode packetType)
        : this(packetType, (byte)RandomNumberGenerator.GetInt32(0, 256))
    {
    }

    /// <summary>
    /// Radius Packet.
    /// </summary>
    public RadiusPacket(RadiusPacketCode packetType, byte identifier)
    {
        PacketType = packetType;
        Identifier = identifier;
        _rawData = new byte[HeaderLength];
        _rawData[CodeIndex] = (byte)PacketType;
        _rawData[IdentifierIndex] = Identifier;
        WriteLength((ushort)HeaderLength);
        Valid = true;
    }

    /// <summary>
    /// Parses a received RADIUS UDP payload. Check <see cref="Valid"/> before using fields.
    /// Malformed packets set <see cref="Valid"/> to false and do not throw.
    /// </summary>
    public RadiusPacket(ReadOnlySpan<byte> receivedData)
    {
        _rawData = [];

        if (receivedData.Length < MinimumPacketLength)
        {
            _rawData = receivedData.ToArray();
            Valid = false;
            return;
        }

        var declaredLength = (ushort)((receivedData[LengthIndex] << 8) | receivedData[LengthIndex + 1]);
        if (declaredLength < MinimumPacketLength ||
            declaredLength > MaximumPacketLength ||
            declaredLength > receivedData.Length)
        {
            _rawData = receivedData.ToArray();
            Valid = false;
            return;
        }

        _rawData = receivedData[..declaredLength].ToArray();

        try
        {
            PacketType = (RadiusPacketCode)_rawData[CodeIndex];
            Identifier = _rawData[IdentifierIndex];
            _rawData.AsSpan(AuthenticatorIndex, AuthenticatorFieldLength).CopyTo(_authenticator);

            if (!TryParseAttributes(_rawData.AsSpan(HeaderLength, declaredLength - HeaderLength)))
            {
                Valid = false;
                return;
            }

            Valid = true;
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or ArgumentOutOfRangeException
                or OverflowException
                or InvalidCastException)
        {
            Valid = false;
        }
    }

    /// <summary>
    /// Raw Data backing member.
    /// </summary>
    public byte[] RawData => _rawData;

    /// <summary>
    /// Packet Type backing member.
    /// </summary>
    public RadiusPacketCode PacketType { get; private set; }

    /// <summary>
    /// Identifier backing member.
    /// </summary>
    public byte Identifier { get; private set; }

    /// <summary>
    /// Valid backing member.
    /// </summary>
    public bool Valid { get; private set; }

    /// <summary>
    /// Attributes backing member.
    /// </summary>
    public IReadOnlyList<RadiusAttribute> Attributes => _attributes;

    /// <summary>
    /// Clone.
    /// </summary>
    public byte[] Authenticator => (byte[])_authenticator.Clone();

    /// <summary>
    /// Sets Identifier.
    /// </summary>
    public void SetIdentifier(byte id)
    {
        EnsureMutable();
        Identifier = id;
        _rawData[IdentifierIndex] = id;
    }

    /// <summary>
    /// Adds Attribute.
    /// </summary>
    public void AddAttribute(RadiusAttribute attribute)
    {
        ArgumentNullException.ThrowIfNull(attribute);
        EnsureMutable();

        var wire = attribute.GetWireBytes();
        if (attribute.Length < 2 || attribute.Length != wire.Length)
        {
            throw new ArgumentException("Attribute has an invalid or inconsistent Length/Data.", nameof(attribute));
        }

        if (_rawData.Length + attribute.Length > MaximumPacketLength)
        {
            throw new InvalidOperationException(
                $"Cannot add attribute: packet would exceed the {MaximumPacketLength}-byte maximum.");
        }

        var expanded = new byte[_rawData.Length + attribute.Length];
        _rawData.AsSpan().CopyTo(expanded);
        wire.AsSpan().CopyTo(expanded.AsSpan(_rawData.Length));
        _rawData = expanded;
        WriteLength((ushort)_rawData.Length);
        _attributes.Add(attribute);
    }

    /// <summary>
    /// Appends a zeroed Message-Authenticator attribute (Type 80). Call before
    /// <see cref="SetAuthenticator"/> when Message-Authenticator is required.
    /// </summary>
    public void AddMessageAuthenticatorPlaceholder()
    {
        EnsureMutable();
        var value = new byte[RadiusMessageAuthenticator.ValueLength];
        AddAttribute(RadiusAttribute.Create(RadiusAttributeType.MessageAuthenticator, value));
    }

    /// <summary>
    /// Finalises the Authenticator field for the current packet type.
    /// When a Message-Authenticator placeholder is present on Access-Request, its HMAC is
    /// recomputed after the request authenticator is written (RFC 3579 §3.2).
    /// </summary>
    public void SetAuthenticator(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> requestAuthenticator = default)
    {
        EnsureMutable();
        if (sharedSecret.IsEmpty)
        {
            throw new ArgumentException("Shared secret must not be empty.", nameof(sharedSecret));
        }

        var secretBytes = sharedSecret.ToArray();
        try
        {
            switch (PacketType)
            {
                case RadiusPacketCode.AccessRequest:
                    // RFC 2865 §4.1: Request Authenticator is 16 random octets (not MD5(random||secret)).
                    _authenticator = RadiusAuthenticators.CreateAccessRequestAuthenticator();
                    break;

                case RadiusPacketCode.AccessAccept:
                case RadiusPacketCode.AccessReject:
                case RadiusPacketCode.AccountingResponse:
                    if (requestAuthenticator.Length != AuthenticatorFieldLength)
                    {
                        throw new ArgumentException(
                            $"A valid {AuthenticatorFieldLength}-byte Request Authenticator is required for packet type {PacketType}.",
                            nameof(requestAuthenticator));
                    }

                    _authenticator = RadiusAuthenticators.ComputeResponseAuthenticator(
                        _rawData,
                        requestAuthenticator,
                        secretBytes);
                    break;

                case RadiusPacketCode.AccountingRequest:
                    _authenticator = RadiusAuthenticators.ComputeAccountingRequestAuthenticator(
                        _rawData,
                        secretBytes);
                    break;

                default:
                    throw new NotSupportedException(
                        $"Packet type '{PacketType}' does not have a defined authenticator strategy.");
            }

            _authenticator.CopyTo(_rawData.AsSpan(AuthenticatorIndex, AuthenticatorFieldLength));

            if (PacketType is RadiusPacketCode.AccessRequest)
            {
                RecomputeMessageAuthenticatorIfPresent(secretBytes);
            }
            else if (HasMessageAuthenticatorAttribute())
            {
                // For responses: Message-Authenticator HMAC uses Request Authenticator in the
                // Authenticator field, then Response Authenticator is recomputed over the final packet.
                if (requestAuthenticator.Length != AuthenticatorFieldLength)
                {
                    throw new ArgumentException(
                        "Request Authenticator is required when Message-Authenticator is present on a response.",
                        nameof(requestAuthenticator));
                }

                requestAuthenticator.CopyTo(_rawData.AsSpan(AuthenticatorIndex, AuthenticatorFieldLength));
                RecomputeMessageAuthenticatorIfPresent(secretBytes);
                _authenticator = RadiusAuthenticators.ComputeResponseAuthenticator(
                    _rawData,
                    requestAuthenticator,
                    secretBytes);
                _authenticator.CopyTo(_rawData.AsSpan(AuthenticatorIndex, AuthenticatorFieldLength));
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    /// <summary>
    /// Sets the Access-Request Request Authenticator to the supplied 16 bytes.
    /// Used when PAP User-Password was encoded against a pre-generated authenticator.
    /// Optionally recomputes Message-Authenticator when <paramref name="sharedSecret"/> is provided.
    /// </summary>
    public void SetAccessRequestAuthenticator(
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret = default)
    {
        EnsureMutable();
        if (PacketType is not RadiusPacketCode.AccessRequest)
        {
            throw new InvalidOperationException("Only Access-Request supports setting a Request Authenticator.");
        }

        if (requestAuthenticator.Length != AuthenticatorFieldLength)
        {
            throw new ArgumentException(
                $"Request Authenticator must be exactly {AuthenticatorFieldLength} bytes.",
                nameof(requestAuthenticator));
        }

        _authenticator = requestAuthenticator.ToArray();
        _authenticator.CopyTo(_rawData.AsSpan(AuthenticatorIndex, AuthenticatorFieldLength));

        if (!sharedSecret.IsEmpty)
        {
            RecomputeMessageAuthenticatorIfPresent(sharedSecret);
        }
    }

    /// <summary>Test alias for <see cref="SetAccessRequestAuthenticator"/>.</summary>
    internal void SetAccessRequestAuthenticatorForTests(
        ReadOnlySpan<byte> requestAuthenticator,
        ReadOnlySpan<byte> sharedSecret = default) =>
        SetAccessRequestAuthenticator(requestAuthenticator, sharedSecret);

    /// <summary>
    /// Attempts to Get Attribute without throwing for ordinary failures.
    /// </summary>
    public bool TryGetAttribute(RadiusAttributeType type, out RadiusAttribute? attribute)
    {
        foreach (var candidate in _attributes)
        {
            if (candidate.Type == type)
            {
                attribute = candidate;
                return true;
            }
        }

        attribute = null;
        return false;
    }

    /// <summary>
    /// Returns Attributes.
    /// </summary>
    public IReadOnlyList<RadiusAttribute> GetAttributes(RadiusAttributeType type)
    {
        List<RadiusAttribute>? matches = null;
        foreach (var candidate in _attributes)
        {
            if (candidate.Type != type)
            {
                continue;
            }

            matches ??= [];
            matches.Add(candidate);
        }

        return matches ?? (IReadOnlyList<RadiusAttribute>)Array.Empty<RadiusAttribute>();
    }

    /// <summary>
    /// Recompute Message Authenticator If Present.
    /// </summary>
    private void RecomputeMessageAuthenticatorIfPresent(ReadOnlySpan<byte> sharedSecret)
    {
        if (!TryFindMessageAuthenticatorValueOffset(out var valueOffset))
        {
            return;
        }

        _rawData.AsSpan(valueOffset, RadiusMessageAuthenticator.ValueLength).Clear();
        var hmac = RadiusMessageAuthenticator.Compute(_rawData, sharedSecret);
        try
        {
            hmac.CopyTo(_rawData.AsSpan(valueOffset, RadiusMessageAuthenticator.ValueLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hmac);
        }
    }

    /// <summary>
    /// Returns whether Message Authenticator Attribute is present.
    /// </summary>
    private bool HasMessageAuthenticatorAttribute() =>
        TryFindMessageAuthenticatorValueOffset(out _);

    /// <summary>
    /// Attempts to Find Message Authenticator Value Offset without throwing for ordinary failures.
    /// </summary>
    private bool TryFindMessageAuthenticatorValueOffset(out int valueOffset)
    {
        valueOffset = 0;
        var offset = HeaderLength;
        while (offset + 2 <= _rawData.Length)
        {
            var type = _rawData[offset];
            var length = _rawData[offset + 1];
            if (length < 2 || offset + length > _rawData.Length)
            {
                return false;
            }

            if (type == (byte)RadiusAttributeType.MessageAuthenticator)
            {
                if (length != RadiusMessageAuthenticator.AttributeWireLength)
                {
                    return false;
                }

                valueOffset = offset + 2;
                return true;
            }

            offset += length;
        }

        return false;
    }

    /// <summary>
    /// Attempts to Parse Attributes without throwing for ordinary failures.
    /// </summary>
    private bool TryParseAttributes(ReadOnlySpan<byte> attributeSpan)
    {
        var offset = 0;
        while (offset < attributeSpan.Length)
        {
            if (!RadiusAttribute.TryParse(attributeSpan[offset..], out var attribute, out var consumed) ||
                attribute is null)
            {
                return false;
            }

            _attributes.Add(attribute);
            offset += consumed;
        }

        return true;
    }

    /// <summary>
    /// Write Length.
    /// </summary>
    private void WriteLength(ushort length)
    {
        _rawData[LengthIndex] = (byte)(length >> 8);
        _rawData[LengthIndex + 1] = (byte)(length & 0xFF);
    }

    /// <summary>
    /// Ensure Mutable.
    /// </summary>
    private void EnsureMutable()
    {
        if (!Valid)
        {
            throw new InvalidOperationException("Cannot mutate an invalid RADIUS packet.");
        }
    }
}
