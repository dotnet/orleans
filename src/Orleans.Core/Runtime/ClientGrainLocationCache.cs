using System.Diagnostics.CodeAnalysis;
using Orleans.Caching;
using Orleans.Runtime;

namespace Orleans;

/// <summary>
/// Caches the silos which host grains called by an external client, so that gateways can route requests directly to them.
/// </summary>
/// <remarks>
/// Locations are learned from the cache invalidation headers on responses and are removed when the cached silo rejects a request.
/// Each update replaces a cached location only when the cache holds the location which the update supersedes, or holds none,
/// so newer locations survive delayed updates. Writers are serialized, while reads use the concurrent cache directly.
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cache has no expiration timer, so disposing it releases no resources.")]
internal sealed class ClientGrainLocationCache
{
    private const int Capacity = 8192;
    private readonly ConcurrentLruCache<GrainId, SiloAddress> _locations = new(Capacity);

    /// <summary>
    /// Gets the cached location of the specified grain.
    /// </summary>
    public bool TryGetLocation(GrainId grainId, [NotNullWhen(true)] out SiloAddress? location) => _locations.TryGet(grainId, out location);

    /// <summary>
    /// Updates cached locations using a response to a request sent by this client.
    /// </summary>
    public void OnResponse(Message response)
    {
        if (response.CacheInvalidationHeader is { } updates)
        {
            foreach (var update in updates)
            {
                Replace(update.GrainId, update.InvalidSiloAddress, update.ValidGrainAddress?.SiloAddress);
            }
        }
        else if (response.Result is Message.ResponseTypes.Rejection && response.SendingSilo is { } rejectingSilo)
        {
            // The silo which the request was addressed to did not serve it.
            Replace(response.SendingGrain, rejectingSilo, replacement: null);
        }
    }

    private void Replace(GrainId grainId, SiloAddress? superseded, SiloAddress? replacement)
    {
        // Serialize the conditional remove/add across responses from different gateways.
        lock (_locations)
        {
            if (superseded is not null)
            {
                _locations.TryRemove(grainId, static (cached, superseded) => cached.Equals(superseded), superseded);
            }

            if (replacement is not null)
            {
                _locations.TryAdd(grainId, replacement);
            }
        }
    }
}
