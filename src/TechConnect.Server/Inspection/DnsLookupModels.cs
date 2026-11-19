// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DnsLookupModels.cs
//
// Purpose:
// Defines DNS lookup request/result models and ConfiguredDnsLookupService,
// which adapts IDnsResolver for CLI nslookup (A/AAAA or PTR for IP literals).
//
// -----------------------------------------------------------------------------

using System.Net;
using TechConnect.Server.NameResolution;

namespace TechConnect.Server.Inspection;

/// <summary>
/// Input for a NOC <c>nslookup</c>-style query.
/// </summary>
internal sealed class DnsLookupRequest
{
    /// <summary>Hostname or IP literal to look up.</summary>
    public required string Hostname { get; init; }
}

/// <summary>
/// Result of a NOC DNS lookup against TechConnect-configured servers.
/// </summary>
internal sealed class DnsLookupResult
{
    /// <summary>Queried hostname or literal.</summary>
    public required string Hostname { get; init; }

    /// <summary>Whether usable answer data was obtained.</summary>
    public required bool Success { get; init; }

    /// <summary>A/AAAA addresses for forward lookups.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>PTR hostname for reverse lookups.</summary>
    public string? PtrHostname { get; init; }

    /// <summary>Whether this was a PTR reverse lookup of an IP literal.</summary>
    public bool IsReverseLookup { get; init; }

    /// <summary>DNS server that answered, when known.</summary>
    public IPAddress? ServerUsed { get; init; }

    /// <summary>Display label such as <c>A/AAAA</c> or <c>PTR</c>.</summary>
    public string? QueryTypeLabel { get; init; }

    /// <summary>Error detail when unsuccessful.</summary>
    public string? Error { get; init; }

    /// <summary>Wall duration for the lookup.</summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// CLI nslookup adapter: resolves hostnames/IP literals via TechConnect-configured DNS only (never OS DNS).
/// </summary>
internal interface IDnsLookupService
{
    /// <summary>
    /// Looks up <paramref name="request"/> using configured TechConnect DNS (never OS DNS).
    /// </summary>
    /// <param name="request">Lookup request.</param>
    /// <param name="cancellationToken">Token that cancels DNS queries.</param>
    /// <returns>Lookup result; disabled DNS returns an explicit configuration error.</returns>
    Task<DnsLookupResult> LookupAsync(DnsLookupRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Adapts <see cref="IDnsResolver"/> for CLI nslookup (A + AAAA, or PTR for IP literals).
/// </summary>
/// <remarks>
/// Forward lookups query A and AAAA sequentially (not in parallel). Addresses are
/// ordered AAAA then A, sorted within family. Reverse lookups build a PTR name via
/// <see cref="DnsWireCodec.BuildPtrName"/>.
/// </remarks>
internal sealed class ConfiguredDnsLookupService : IDnsLookupService
{
    /// <summary>Underlying TechConnect DNS resolver.</summary>
    private readonly IDnsResolver _resolver;

    /// <summary>
    /// Initializes the lookup service.
    /// </summary>
    /// <param name="resolver">Configured DNS resolver.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolver"/> is null.</exception>
    public ConfiguredDnsLookupService(IDnsResolver resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public async Task<DnsLookupResult> LookupAsync(
        DnsLookupRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Hostname))
        {
            return new DnsLookupResult
            {
                Hostname = request.Hostname ?? string.Empty,
                Success = false,
                Error = "Hostname is required."
            };
        }

        var host = request.Hostname.Trim();

        if (!_resolver.IsEnabled)
        {
            return new DnsLookupResult
            {
                Hostname = host,
                Success = false,
                Error = "DNS is not configured in TechConnect. Set TechConnectServer:Dns:Servers."
            };
        }

        if (IPAddress.TryParse(host, out var literal))
        {
            var ptr = await _resolver.QueryAsync(
                    DnsWireCodec.BuildPtrName(literal),
                    DnsRecordType.Ptr,
                    cancellationToken)
                .ConfigureAwait(false);
            return new DnsLookupResult
            {
                Hostname = host,
                Success = ptr.Success,
                IsReverseLookup = true,
                PtrHostname = ptr.PtrHostname,
                ServerUsed = ptr.ServerUsed,
                QueryTypeLabel = "PTR",
                Error = ptr.Success ? null : ptr.Error,
                Duration = ptr.Duration
            };
        }

        var a = await _resolver.QueryAsync(host, DnsRecordType.A, cancellationToken).ConfigureAwait(false);
        var aaaa = await _resolver.QueryAsync(host, DnsRecordType.Aaaa, cancellationToken)
            .ConfigureAwait(false);

        var addresses = new List<IPAddress>();
        if (aaaa.Success)
        {
            addresses.AddRange(aaaa.Addresses);
        }

        if (a.Success)
        {
            addresses.AddRange(a.Addresses);
        }

        addresses = addresses
            .Distinct()
            .OrderBy(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 0 : 1)
            .ThenBy(x => x.ToString(), StringComparer.Ordinal)
            .ToList();

        var duration = a.Duration > aaaa.Duration ? a.Duration : aaaa.Duration;
        var server = a.ServerUsed ?? aaaa.ServerUsed;

        if (addresses.Count == 0)
        {
            var error = a.Error ?? aaaa.Error ?? "No address records returned.";
            if (a.ResponseCode == DnsResponseCode.NxDomain || aaaa.ResponseCode == DnsResponseCode.NxDomain)
            {
                error = a.Error ?? aaaa.Error ?? "NXDOMAIN — name does not exist.";
            }

            return new DnsLookupResult
            {
                Hostname = host,
                Success = false,
                ServerUsed = server,
                QueryTypeLabel = "A/AAAA",
                Error = error,
                Duration = duration
            };
        }

        return new DnsLookupResult
        {
            Hostname = host,
            Success = true,
            Addresses = addresses,
            ServerUsed = server,
            QueryTypeLabel = "A/AAAA",
            Duration = duration
        };
    }
}
