using Orleans.Caching;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Conservatively classifies silos which are absent from cluster membership.
/// </summary>
internal sealed class UnknownSiloStatusCache
{
    private const int CacheCapacity = 1_024;
    private readonly ConcurrentLruCache<SiloAddress, UnknownSiloEntry> _unknownSilos = new(CacheCapacity);
    private readonly object _lock = new();
    private long _refreshEpoch;

    public SiloStatus GetSiloStatus(ClusterMembershipSnapshot snapshot, SiloAddress siloAddress)
    {
        var status = snapshot.GetSiloStatus(siloAddress);
        if (status != SiloStatus.None)
        {
            _unknownSilos.TryRemove(siloAddress);
            return status;
        }

        lock (_lock)
        {
            if (!_unknownSilos.TryGet(siloAddress, out var entry))
            {
                _unknownSilos.AddOrUpdate(siloAddress, new(_refreshEpoch));
                return SiloStatus.None;
            }

            return entry.IsDead ? SiloStatus.Dead : SiloStatus.None;
        }
    }

    public long OnFullRefreshStarted()
    {
        lock (_lock)
        {
            return ++_refreshEpoch;
        }
    }

    public void OnFullRefreshCompleted(long refreshEpoch, ClusterMembershipSnapshot snapshot)
    {
        lock (_lock)
        {
            foreach (var (siloAddress, entry) in _unknownSilos)
            {
                var status = snapshot.GetSiloStatus(siloAddress);
                if (status != SiloStatus.None)
                {
                    _unknownSilos.TryRemove(siloAddress);
                }
                else if (refreshEpoch > entry.ObservedAtRefreshEpoch)
                {
                    _unknownSilos.AddOrUpdate(siloAddress, entry.AsDead());
                }
            }
        }
    }

    private readonly record struct UnknownSiloEntry(long ObservedAtRefreshEpoch, bool IsDead = false)
    {
        public UnknownSiloEntry AsDead() => new(ObservedAtRefreshEpoch, IsDead: true);
    }
}
