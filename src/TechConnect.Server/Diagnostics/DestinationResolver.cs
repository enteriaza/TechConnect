// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DestinationResolver.cs
//
// Purpose:
// Shared destination detection and address selection for ping and traceroute,
// using configured TechConnect DNS (never OS DNS) and IPv6 availability gates.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Diagnostics;

/// <summary>
/// Outcome of resolving a ping/traceroute destination literal or hostname.
/// </summary>
internal sealed class DestinationResolutionResult
{
    /// <summary>Whether an address was selected successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Trimmed input string that was resolved.</summary>
    public required string Input { get; init; }

    /// <summary>Selected address when <see cref="Success"/> is <see langword="true"/>.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>Address family of <see cref="Address"/> when successful.</summary>
    public AddressFamily? Family { get; init; }

    /// <summary>Whether the input was an IP literal rather than a hostname.</summary>
    public bool WasLiteral { get; init; }

    /// <summary>All addresses returned by DNS for hostnames; single literal for IP input.</summary>
    public IReadOnlyList<IPAddress> AllAddresses { get; init; } = [];

    /// <summary>Operator-facing error when unsuccessful.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Resolves operator destination text to a usable IP address for diagnostics.
/// </summary>
internal interface IDestinationResolver
{
    /// <summary>
    /// Resolves <paramref name="destination"/> as a literal or via configured DNS.
    /// </summary>
    /// <param name="destination">IP literal or hostname.</param>
    /// <param name="cancellationToken">Token that cancels DNS queries.</param>
    /// <returns>Success with selected address, or failure with operator-facing error text.</returns>
    /// <exception cref="OperationCanceledException">
    /// Propagated when <paramref name="cancellationToken"/> cancels during DNS resolution.
    /// </exception>
    Task<DestinationResolutionResult> ResolveAsync(
        string destination,
        CancellationToken cancellationToken);
}

/// <summary>
/// Shared destination detection and address selection for ping and traceroute.
/// </summary>
/// <remarks>
/// <para>
/// Hostname resolution uses <see cref="IDnsResolver"/> only (TechConnect-configured
/// servers). When DNS is disabled, hostname resolution fails with an explicit
/// configuration error — the OS resolver is never consulted.
/// </para>
/// <para>
/// IPv6 literals and AAAA selection require <see cref="IIPv6NetworkAvailability.IsIpv6PingAvailable"/>.
/// Prefer IPv6 when available and at least one AAAA exists; otherwise first IPv4.
/// IPv6-only answers are rejected when IPv6 is unavailable (no silent IPv6 use).
/// </para>
/// </remarks>
internal sealed class DestinationResolver : IDestinationResolver
{
    /// <summary>Configured TechConnect DNS resolver (may be disabled).</summary>
    private readonly IDnsResolver _dns;

    /// <summary>Host IPv6 usability gate.</summary>
    private readonly IIPv6NetworkAvailability _ipv6;

    /// <summary>
    /// Initializes a resolver over configured DNS and IPv6 availability.
    /// </summary>
    /// <param name="dns">TechConnect DNS client; never the OS resolver.</param>
    /// <param name="ipv6">IPv6 availability detector.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dns"/> or <paramref name="ipv6"/> is null.</exception>
    public DestinationResolver(IDnsResolver dns, IIPv6NetworkAvailability ipv6)
    {
        _dns = dns ?? throw new ArgumentNullException(nameof(dns));
        _ipv6 = ipv6 ?? throw new ArgumentNullException(nameof(ipv6));
    }

    /// <inheritdoc />
    public async Task<DestinationResolutionResult> ResolveAsync(
        string destination,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            return Fail(destination ?? string.Empty, "% Invalid destination.");
        }

        var input = destination.Trim();

        if (IPAddress.TryParse(input, out var literal))
        {
            if (literal.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            {
                return Fail(input, "% Unsupported destination address family.");
            }

            if (literal.AddressFamily == AddressFamily.InterNetworkV6 && !_ipv6.IsIpv6PingAvailable)
            {
                return Fail(
                    input,
                    "% IPv6 is unavailable: no usable global IPv6 address with an IPv6 default gateway.");
            }

            return new DestinationResolutionResult
            {
                Success = true,
                Input = input,
                Address = literal,
                Family = literal.AddressFamily,
                WasLiteral = true,
                AllAddresses = [literal]
            };
        }

        if (!_dns.IsEnabled)
        {
            return Fail(
                input,
                "% DNS is not configured in TechConnect. Hostname resolution requires TechConnectServer:Dns:Servers.");
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _dns.ResolveAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Fail(input, "% Unable to resolve hostname.");
        }

        if (addresses.Count == 0)
        {
            return Fail(input, "% Hostname did not resolve to an address.");
        }

        var selected = SelectAddress(addresses, _ipv6.IsIpv6PingAvailable);
        if (selected is null)
        {
            return Fail(input, "% Hostname did not resolve to a usable address family.");
        }

        return new DestinationResolutionResult
        {
            Success = true,
            Input = input,
            Address = selected,
            Family = selected.AddressFamily,
            WasLiteral = false,
            AllAddresses = addresses
        };
    }

    /// <summary>
    /// Prefer IPv6 when IPv6 is enabled and at least one AAAA exists; otherwise first IPv4.
    /// </summary>
    /// <param name="addresses">Candidate addresses from DNS or literals.</param>
    /// <param name="ipv6Available">Whether the host can use IPv6 for diagnostics.</param>
    /// <returns>Selected address, or <see langword="null"/> when no usable family exists.</returns>
    /// <remarks>Deterministic within a family via ordinal string sort.</remarks>
    internal static IPAddress? SelectAddress(IReadOnlyList<IPAddress> addresses, bool ipv6Available)
    {
        var v6 = addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6)
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToList();
        var v4 = addresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .ToList();

        if (ipv6Available && v6.Count > 0)
        {
            return v6[0];
        }

        if (v4.Count > 0)
        {
            return v4[0];
        }

        if (ipv6Available && v6.Count > 0)
        {
            return v6[0];
        }

        // IPv6 unavailable and no IPv4 — do not silently use IPv6.
        return null;
    }

    /// <summary>Builds a failed resolution result.</summary>
    /// <param name="input">Original input.</param>
    /// <param name="error">Operator-facing error.</param>
    /// <returns>Unsuccessful <see cref="DestinationResolutionResult"/>.</returns>
    private static DestinationResolutionResult Fail(string input, string error) =>
        new()
        {
            Success = false,
            Input = input,
            Error = error
        };
}
