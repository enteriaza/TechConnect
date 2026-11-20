// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    ConfiguredDnsResolver.cs
//
// Purpose:
// Direct UDP/TCP DNS client against explicitly configured servers.
// Never uses the operating-system resolver; owns per-query sockets.
//
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.NameResolution;

/// <summary>
/// Direct UDP/TCP DNS client against explicitly configured servers. Never uses the OS resolver.
/// </summary>
/// <remarks>
/// <para>
/// Each query opens and disposes its own UDP (and on truncation, TCP) socket.
/// Per-server timeout uses a linked CTS with <c>CancelAfter</c>; caller cancellation
/// returns <see cref="DnsResponseCode.Cancelled"/>.
/// </para>
/// <para>
/// Failover walks <see cref="ConfiguredServers"/> in order on transport/server errors;
/// NXDOMAIN and NODATA stop without trying the next server.
/// </para>
/// </remarks>
internal sealed class ConfiguredDnsResolver : IDnsResolver
{
    /// <summary>
    /// Configured servers (in configuration order) and per-server timeout, swapped as one
    /// immutable snapshot by <see cref="ApplyOptions"/>; in-flight queries keep their snapshot.
    /// </summary>
    private volatile ResolverSettings _settings;

    /// <summary>DNS server port (53 in production; overridable for tests).</summary>
    private readonly int _port;

    /// <summary>Logger for failover and transport diagnostics.</summary>
    private readonly ILogger<ConfiguredDnsResolver> _logger;

    /// <summary>
    /// Initializes from <see cref="TechConnectServerOptions"/> DNS settings.
    /// </summary>
    /// <param name="options">Server options; <c>Dns:Servers</c> and <c>Dns:QueryTimeoutSeconds</c> are read.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is null.</exception>
    public ConfiguredDnsResolver(
        IOptions<TechConnectServerOptions> options,
        ILogger<ConfiguredDnsResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = CreateSettings(options.Value.Dns ?? new DnsOptions());
        _port = 53;
    }

    /// <summary>Test constructor with explicit servers (optional non-53 port for local fake DNS).</summary>
    /// <param name="servers">DNS server addresses.</param>
    /// <param name="timeout">Per-query timeout; non-positive becomes 3 seconds.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="port">DNS port (1–65535).</param>
    /// <exception cref="ArgumentNullException"><paramref name="servers"/> or <paramref name="logger"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> is out of range.</exception>
    internal ConfiguredDnsResolver(
        IReadOnlyList<IPAddress> servers,
        TimeSpan timeout,
        ILogger<ConfiguredDnsResolver> logger,
        int port = 53)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _settings = new ResolverSettings(servers, timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : timeout);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        _port = port;
    }

    /// <inheritdoc />
    public bool IsEnabled => _settings.Servers.Count > 0;

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> ConfiguredServers => _settings.Servers;

    /// <inheritdoc />
    public TimeSpan QueryTimeout => _settings.Timeout;

    /// <summary>
    /// Replaces the configured servers and query timeout for subsequent queries.
    /// </summary>
    /// <remarks>
    /// Called only after a validated configuration has been persisted. Queries already in
    /// progress complete against the snapshot they started with.
    /// </remarks>
    /// <param name="dns">Validated DNS options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="dns"/> is null.</exception>
    public void ApplyOptions(DnsOptions dns)
    {
        ArgumentNullException.ThrowIfNull(dns);
        _settings = CreateSettings(dns);
    }

    /// <summary>Builds an immutable settings snapshot from DNS options.</summary>
    private static ResolverSettings CreateSettings(DnsOptions dns) =>
        new(ParseServers(dns.Servers ?? []), TimeSpan.FromSeconds(Math.Clamp(dns.QueryTimeoutSeconds, 1, 60)));

    /// <summary>Immutable resolver settings snapshot.</summary>
    /// <param name="Servers">Server addresses in configuration order.</param>
    /// <param name="Timeout">Per-server query timeout.</param>
    private sealed record ResolverSettings(IReadOnlyList<IPAddress> Servers, TimeSpan Timeout);

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string hostname,
        CancellationToken cancellationToken)
    {
        var aTask = QueryAsync(hostname, DnsRecordType.A, cancellationToken);
        var aaaaTask = QueryAsync(hostname, DnsRecordType.Aaaa, cancellationToken);
        await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);

        var a = await aTask.ConfigureAwait(false);
        var aaaa = await aaaaTask.ConfigureAwait(false);

        var addresses = new List<IPAddress>();
        if (aaaa.Success)
        {
            addresses.AddRange(aaaa.Addresses.Where(x => x.AddressFamily == AddressFamily.InterNetworkV6));
        }

        if (a.Success)
        {
            addresses.AddRange(a.Addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork));
        }

        // Deterministic order within family: already AAAA then A; sort each family by string.
        return addresses
            .Distinct()
            .OrderBy(x => x.AddressFamily == AddressFamily.InterNetworkV6 ? 0 : 1)
            .ThenBy(x => x.ToString(), StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<string?> ReverseResolveAsync(
        IPAddress address,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!IsEnabled)
        {
            return null;
        }

        var ptrName = DnsWireCodec.BuildPtrName(address);
        var result = await QueryAsync(ptrName, DnsRecordType.Ptr, cancellationToken).ConfigureAwait(false);
        return result.Success ? result.PtrHostname : null;
    }

    /// <inheritdoc />
    public async Task<DnsQueryResult> QueryAsync(
        string name,
        DnsRecordType type,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var sw = Stopwatch.StartNew();
        var queryName = DnsWireCodec.NormalizeName(name);

        var settings = _settings;
        if (settings.Servers.Count == 0)
        {
            return Disabled(queryName, type, sw.Elapsed);
        }

        DnsQueryResult? lastTransient = null;

        foreach (var server in settings.Servers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new DnsQueryResult
                {
                    QueryName = queryName,
                    QueryType = type,
                    Success = false,
                    ResponseCode = DnsResponseCode.Cancelled,
                    ServerUsed = server,
                    Error = "DNS lookup cancelled.",
                    Duration = sw.Elapsed
                };
            }

            try
            {
                var result = await QueryServerAsync(server, queryName, type, settings.Timeout, cancellationToken)
                    .ConfigureAwait(false);
                result = result with { Duration = sw.Elapsed };

                // Fail over on transport/protocol failures and server-side errors that may
                // succeed on another configured resolver. Stop on success, NXDOMAIN, or NODATA.
                if (ShouldFailover(result.ResponseCode))
                {
                    _logger.LogDebug(
                        "DNS {Type} {Name} via {Server} failed; trying next resolver: {Code} {Error}",
                        type,
                        queryName,
                        server,
                        result.ResponseCode,
                        result.Error);
                    lastTransient = result;
                    continue;
                }

                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new DnsQueryResult
                {
                    QueryName = queryName,
                    QueryType = type,
                    Success = false,
                    ResponseCode = DnsResponseCode.Cancelled,
                    ServerUsed = server,
                    Error = "DNS lookup cancelled.",
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                _logger.LogDebug(ex, "DNS transport error for {Name} via {Server}", queryName, server);
                lastTransient = new DnsQueryResult
                {
                    QueryName = queryName,
                    QueryType = type,
                    Success = false,
                    ResponseCode = DnsResponseCode.TransportError,
                    ServerUsed = server,
                    Error = "DNS server did not respond.",
                    Duration = sw.Elapsed
                };
            }
        }

        return lastTransient ?? new DnsQueryResult
        {
            QueryName = queryName,
            QueryType = type,
            Success = false,
            ResponseCode = DnsResponseCode.Timeout,
            Error = "All configured DNS servers failed.",
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Whether another configured resolver should be tried after this response.
    /// NXDOMAIN and NODATA (NOERROR with no matching records) stop; server-side and
    /// transport failures fail over.
    /// </summary>
    internal static bool ShouldFailover(DnsResponseCode code) =>
        code is DnsResponseCode.Timeout
            or DnsResponseCode.TransportError
            or DnsResponseCode.Malformed
            or DnsResponseCode.Truncated
            or DnsResponseCode.ServFail
            or DnsResponseCode.Refused
            or DnsResponseCode.FormErr
            or DnsResponseCode.NotImp;

    /// <summary>
    /// Queries a single server over UDP, falling back to TCP when the TC bit is set.
    /// </summary>
    /// <param name="server">DNS server address.</param>
    /// <param name="queryName">Normalized query name.</param>
    /// <param name="type">Record type.</param>
    /// <param name="timeout">Per-server timeout from the query's settings snapshot.</param>
    /// <param name="cancellationToken">Caller cancellation; linked with per-query timeout.</param>
    /// <returns>Query result for this server attempt.</returns>
    private async Task<DnsQueryResult> QueryServerAsync(
        IPAddress server,
        string queryName,
        DnsRecordType type,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var transactionId = (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
        byte[] query;
        try
        {
            query = DnsWireCodec.BuildQuery(transactionId, queryName, type);
        }
        catch (ArgumentException ex)
        {
            return Fail(queryName, type, server, DnsResponseCode.FormErr, ex.Message, TimeSpan.Zero);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        byte[] responseBytes;
        try
        {
            responseBytes = await SendUdpAsync(server, query, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(queryName, type, server, DnsResponseCode.Timeout, "DNS query timed out.", TimeSpan.Zero);
        }

        if (!DnsWireCodec.TryParseResponse(
                responseBytes,
                transactionId,
                queryName,
                type,
                out var parsed,
                out var parseError))
        {
            return Fail(
                queryName,
                type,
                server,
                DnsResponseCode.Malformed,
                parseError ?? "Malformed DNS response.",
                TimeSpan.Zero);
        }

        if (parsed.Truncated)
        {
            _logger.LogDebug("DNS UDP truncated for {Name}; falling back to TCP via {Server}", queryName, server);
            try
            {
                responseBytes = await SendTcpAsync(server, query, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Fail(
                    queryName,
                    type,
                    server,
                    DnsResponseCode.Timeout,
                    "DNS TCP fallback timed out.",
                    TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                return Fail(
                    queryName,
                    type,
                    server,
                    DnsResponseCode.TransportError,
                    "DNS TCP fallback failed.",
                    TimeSpan.Zero);
            }

            if (!DnsWireCodec.TryParseResponse(
                    responseBytes,
                    transactionId,
                    queryName,
                    type,
                    out parsed,
                    out parseError))
            {
                return Fail(
                    queryName,
                    type,
                    server,
                    DnsResponseCode.Malformed,
                    parseError ?? "Malformed DNS TCP response.",
                    TimeSpan.Zero);
            }

            if (parsed.Truncated)
            {
                return Fail(
                    queryName,
                    type,
                    server,
                    DnsResponseCode.Truncated,
                    "DNS response remained truncated after TCP fallback.",
                    TimeSpan.Zero);
            }
        }

        return ToResult(queryName, type, server, parsed);
    }

    /// <summary>
    /// Maps a parsed response to a <see cref="DnsQueryResult"/>, treating NODATA as unsuccessful.
    /// </summary>
    /// <param name="queryName">Normalized query name.</param>
    /// <param name="type">Query type.</param>
    /// <param name="server">Server that answered.</param>
    /// <param name="parsed">Parsed wire response.</param>
    /// <returns>Mapped result.</returns>
    private static DnsQueryResult ToResult(
        string queryName,
        DnsRecordType type,
        IPAddress server,
        ParsedDnsResponse parsed)
    {
        if (parsed.ResponseCode != DnsResponseCode.NoError)
        {
            return new DnsQueryResult
            {
                QueryName = queryName,
                QueryType = type,
                Success = false,
                ResponseCode = parsed.ResponseCode,
                ServerUsed = server,
                Error = DescribeRcode(parsed.ResponseCode)
            };
        }

        if (type == DnsRecordType.Ptr)
        {
            if (string.IsNullOrWhiteSpace(parsed.PtrHostname))
            {
                return new DnsQueryResult
                {
                    QueryName = queryName,
                    QueryType = type,
                    Success = false,
                    ResponseCode = DnsResponseCode.NoError,
                    ServerUsed = server,
                    Error = "No PTR records returned."
                };
            }

            return new DnsQueryResult
            {
                QueryName = queryName,
                QueryType = type,
                Success = true,
                ResponseCode = DnsResponseCode.NoError,
                ServerUsed = server,
                PtrHostname = parsed.PtrHostname
            };
        }

        if (parsed.Addresses.Count == 0)
        {
            return new DnsQueryResult
            {
                QueryName = queryName,
                QueryType = type,
                Success = false,
                ResponseCode = DnsResponseCode.NoError,
                ServerUsed = server,
                Error = "No address records returned."
            };
        }

        return new DnsQueryResult
        {
            QueryName = queryName,
            QueryType = type,
            Success = true,
            ResponseCode = DnsResponseCode.NoError,
            ServerUsed = server,
            Addresses = parsed.Addresses
                .OrderBy(a => a.ToString(), StringComparer.Ordinal)
                .ToArray()
        };
    }

    /// <summary>
    /// Sends a DNS query over UDP and returns the response datagram (owned socket).
    /// </summary>
    /// <param name="server">DNS server.</param>
    /// <param name="query">Query message bytes.</param>
    /// <param name="cancellationToken">Cancellation / timeout token.</param>
    /// <returns>Response bytes (copy of received buffer).</returns>
    /// <exception cref="IOException">Empty UDP response.</exception>
    private async Task<byte[]> SendUdpAsync(
        IPAddress server,
        byte[] query,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        socket.SendTimeout = 5_000;
        socket.ReceiveTimeout = 5_000;
        await socket.ConnectAsync(new IPEndPoint(server, _port), cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(query, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[DnsWireCodec.MaxUdpPayload];
        var received = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cancellationToken)
            .ConfigureAwait(false);
        if (received <= 0)
        {
            throw new IOException("Empty DNS UDP response.");
        }

        return buffer.AsSpan(0, received).ToArray();
    }

    /// <summary>
    /// Sends a length-prefixed DNS query over TCP and reads the framed response (owned socket).
    /// </summary>
    /// <param name="server">DNS server.</param>
    /// <param name="query">Query message bytes (without length prefix).</param>
    /// <param name="cancellationToken">Cancellation / timeout token.</param>
    /// <returns>Response message bytes (without length prefix).</returns>
    /// <exception cref="IOException">Invalid length or connection closed.</exception>
    private async Task<byte[]> SendTcpAsync(
        IPAddress server,
        byte[] query,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(server.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(server, _port), cancellationToken).ConfigureAwait(false);

        var framed = new byte[2 + query.Length];
        framed[0] = (byte)(query.Length >> 8);
        framed[1] = (byte)(query.Length & 0xFF);
        query.CopyTo(framed.AsSpan(2));
        await socket.SendAsync(framed, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        var lengthBuf = new byte[2];
        await ReadExactAsync(socket, lengthBuf, cancellationToken).ConfigureAwait(false);
        var length = (lengthBuf[0] << 8) | lengthBuf[1];
        if (length is < 12 or > DnsWireCodec.MaxTcpMessage)
        {
            throw new IOException("Invalid DNS TCP message length.");
        }

        var message = new byte[length];
        await ReadExactAsync(socket, message, cancellationToken).ConfigureAwait(false);
        return message;
    }

    /// <summary>Reads exactly <paramref name="buffer"/>.Length bytes from <paramref name="socket"/>.</summary>
    /// <param name="socket">Connected TCP socket.</param>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="IOException">Peer closed before the buffer was filled.</exception>
    private static async Task ReadExactAsync(Socket socket, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket
                .ReceiveAsync(buffer[offset..], SocketFlags.None, cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("DNS TCP connection closed.");
            }

            offset += read;
        }
    }

    /// <summary>Builds a disabled-DNS result when no servers are configured.</summary>
    /// <param name="name">Normalized query name.</param>
    /// <param name="type">Query type.</param>
    /// <param name="duration">Elapsed duration.</param>
    /// <returns>Unsuccessful disabled result.</returns>
    private static DnsQueryResult Disabled(string name, DnsRecordType type, TimeSpan duration) =>
        new()
        {
            QueryName = name,
            QueryType = type,
            Success = false,
            ResponseCode = DnsResponseCode.Disabled,
            Error = "DNS is not configured in TechConnect. Set TechConnectServer:Dns:Servers.",
            Duration = duration
        };

    /// <summary>Builds an unsuccessful result for a specific server attempt.</summary>
    /// <param name="name">Normalized query name.</param>
    /// <param name="type">Query type.</param>
    /// <param name="server">Server attempted.</param>
    /// <param name="code">Response / local code.</param>
    /// <param name="error">Error detail.</param>
    /// <param name="duration">Elapsed duration (often zero for early failures).</param>
    /// <returns>Unsuccessful result.</returns>
    private static DnsQueryResult Fail(
        string name,
        DnsRecordType type,
        IPAddress server,
        DnsResponseCode code,
        string error,
        TimeSpan duration) =>
        new()
        {
            QueryName = name,
            QueryType = type,
            Success = false,
            ResponseCode = code,
            ServerUsed = server,
            Error = error,
            Duration = duration
        };

    /// <summary>Maps a wire RCODE to an operator-facing description.</summary>
    /// <param name="code">Response code.</param>
    /// <returns>Description string.</returns>
    private static string DescribeRcode(DnsResponseCode code) =>
        code switch
        {
            DnsResponseCode.FormErr => "FORMERR — server could not interpret the query.",
            DnsResponseCode.ServFail => "SERVFAIL — server failure.",
            DnsResponseCode.NxDomain => "NXDOMAIN — name does not exist.",
            DnsResponseCode.NotImp => "NOTIMP — query type not implemented.",
            DnsResponseCode.Refused => "REFUSED — server refused the query.",
            _ => $"DNS response code {(int)code}."
        };

    /// <summary>
    /// Parses configured server strings as IP literals; blank and non-IP entries are skipped.
    /// </summary>
    /// <param name="servers">Configuration entries.</param>
    /// <returns>Parsed address list (may be empty).</returns>
    private static IReadOnlyList<IPAddress> ParseServers(IEnumerable<string> servers)
    {
        var list = new List<IPAddress>();
        foreach (var entry in servers)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (IPAddress.TryParse(entry.Trim(), out var address))
            {
                list.Add(address);
            }
        }

        return list;
    }
}
