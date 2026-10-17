using Microsoft.Extensions.DependencyInjection;
using Orleans.Placement;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace Orleans.Journaling;

/// <summary>
/// A placement strategy which activates a grain on the silo hosting the Garnet partition which stores its journal, so
/// that its journal operations reach the partition without leaving the silo.
/// </summary>
/// <remarks>
/// The grain's journal is <see cref="JournalId.FromGrainId"/> of its id, in the silo's default journal storage provider,
/// which must be Garnet journal storage. A grain whose partition's host is not a compatible silo, for example while the
/// partition fails over, is placed on a random compatible silo. Placement affects only where operations run, never
/// their outcome.
/// </remarks>
[Serializable, GenerateSerializer, Immutable, SuppressReferenceTracking]
public sealed class GarnetJournalPlacement : PlacementStrategy
{
}

/// <summary>
/// Activates the grain on the silo hosting the Garnet partition which stores its journal. See <see cref="GarnetJournalPlacement"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class GarnetJournalPlacementAttribute : PlacementAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GarnetJournalPlacementAttribute"/> class.
    /// </summary>
    public GarnetJournalPlacementAttribute()
        : base(new GarnetJournalPlacement())
    {
    }
}

/// <summary>
/// Places grains with <see cref="GarnetJournalPlacement"/> on the host of their journal's partition.
/// </summary>
/// <remarks>
/// The director maps the grain's journal to its bucket and the bucket to its owner in the silo's current view of the
/// range map. Hosts are silo addresses when partitions are on shared storage; a partition in a local data directory is
/// recognized only when this silo hosts it.
/// </remarks>
internal sealed class GarnetJournalPlacementDirector(IServiceProvider services) : IPlacementDirector
{
    private GarnetJournalStorageProvider? _provider;

    public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
    {
        var compatible = context.GetCompatibleSilos(target);
        if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatible) is { } hint)
        {
            return Task.FromResult(hint);
        }

        if (TryGetHost(target.GrainIdentity, compatible, context.LocalSilo) is { } host)
        {
            return Task.FromResult(host);
        }

        return Task.FromResult(compatible[Random.Shared.Next(compatible.Length)]);
    }

    /// <summary>Gets the compatible silo which hosts the partition owning <paramref name="grainId"/>'s journal, if any.</summary>
    internal SiloAddress? TryGetHost(GrainId grainId, SiloAddress[] compatible, SiloAddress localSilo)
    {
        var provider = _provider ??= services.GetService<IJournalStorageProvider>() as GarnetJournalStorageProvider
            ?? services.GetKeyedService<IJournalStorageProvider>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME) as GarnetJournalStorageProvider;
        if (provider?.TryGetRouter() is not { } router)
        {
            return null;
        }

        var journalId = JournalId.FromGrainId(grainId);
        var bucket = GarnetJournalKeys.GetBucket(GarnetJournalKeys.EncodeJournalId(journalId.Value), router.BucketCount);
        var map = router.View.Map;
        if (!map.Partitions.TryGetValue(map.GetOwner(bucket), out var hosting) || hosting.Endpoint is null)
        {
            return null;
        }

        if (string.Equals(hosting.Host, provider.HostId, StringComparison.Ordinal))
        {
            return Array.IndexOf(compatible, localSilo) >= 0 ? localSilo : null;
        }

        return SiloAddress.TryParse(hosting.Host, out var silo) && Array.IndexOf(compatible, silo) >= 0 ? silo : null;
    }
}
