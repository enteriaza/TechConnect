// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusExchangeResult.cs
//
// Purpose:
// Carries the outcome of a RADIUS exchange, including a validated packet only
// for Accept and Reject results.
//
// -----------------------------------------------------------------------------

using TechConnect.Server.Authentication.Radius.Protocol;

namespace TechConnect.Server.Authentication.Radius.Client;

/// <summary>
/// Result of a RADIUS exchange.
/// </summary>
/// <remarks>
/// <see cref="Packet"/> is only set for <see cref="RadiusExchangeOutcome.Accept"/>
/// and <see cref="RadiusExchangeOutcome.Reject"/> after integrity validation succeeds.
/// Other outcomes carry a <see cref="Detail"/> string and a null packet.
/// </remarks>
internal sealed class RadiusExchangeResult
{
    /// <summary>
    /// Creates a result with the given outcome, optional packet, and optional detail.
    /// </summary>
    /// <param name="outcome">Semantic exchange outcome.</param>
    /// <param name="packet">Validated packet for Accept/Reject; otherwise <see langword="null"/>.</param>
    /// <param name="detail">Diagnostic detail for non-success paths.</param>
    private RadiusExchangeResult(
        RadiusExchangeOutcome outcome,
        RadiusPacket? packet,
        string? detail)
    {
        Outcome = outcome;
        Packet = packet;
        Detail = detail;
    }

    /// <summary>
    /// Semantic outcome of the exchange.
    /// </summary>
    public RadiusExchangeOutcome Outcome { get; }

    /// <summary>
    /// Validated response packet for Accept/Reject; otherwise <see langword="null"/>.
    /// </summary>
    public RadiusPacket? Packet { get; }

    /// <summary>
    /// Optional diagnostic detail (timeouts, validation failures, cancellation reasons).
    /// </summary>
    public string? Detail { get; }

    /// <summary>
    /// Creates an Accept result with a validated Access-Accept packet.
    /// </summary>
    /// <param name="packet">Validated Access-Accept packet.</param>
    /// <returns>Result with <see cref="RadiusExchangeOutcome.Accept"/>.</returns>
    public static RadiusExchangeResult Accept(RadiusPacket packet) =>
        new(RadiusExchangeOutcome.Accept, packet, null);

    /// <summary>
    /// Creates a Reject result with a validated Access-Reject packet.
    /// </summary>
    /// <param name="packet">Validated Access-Reject packet.</param>
    /// <returns>Result with <see cref="RadiusExchangeOutcome.Reject"/>.</returns>
    public static RadiusExchangeResult Reject(RadiusPacket packet) =>
        new(RadiusExchangeOutcome.Reject, packet, null);

    /// <summary>
    /// Creates an Unavailable result (timeout or transport failure).
    /// </summary>
    /// <param name="detail">Reason the server was unavailable.</param>
    /// <returns>Result with <see cref="RadiusExchangeOutcome.Unavailable"/>.</returns>
    public static RadiusExchangeResult Unavailable(string detail) =>
        new(RadiusExchangeOutcome.Unavailable, null, detail);

    /// <summary>
    /// Creates an InvalidResponse result after a datagram failed validation.
    /// </summary>
    /// <param name="detail">Validation failure description.</param>
    /// <returns>Result with <see cref="RadiusExchangeOutcome.InvalidResponse"/>.</returns>
    public static RadiusExchangeResult InvalidResponse(string detail) =>
        new(RadiusExchangeOutcome.InvalidResponse, null, detail);

    /// <summary>
    /// Creates a Cancelled result when the caller or client aborted the exchange.
    /// </summary>
    /// <param name="detail">Cancellation description.</param>
    /// <returns>Result with <see cref="RadiusExchangeOutcome.Cancelled"/>.</returns>
    public static RadiusExchangeResult Cancelled(string detail) =>
        new(RadiusExchangeOutcome.Cancelled, null, detail);
}