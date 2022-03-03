using Orleans;
using Orleans.Runtime;

namespace GPSTracker.GrainImplementation;

public class HubListGrain : Grain, IHubListGrain
{
    private readonly IClusterMembershipService _clusterMembership;
    private readonly Dictionary<SiloAddress, IRemoteLocationHub> _hubs = new();
    private MembershipVersion _cacheMembershipVersion;
    private List<(SiloAddress Host, IRemoteLocationHub Hub)>? _cache;

    public HubListGrain(IClusterMembershipService clusterMembershipService)
    {
        _clusterMembership = clusterMembershipService;
    }

    public ValueTask AddHub(SiloAddress host, IRemoteLocationHub hubReference)
    {
        // Invalidate the cache.
        _cache = null;
        _hubs[host] = hubReference;

        return default;
    }

    public ValueTask<List<(SiloAddress Host, IRemoteLocationHub Hub)>> GetHubs() =>
        new(GetCachedHubs());

    private List<(SiloAddress Host, IRemoteLocationHub Hub)> GetCachedHubs()
    {
        // Returns a cached list of hubs if the cache is valid, otherwise builds a list of hubs.
        var clusterMembers = _clusterMembership.CurrentSnapshot;
        if (_cache is { } && clusterMembers.Version == _cacheMembershipVersion)
        {
            return _cache;
        }

        // Filter out hosts which are not yet active or have been removed from the cluster.
        var hubs = new List<(SiloAddress Host, IRemoteLocationHub Hub)>();
        var toDelete = new List<SiloAddress>();
        foreach (var pair in _hubs)
        {
            var host = pair.Key;
            var hubRef = pair.Value;
            var hostStatus = clusterMembers.GetSiloStatus(host);
            if (hostStatus == SiloStatus.Dead)
            {
                toDelete.Add(host);
            }

            if (hostStatus == SiloStatus.Active)
            {
                hubs.Add((host, hubRef));
            }
        }

        foreach (var host in toDelete)
        {
            _hubs.Remove(host);
        }

        _cache = hubs;
        _cacheMembershipVersion = clusterMembers.Version;
        return hubs;
    }
}
