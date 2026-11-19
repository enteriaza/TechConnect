// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    FakeIcmpTracerouteClient.cs
//
// Purpose:
// Test double IIcmpTracerouteClient that yields scripted TracerouteProbeResult sequences.
//
// -----------------------------------------------------------------------------

using System.Runtime.CompilerServices;
using TechConnect.Server.Diagnostics;

namespace TechConnect.Server.Tests.Diagnostics;

/// <summary>
/// Scripted <see cref="IIcmpTracerouteClient"/> for dispatcher and formatter tests.
/// </summary>
/// <remarks>
/// Yields enqueued results in order, optionally delaying per probe. Can throw a configured
/// exception before yielding. Does not open sockets.
/// </remarks>
internal sealed class FakeIcmpTracerouteClient : IIcmpTracerouteClient
{
    /// <summary>Queued probe results to yield from <see cref="TraceAsync"/>.</summary>
    private readonly Queue<TracerouteProbeResult> _results = new();

    /// <summary>Optional delay applied before each yielded probe.</summary>
    private readonly TimeSpan _delayPerProbe;

    /// <summary>
    /// Initializes the fake client.
    /// </summary>
    /// <param name="delayPerProbe">Per-probe delay; null or omitted means no delay.</param>
    public FakeIcmpTracerouteClient(TimeSpan? delayPerProbe = null) =>
        _delayPerProbe = delayPerProbe ?? TimeSpan.Zero;

    /// <summary>Requests observed by <see cref="TraceAsync"/>, in call order.</summary>
    public List<TracerouteRequest> Requests { get; } = [];

    /// <summary>When set, <see cref="TraceAsync"/> throws this exception after recording the request.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>
    /// When set, invoked per request to choose an exception to throw (overrides <see cref="ExceptionToThrow"/> when non-null).
    /// </summary>
    public Func<TracerouteRequest, Exception?>? ExceptionFactory { get; set; }

    /// <summary>
    /// Enqueues probe results to be yielded by the next <see cref="TraceAsync"/> enumeration.
    /// </summary>
    /// <param name="results">Results to enqueue in order.</param>
    public void Enqueue(params TracerouteProbeResult[] results)
    {
        foreach (var result in results)
        {
            _results.Enqueue(result);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TracerouteProbeResult> TraceAsync(
        TracerouteRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var toThrow = ExceptionFactory?.Invoke(request) ?? ExceptionToThrow;
        if (toThrow is not null)
        {
            throw toThrow;
        }

        while (_results.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_delayPerProbe > TimeSpan.Zero)
            {
                await Task.Delay(_delayPerProbe, cancellationToken).ConfigureAwait(false);
            }

            yield return _results.Dequeue();
        }
    }
}
