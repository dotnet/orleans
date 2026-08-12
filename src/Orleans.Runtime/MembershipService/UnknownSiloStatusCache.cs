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
            return ObserveUnknownSilo(siloAddress, snapshot.Version);
        }
    }

    public void Observe(ClusterMembershipSnapshot snapshot)
    {
        lock (_lock)
        {
            foreach (var (siloAddress, _) in _unknownSilos)
            {
                var status = snapshot.GetSiloStatus(siloAddress);
                if (status != SiloStatus.None)
                {
                    _unknownSilos.TryRemove(siloAddress);
                }
                else
                {
                    ObserveUnknownSilo(siloAddress, snapshot.Version);
                }
            }
        }
    }

    private SiloStatus ObserveUnknownSilo(SiloAddress siloAddress, MembershipVersion version)
    {
        if (!_unknownSilos.TryGet(siloAddress, out var entry))
        {
            _unknownSilos.AddOrUpdate(siloAddress, new(version));
            return SiloStatus.None;
        }

        if (entry.IsDead)
        {
            return SiloStatus.Dead;
        }

        if (version <= entry.LastObservedVersion)
        {
            return SiloStatus.None;
        }

        if (!entry.HasCausalBarrier)
        {
            // This snapshot might have come from a refresh which was already in flight when the
            // silo was first observed as unknown. Require another newer snapshot before declaring it dead.
            _unknownSilos.AddOrUpdate(siloAddress, entry.WithCausalBarrier(version));
            return SiloStatus.None;
        }

        _unknownSilos.AddOrUpdate(siloAddress, entry.AsDead());
        return SiloStatus.Dead;
    }

    private readonly record struct UnknownSiloEntry(
        MembershipVersion LastObservedVersion,
        bool HasCausalBarrier = false,
        bool IsDead = false)
    {
        public UnknownSiloEntry WithCausalBarrier(MembershipVersion version) => new(version, HasCausalBarrier: true);

        public UnknownSiloEntry AsDead() => new(LastObservedVersion, HasCausalBarrier: true, IsDead: true);
    }
}
