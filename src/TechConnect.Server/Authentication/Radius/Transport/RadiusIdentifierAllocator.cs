// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    RadiusIdentifierAllocator.cs
//
// Purpose:
// Allocates RADIUS packet identifiers (0–255) with exclusive outstanding
// ownership and post-release quarantine against late-response reuse.
//
// -----------------------------------------------------------------------------

namespace TechConnect.Server.Authentication.Radius.Transport;

/// <summary>
/// Allocates RADIUS packet identifiers (0–255) with exclusive outstanding ownership and a
/// post-release quarantine so late responses cannot be delivered to a reused identifier.
/// </summary>
/// <remarks>
/// All public methods synchronize on an internal gate; concurrent callers are supported.
/// Quarantine expiry uses the injected <see cref="TimeProvider"/>.
/// </remarks>
internal sealed class RadiusIdentifierAllocator
{
    /// <summary>
    /// Number of distinct RADIUS identifiers (0–255).
    /// </summary>
    public const int Capacity = 256;

    /// <summary>
    /// Lock protecting free/in-use/quarantine collections.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Identifiers currently available for allocation (excluding quarantined).
    /// </summary>
    private readonly Queue<byte> _free = new(Capacity);

    /// <summary>
    /// Identifiers currently outstanding in an exchange.
    /// </summary>
    private readonly HashSet<byte> _inUse = new(Capacity);

    /// <summary>
    /// Quarantined identifiers mapped to UTC expiry ticks.
    /// </summary>
    private readonly Dictionary<byte, long> _quarantineExpiryTicks = new(Capacity);

    /// <summary>
    /// Clock used to evaluate quarantine expiry.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an allocator with all 256 identifiers initially free.
    /// </summary>
    /// <param name="timeProvider">
    /// Optional clock; when <see langword="null"/>, <see cref="TimeProvider.System"/> is used.
    /// </param>
    public RadiusIdentifierAllocator(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        for (var i = 0; i < Capacity; i++)
        {
            _free.Enqueue((byte)i);
        }
    }

    /// <summary>
    /// Count of identifiers currently marked in use (outstanding exchanges).
    /// </summary>
    public int InUseCount
    {
        get
        {
            lock (_gate)
            {
                return _inUse.Count;
            }
        }
    }

    /// <summary>
    /// Count of identifiers currently free after purging expired quarantine entries.
    /// </summary>
    public int FreeCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpiredQuarantineUnlocked();
                return _free.Count;
            }
        }
    }

    /// <summary>
    /// Count of identifiers still quarantined after purging expired entries.
    /// </summary>
    public int QuarantinedCount
    {
        get
        {
            lock (_gate)
            {
                PurgeExpiredQuarantineUnlocked();
                return _quarantineExpiryTicks.Count;
            }
        }
    }

    /// <summary>
    /// Attempts to allocate an identifier.
    /// </summary>
    /// <param name="identifier">Allocated identifier when the method returns <see langword="true"/>; otherwise 0.</param>
    /// <returns>
    /// <see langword="false"/> when all identifiers are in use or quarantined; otherwise <see langword="true"/>.
    /// </returns>
    public bool TryAllocate(out byte identifier)
    {
        lock (_gate)
        {
            PurgeExpiredQuarantineUnlocked();

            while (_free.Count > 0)
            {
                var candidate = _free.Dequeue();
                if (_quarantineExpiryTicks.ContainsKey(candidate) || !_inUse.Add(candidate))
                {
                    continue;
                }

                identifier = candidate;
                return true;
            }

            identifier = 0;
            return false;
        }
    }

    /// <summary>
    /// Releases an outstanding identifier into quarantine for <paramref name="quarantine"/>.
    /// </summary>
    /// <param name="identifier">Identifier previously allocated by <see cref="TryAllocate"/>.</param>
    /// <param name="quarantine">
    /// Quarantine duration; negative values are treated as <see cref="TimeSpan.Zero"/>.
    /// </param>
    /// <remarks>
    /// Idempotent: releasing an identifier that is not in use is a no-op.
    /// </remarks>
    public void Release(byte identifier, TimeSpan quarantine)
    {
        if (quarantine < TimeSpan.Zero)
        {
            quarantine = TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (!_inUse.Remove(identifier))
            {
                return;
            }

            var expiry = _timeProvider.GetUtcNow().UtcTicks + quarantine.Ticks;
            _quarantineExpiryTicks[identifier] = expiry;
            PurgeExpiredQuarantineUnlocked();
        }
    }

    /// <summary>
    /// Moves expired quarantined identifiers back onto the free queue.
    /// </summary>
    /// <remarks>Caller must hold <see cref="_gate"/>.</remarks>
    private void PurgeExpiredQuarantineUnlocked()
    {
        var now = _timeProvider.GetUtcNow().UtcTicks;
        List<byte>? expired = null;
        foreach (var pair in _quarantineExpiryTicks)
        {
            if (pair.Value <= now)
            {
                expired ??= [];
                expired.Add(pair.Key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var id in expired)
        {
            _quarantineExpiryTicks.Remove(id);
            if (!_inUse.Contains(id))
            {
                _free.Enqueue(id);
            }
        }
    }
}
