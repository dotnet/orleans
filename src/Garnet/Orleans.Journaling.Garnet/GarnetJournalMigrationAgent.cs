using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// Runs in each silo which hosts partitions: drives the migrations into those partitions, drops their expired
/// tombstones and absent markers, and, in the silo hosting the partition with the lowest id, runs the balancer.
/// </summary>
/// <remarks>
/// The destination's host drives a migration, sealing its source and reclaiming it remotely, so every migration has
/// one driver while its destination's host is up. A second driver, for example a host which has not observed that its
/// partition moved, or a test, is harmless: every step is an idempotent partition procedure followed by a MetaStore
/// compare-and-swap (see <see cref="GarnetJournalMigrationCoordinator"/>).
/// </remarks>
internal sealed class GarnetJournalMigrationAgent : IAsyncDisposable
{
    private readonly GarnetJournalRouter _router;
    private readonly GarnetJournalMigrationCoordinator _coordinator;
    private readonly Func<PartitionId, bool> _isHosted;
    private readonly GarnetJournalStorageOptions _options;
    private readonly ILogger? _logger;
    private readonly GarnetJournalBalancer? _balancer;
    private readonly CancellationTokenSource _stop = new();
    private Task[] _loops = [];

    public GarnetJournalMigrationAgent(
        GarnetJournalRouter router,
        GarnetJournalMigrationCoordinator coordinator,
        Func<PartitionId, bool> isHosted,
        GarnetJournalStorageOptions options,
        ILogger? logger)
    {
        _router = router;
        _coordinator = coordinator;
        _isHosted = isHosted;
        _options = options;
        _logger = logger;
        if (options.BalancingInterval != Timeout.InfiniteTimeSpan)
        {
            _balancer = new GarnetJournalBalancer(router, coordinator, GarnetJournalBalancerSettings.From(options), logger);
        }
    }

    /// <summary>Gets the balancer this agent runs while its silo hosts the partition with the lowest id, if balancing is enabled.</summary>
    internal GarnetJournalBalancer? Balancer => _balancer;

    /// <summary>
    /// Starts the agent's loops: one drives handovers, one consolidates and reclaims, one collects markers, and one
    /// balances, so neither a long consolidation, a long collection, nor a balancing round delays a handover, during
    /// which its bucket is unavailable.
    /// </summary>
    public void Start()
    {
        List<Task> loops =
        [
            Task.Run(() => RunAsync(MigrationWork.Handover, _stop.Token)),
            Task.Run(() => RunAsync(MigrationWork.Completion, _stop.Token)),
            Task.Run(() => CollectAsync(_stop.Token)),
        ];
        if (_balancer is { } balancer)
        {
            loops.Add(Task.Run(() => BalanceAsync(balancer, _stop.Token)));
        }

        _loops = [.. loops];
    }

    /// <summary>Runs a balancing round every <see cref="GarnetJournalStorageOptions.BalancingInterval"/> while this silo hosts the partition with the lowest id (D37).</summary>
    private async Task BalanceAsync(GarnetJournalBalancer balancer, CancellationToken cancellationToken)
    {
        var time = _router.TimeProvider;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.BalancingInterval, time, cancellationToken).ConfigureAwait(false);
                if (GarnetJournalBalancer.IsBalancer(_router.View.Map, _isHosted))
                {
                    await balancer.RunOnceAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Garnet journal balancing round failed; retrying.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var loop in _loops)
        {
            await loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stop.Dispose();
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        var time = _router.TimeProvider;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.MarkerCollectionInterval, time, cancellationToken).ConfigureAwait(false);
                await CollectMarkersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Garnet journal marker collection failed; retrying.");
            }
        }
    }

    private async Task RunAsync(MigrationWork work, CancellationToken cancellationToken)
    {
        var time = _router.TimeProvider;
        while (!cancellationToken.IsCancellationRequested)
        {
            var progress = false;
            try
            {
                progress = await _coordinator.AdvanceAsync(_isHosted, work, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Garnet journal migration pass failed; retrying.");
            }

            if (!progress)
            {
                try
                {
                    await Task.Delay(_options.MigrationInterval, time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Drops absent markers, and tombstones older than <see cref="GarnetJournalStorageOptions.TombstoneRetention"/> by
    /// this silo's clock, from the buckets the hosted partitions own, raising each bucket's collection generation (D5,
    /// D25). The clock decides only when tombstones are dropped; the generation, which partitions assign, decides which
    /// retries are rejected.
    /// </summary>
    internal async Task<int> CollectMarkersAsync(CancellationToken cancellationToken)
    {
        var view = _router.View;
        var cutoff = _router.TimeProvider.GetUtcNow() - _options.TombstoneRetention;
        var dropped = 0;
        foreach (var range in view.Map.Ranges)
        {
            if (!_isHosted(range.Owner))
            {
                continue;
            }

            for (var bucket = range.FirstBucket; bucket <= range.LastBucket; bucket++)
            {
                dropped += await _router.Migrator.DropMarkersAsync(range.Owner, bucket, cutoff, cancellationToken).ConfigureAwait(false);
            }
        }

        return dropped;
    }
}
