using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Runs in every silo of a cluster whose partitions are on shared storage: assigns each partition whose host Orleans
/// membership has declared dead, or no longer knows, or which no registered host serves, to an active silo registered
/// for automatic hosting; and, every <c>balancingInterval</c>, moves one partition from the active registered silo
/// hosting the most partitions to the one hosting the fewest while their counts differ by two or more.
/// </summary>
/// <remarks>
/// <para>
/// Hosts are silo addresses (<see cref="SiloAddress.ToParsableString"/>), or the explicit identifiers of silos
/// configured with <see cref="GarnetJournalStorageOptions.HostId"/>. Every host registers in the shared
/// <see cref="IGarnetJournalHostRegistry"/> before it serves, so the agent assigns partitions only to active silos
/// registered under their addresses, which serve assignments under those addresses; leaves partitions of registered
/// explicit hosts in place, since membership cannot judge them; and reassigns a partition whose host is neither, such
/// as a placeholder in the initial map.
/// </para>
/// <para>
/// Every silo with automatic hosting runs this agent; the range map's compare-and-swap lets exactly one assignment of
/// each partition per epoch succeed, and the new host's takeover of the partition's storage fences the previous host,
/// so the agent needs no election and tolerates a wrong verdict.
/// </para>
/// </remarks>
internal sealed class GarnetJournalFailoverAgent(
    GarnetJournalHostingCoordinator coordinator,
    IClusterMembershipService membership,
    IGarnetJournalHostRegistry registry,
    TimeSpan interval,
    ILogger? logger,
    TimeSpan? balancingInterval = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, UnknownHost> _unknown = new(StringComparer.Ordinal);
    private long _refreshes;
    private Task? _run;
    private long _nextBalance = Environment.TickCount64 + (long)(balancingInterval ?? TimeSpan.Zero).TotalMilliseconds;

    public void Start() => _run = Task.Run(() => RunAsync(_stop.Token));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_run is { } run)
        {
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stop.Dispose();
    }

    /// <summary>Assigns every partition which needs a host, by the current membership snapshot.</summary>
    internal async Task<int> AssignAsync(CancellationToken cancellationToken)
    {
        // While some host is unknown to membership, read the membership table, so its verdict does not depend on the
        // membership version advancing, which it does not in a stable cluster.
        if (_unknown.Count > 0)
        {
            await membership.Refresh(cancellationToken: cancellationToken).ConfigureAwait(false);
            _refreshes++;
        }

        var snapshot = membership.CurrentSnapshot;

        // Read the registry after the snapshot: a silo registers before it becomes active, so every active silo in the
        // snapshot which serves under its address is in the registry.
        var registered = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        var active = snapshot.Members.Values
            .Where(member => member.Status == SiloStatus.Active)
            .Select(member => member.SiloAddress.ToParsableString())
            .Where(registered.Contains)
            .ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dead = new HashSet<string>(StringComparer.Ordinal);
        var assigned = await coordinator.AssignAsync(host => NeedsHost(snapshot, registered, host, seen, dead), active, cancellationToken).ConfigureAwait(false);

        // Forget hosts which no longer host partitions, which membership knows again, or whose partitions were just
        // reassigned: the coordinator returns once every partition of a host it found dead has a new host.
        foreach (var host in _unknown.Keys.Where(host => !seen.Contains(host) || dead.Contains(host)).ToList())
        {
            _unknown.Remove(host);
        }

        // Each run of a silo registers a new address; drop the registrations of silos membership has declared dead.
        foreach (var host in registered)
        {
            if (SiloAddress.TryParse(host, out var silo) && snapshot.GetSiloStatus(silo) == SiloStatus.Dead)
            {
                await registry.UnregisterAsync(host, cancellationToken).ConfigureAwait(false);
            }
        }

        if (assigned == 0 && balancingInterval is { } balance && balance != Timeout.InfiniteTimeSpan && Environment.TickCount64 >= _nextBalance)
        {
            _nextBalance = Environment.TickCount64 + (long)balance.TotalMilliseconds;
            var serving = registered.Where(host => !SiloAddress.TryParse(host, out _)).Concat(active).ToHashSet(StringComparer.Ordinal);
            await coordinator.TryBalanceAsync(active, serving, cancellationToken).ConfigureAwait(false);
        }

        return assigned;
    }

    /// <summary>
    /// A partition needs a host if its host is a silo which membership has declared dead, or which membership no longer
    /// knows, for example because its entry was cleaned up or the cluster was redeployed with a new membership table;
    /// or if no registered host serves it: an active silo address which is not registered, whose silo serves only
    /// another identifier, or an unregistered identifier, such as a placeholder.
    /// </summary>
    /// <remarks>
    /// A silo which has just joined may be missing from this silo's snapshot, so an unknown host is dead only once a
    /// newer membership version, or a membership table read which started after the host was first found unknown, still
    /// does not know it. A refresh may join a read already in progress, so the second refresh after the host was first
    /// found unknown is the first one guaranteed to read the table afterwards.
    /// </remarks>
    private bool NeedsHost(ClusterMembershipSnapshot snapshot, IReadOnlySet<string> registered, string host, HashSet<string> seen, HashSet<string> dead)
    {
        if (!SiloAddress.TryParse(host, out var silo))
        {
            return !registered.Contains(host);
        }

        var status = snapshot.GetSiloStatus(silo);
        if (status != SiloStatus.None)
        {
            return status == SiloStatus.Dead || status == SiloStatus.Active && !registered.Contains(host);
        }

        seen.Add(host);
        if (!_unknown.TryGetValue(host, out var since))
        {
            _unknown[host] = new(snapshot.Version, _refreshes);
            return false;
        }

        if (_refreshes - since.Refreshes >= 2 || snapshot.GetSiloStatus(silo, since.Version) == SiloStatus.Dead)
        {
            dead.Add(host);
            return true;
        }

        return false;
    }

    /// <summary>When a host was first found unknown: the membership version and the number of refreshes by then.</summary>
    private readonly record struct UnknownHost(MembershipVersion Version, long Refreshes);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AssignAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Assigning Garnet journal partitions to hosts failed; retrying.");
                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
