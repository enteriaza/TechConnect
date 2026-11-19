// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    DnsQueryModels.cs
//
// Purpose:
// Defines DNS record types, response codes, query result models, and the
// IDnsResolver contract for TechConnect-configured name resolution.
//
// -----------------------------------------------------------------------------

using System.Net;
using System.Net.Sockets;

namespace TechConnect.Server.NameResolution;

/// <summary>
/// DNS resource record types supported by the TechConnect resolver.
/// </summary>
internal enum DnsRecordType : ushort
{
    /// <summary>IPv4 address record.</summary>
    A = 1,

    /// <summary>Pointer (reverse) record.</summary>
    Ptr = 12,

    /// <summary>IPv6 address record.</summary>
    Aaaa = 28
}

/// <summary>
/// DNS response / client outcome codes, including wire RCODEs and local transport statuses.
/// </summary>
/// <remarks>
/// Values 0–5 match RFC wire RCODEs. Values 100+ are TechConnect-local (truncated, timeout,
/// malformed, cancelled, disabled, no servers, transport error) and are never on the wire.
/// </remarks>
internal enum DnsResponseCode
{
    /// <summary>NOERROR — successful response (may still be NODATA).</summary>
    NoError = 0,

    /// <summary>FORMERR — server could not interpret the query.</summary>
    FormErr = 1,

    /// <summary>SERVFAIL — server failure.</summary>
    ServFail = 2,

    /// <summary>NXDOMAIN — name does not exist.</summary>
    NxDomain = 3,

    /// <summary>NOTIMP — query type not implemented.</summary>
    NotImp = 4,

    /// <summary>REFUSED — server refused the query.</summary>
    Refused = 5,

    /// <summary>Local: response remained truncated after TCP fallback.</summary>
    Truncated = 100,

    /// <summary>Local: query timed out against a server.</summary>
    Timeout = 101,

    /// <summary>Local: response failed parse / validation.</summary>
    Malformed = 102,

    /// <summary>Local: caller cancellation.</summary>
    Cancelled = 103,

    /// <summary>Local: no configured servers (<see cref="IDnsResolver.IsEnabled"/> is false).</summary>
    Disabled = 104,

    /// <summary>Local: reserved / unused no-servers sentinel.</summary>
    NoServers = 105,

    /// <summary>Local: socket/IO failure talking to a server.</summary>
    TransportError = 106
}

/// <summary>
/// Result of a single DNS query against configured TechConnect servers.
/// </summary>
internal sealed record DnsQueryResult
{
    /// <summary>Normalized query name.</summary>
    public required string QueryName { get; init; }

    /// <summary>Record type that was queried.</summary>
    public required DnsRecordType QueryType { get; init; }

    /// <summary>
    /// Whether usable answer data was obtained (addresses for A/AAAA, hostname for PTR).
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>Wire or local response code.</summary>
    public DnsResponseCode ResponseCode { get; init; }

    /// <summary>Server that produced this result, when known.</summary>
    public IPAddress? ServerUsed { get; init; }

    /// <summary>A/AAAA addresses from the answer section (empty for PTR / failures).</summary>
    public IReadOnlyList<IPAddress> Addresses { get; init; } = [];

    /// <summary>PTR hostname when a reverse lookup succeeded.</summary>
    public string? PtrHostname { get; init; }

    /// <summary>Human-readable error for unsuccessful outcomes.</summary>
    public string? Error { get; init; }

    /// <summary>Wall duration for the query attempt (including failover).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets whether this is an authoritative negative (NXDOMAIN or NOERROR NODATA).
    /// </summary>
    /// <value>
    /// <see langword="true"/> for NXDOMAIN, or NOERROR with no addresses/PTR and <see cref="Success"/> false.
    /// </value>
    public bool IsAuthoritativeNegative =>
        ResponseCode is DnsResponseCode.NxDomain ||
        (ResponseCode == DnsResponseCode.NoError &&
         !Success &&
         Addresses.Count == 0 &&
         PtrHostname is null);
}

/// <summary>
/// TechConnect-configured DNS client. Never uses the operating-system resolver.
/// </summary>
internal interface IDnsResolver
{
    /// <summary>
    /// Gets whether at least one server is configured under <c>TechConnectServer:Dns:Servers</c>.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>Gets the configured DNS server addresses in configuration order.</summary>
    IReadOnlyList<IPAddress> ConfiguredServers { get; }

    /// <summary>Gets the per-server query timeout.</summary>
    TimeSpan QueryTimeout { get; }

    /// <summary>
    /// Issues a single typed query, failing over across configured servers on transient failures.
    /// </summary>
    /// <param name="name">DNS name (or PTR reverse name).</param>
    /// <param name="type">Record type.</param>
    /// <param name="cancellationToken">Token that cancels the lookup.</param>
    /// <returns>Query result; does not throw for DNS protocol failures.</returns>
    Task<DnsQueryResult> QueryAsync(
        string name,
        DnsRecordType type,
        CancellationToken cancellationToken);

    /// <summary>
    /// Resolves A and AAAA in parallel and returns distinct addresses (AAAA then A, sorted within family).
    /// </summary>
    /// <param name="hostname">Hostname to resolve.</param>
    /// <param name="cancellationToken">Token that cancels both queries.</param>
    /// <returns>Combined address list (may be empty).</returns>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostname,
        CancellationToken cancellationToken);

    /// <summary>
    /// Performs a PTR reverse lookup; returns <see langword="null"/> when DNS is disabled or PTR fails.
    /// </summary>
    /// <param name="address">Address to reverse-resolve.</param>
    /// <param name="cancellationToken">Token that cancels the lookup.</param>
    /// <returns>PTR hostname, or <see langword="null"/>.</returns>
    Task<string?> ReverseResolveAsync(
        IPAddress address,
        CancellationToken cancellationToken);
}
