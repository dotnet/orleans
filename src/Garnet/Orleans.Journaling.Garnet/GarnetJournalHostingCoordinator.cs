using System.IO.Hashing;
using System.Text;
using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// Assigns partitions to hosts in the range map: fails partitions over from hosts which have died, and places
/// partitions which have no host.
/// </summary>
/// <remarks>
/// <para>
/// Each assignment is a compare-and-swap of the range map which gives the partition a new host and increments its
/// hosting epoch, and clears its endpoint until the new host publishes one. The new host takes the partition's storage
/// over at the new epoch, which fences the previous host before recovery reads the storage (D12), so an assignment
/// never needs the previous host to have stopped: a crashed, partitioned, or duplicated coordinator, or a wrong
/// verdict that a host has died, costs availability, never safety.
/// </para>
/// <para>
/// Only partitions on shared storage can move between hosts; a partition in a silo's local data directory stays with
/// that silo.
/// </para>
/// </remarks>
internal sealed class GarnetJournalHostingCoordinator(IRangeMetaStore metaStore, ILogger? logger = null)
{
    /// <summary>
    /// Assigns <paramref name="partition"/> to <paramref name="host"/> at the next epoch, if its hosting epoch is still
    /// <paramref name="expectedEpoch"/>.
    /// </summary>
    /// <returns>The written view, or <see langword="null"/> if the partition's epoch is no longer <paramref name="expectedEpoch"/>.</returns>
    public async Task<RangeMapSnapshot?> TryAssignAsync(PartitionId partition, long expectedEpoch, string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        while (true)
        {
            var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!view.Map.Partitions.TryGetValue(partition, out var hosting))
            {
                throw new ArgumentException($"Partition '{partition}' is not in the range map.", nameof(partition));
            }

            if (hosting.Epoch != expectedEpoch)
            {
                return null;
            }

            if (await TryWriteAsync(view, [(partition, host)], cancellationToken).ConfigureAwait(false) is { } written)
            {
                return written;
            }
        }
    }

    /// <summary>
    /// Assigns every partition whose host <paramref name="needsHost"/> rejects to one of <paramref name="hosts"/>,
    /// preferring the hosts with the fewest partitions.
    /// </summary>
    /// <returns>The number of partitions assigned.</returns>
    public async Task<int> AssignAsync(Func<string, bool> needsHost, IReadOnlyCollection<string> hosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(needsHost);
        ArgumentNullException.ThrowIfNull(hosts);
        if (hosts.Count == 0)
        {
            return 0;
        }

        while (true)
        {
            var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var orphans = view.Map.Partitions.Where(entry => needsHost(entry.Value.Host)).Select(entry => entry.Key).ToList();
            if (orphans.Count == 0)
            {
                return 0;
            }

            var load = hosts.ToDictionary(host => host, host => view.Map.Partitions.Values.Count(hosting => hosting.Host == host), StringComparer.Ordinal);
            var assignments = new List<(PartitionId, string)>();
            foreach (var partition in orphans)
            {
                // Concurrent coordinators make the same choice for the same view; ties go to a pseudo-random host per partition.
                var host = load.OrderBy(entry => entry.Value).ThenBy(entry => Rank(partition, entry.Key)).First().Key;
                load[host]++;
                assignments.Add((partition, host));
            }

            if (await TryWriteAsync(view, assignments, cancellationToken).ConfigureAwait(false) is not null)
            {
                return assignments.Count;
            }
        }
    }

    private static ulong Rank(PartitionId partition, string host) => XxHash64.HashToUInt64(Encoding.UTF8.GetBytes($"{partition.Value}\n{host}"));

    /// <summary>
    /// Moves one partition from the host with the most partitions to the one with the fewest, if their counts differ by
    /// two or more and every partition is served by one of <paramref name="servingHosts"/>.
    /// </summary>
    /// <remarks>
    /// A move is a planned failover: the new host takes the partition's storage over, which fences the previous host, so
    /// the partition is unavailable from the assignment until the new host publishes its endpoint. Requiring every
    /// partition to be served first moves one partition at a time: the move commits by compare-and-swap against the view
    /// in which every partition served, so any intervening write, including another coordinator's move, rejects it.
    /// </remarks>
    /// <returns>The partition moved, or <see langword="null"/>.</returns>
    public async Task<PartitionId?> TryBalanceAsync(IReadOnlyCollection<string> hosts, IReadOnlySet<string> servingHosts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        ArgumentNullException.ThrowIfNull(servingHosts);
        if (hosts.Count < 2)
        {
            return null;
        }

        var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var partitions = view.Map.Partitions;
        var set = hosts.ToHashSet(StringComparer.Ordinal);
        if (partitions.Values.Any(hosting => hosting.Endpoint is null || !servingHosts.Contains(hosting.Host)))
        {
            return null;
        }

        var counts = set.ToDictionary(host => host, host => partitions.Values.Count(hosting => hosting.Host == host), StringComparer.Ordinal);
        var busiest = counts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).First();
        var idlest = counts.OrderBy(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal).First();
        if (busiest.Value - idlest.Value < 2)
        {
            return null;
        }

        // Commit against the view whose readiness and counts were checked, so any intervening write, such as another
        // agent's move of a different partition, rejects this plan rather than overlapping two outages.
        var (partition, _) = partitions.Where(entry => entry.Value.Host == busiest.Key).MinBy(entry => Rank(entry.Key, idlest.Key));
        if (await TryWriteAsync(view, [(partition, idlest.Key)], cancellationToken).ConfigureAwait(false) is null)
        {
            return null;
        }

        logger?.LogInformation("Moved Garnet journal partition {Partition} from host {From} to host {To} to balance placement.", partition, busiest.Key, idlest.Key);
        return partition;
    }

    private async Task<RangeMapSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var view = await metaStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Garnet journal range map was removed from its MetaStore.");
        view.Map.Validate();
        return view;
    }

    /// <returns>The written view, or <see langword="null"/> if another write intervened or the outcome is unknown, in which case the caller reads the map and retries.</returns>
    private async Task<RangeMapSnapshot?> TryWriteAsync(RangeMapSnapshot view, List<(PartitionId Partition, string Host)> assignments, CancellationToken cancellationToken)
    {
        var partitions = view.Map.Partitions;
        foreach (var (partition, host) in assignments)
        {
            var hosting = partitions[partition];
            partitions = partitions.SetItem(partition, hosting with { Host = host, Epoch = hosting.Epoch + 1, Endpoint = null });
        }

        RangeMapWriteResult result;
        try
        {
            result = await metaStore.TryWriteAsync(view.Map with { Partitions = partitions }, view.Version, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            // The write's outcome is unknown (D24); the caller recomputes from the map it reads next.
            logger?.LogDebug(exception, "The outcome of a Garnet journal hosting assignment is unknown.");
            return null;
        }

        if (!result.Written)
        {
            return null;
        }

        foreach (var (partition, host) in assignments)
        {
            logger?.LogInformation(
                "Assigned Garnet journal partition {Partition} to host {Host} at epoch {Epoch} in range map version {Version}.",
                partition,
                host,
                partitions[partition].Epoch,
                result.Current!.Version);
        }

        return result.Current;
    }
}
