using Microsoft.Extensions.Logging;
using Orleans.Journaling.Ranges;

namespace Orleans.Journaling;

/// <summary>
/// Drives bucket migrations: commits moves, seals sources, opens destinations, consolidates, and reclaims sources.
/// </summary>
/// <remarks>
/// <para>
/// Each step runs an idempotent partition procedure and then records, by compare-and-swap in the MetaStore, the phase
/// it observed in the partitions (<see cref="RecordObservedAsync"/>). Phases advance only from observed partition
/// state, and the next step of a migration starts only from its recorded phase, so a crashed coordinator leaves work
/// which any coordinator resumes, and duplicated coordinators repeat procedures which report success if already applied.
/// </para>
/// <para>
/// The handover of a bucket from A to B at version V: commit V, assigning the bucket to B with a migration in phase
/// <see cref="MigrationPhase.Committed"/>; A seals the bucket at V (<see cref="MigrationPhase.Sealed"/>); B opens it at
/// V with source A, only once the seal is recorded (<see cref="MigrationPhase.Opened"/>); B consolidates the bucket's
/// journals from its migration chain and marks it done (<see cref="MigrationPhase.Done"/>); the migration and every
/// earlier one of the bucket are then <see cref="MigrationPhase.Complete"/>, their sources reclaim their copies, and
/// the migrations are removed.
/// </para>
/// <para>
/// A bucket is unavailable between its seal and its open, so handovers never wait for other migrations: each bucket's
/// steps record their phases independently, a handover proceeds to its open as soon as its seal is recorded, and the
/// agent drives handovers (<see cref="MigrationWork.Handover"/>) separately from consolidation and reclamation
/// (<see cref="MigrationWork.Completion"/>), which can take long.
/// </para>
/// </remarks>
internal sealed class GarnetJournalMigrationCoordinator(GarnetJournalRouter router, ILogger? logger = null)
{
    private const int Concurrency = 32;

    private GarnetJournalMigrator Migrator => router.Migrator;

    /// <summary>
    /// Commits a view which moves <paramref name="buckets"/> to <paramref name="destination"/>, following the
    /// coordinator rule (D11): each bucket's previous move has reached <see cref="MigrationPhase.Opened"/>, and the
    /// destination holds no copy of the bucket, or has reclaimed it.
    /// </summary>
    public async Task<MoveResult> TryMoveAsync(IEnumerable<int> buckets, PartitionId destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        var requested = buckets.Distinct().Order().ToArray();
        while (true)
        {
            var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!view.Map.Partitions.ContainsKey(destination))
            {
                throw new ArgumentException($"Partition '{destination}' is not in the range map.", nameof(destination));
            }

            var editor = new RangeMapBuckets(view.Map);
            var moving = requested.Where(bucket => editor.GetOwner(bucket) != destination).ToList();
            if (moving.Count == 0)
            {
                return new(MoveStatus.AlreadyOwned, view);
            }

            if (moving.Any(bucket => editor.GetLatestMigration(bucket) is { Phase: < MigrationPhase.Opened }))
            {
                return new(MoveStatus.PreviousMoveNotOpened, view);
            }

            // Each migration still in the map adds a source to the chain which reads of the bucket follow; completed
            // migrations are removed once their sources are reclaimed. The chain must stay within the traversal bound.
            if (moving.Any(bucket => editor.GetMigrations(bucket).Count >= Migrator.MaxChainLength))
            {
                return new(MoveStatus.ChainTooLong, view);
            }

            // The owner must hold the bucket open: a partition opens its initial buckets when it first starts, and a
            // bucket moved before then would never be sealed. Once open, a bucket stays open until a move seals it.
            var owners = await Task.WhenAll(moving.Select(bucket => Migrator.GetBucketAsync(editor.GetOwner(bucket), bucket, cancellationToken))).ConfigureAwait(false);
            if (owners.Any(state => state.State != GarnetJournalProtocol.OpenStateName))
            {
                return new(MoveStatus.PreviousMoveNotOpened, view);
            }

            // The destination must not hold a copy of a bucket which a read path can reach; a copy is reachable until it
            // is reclaimed. A copy cannot appear between this check and the commit: only a commit which moves the
            // bucket to the destination makes it open the bucket, and such a commit fails this commit's compare-and-swap.
            var states = await Task.WhenAll(moving.Select(bucket => Migrator.GetBucketAsync(destination, bucket, cancellationToken))).ConfigureAwait(false);
            if (states.Any(state => !state.IsAbsentOrReclaimed))
            {
                return new(MoveStatus.DestinationHoldsCopy, view);
            }

            foreach (var bucket in moving)
            {
                editor.Move(bucket, destination, view.Version + 1);
            }

            if (await TryWriteAsync(editor.ToMap(), view, cancellationToken).ConfigureAwait(false) is { } written)
            {
                logger?.LogInformation("Committed range map version {Version}, moving {Count} Garnet journal buckets to partition {Destination}.", written.Version, moving.Count, destination);
                return new(MoveStatus.Committed, written);
            }
        }
    }

    /// <summary>
    /// Advances every migration whose destination matches <paramref name="filter"/> by one step, recording the phases
    /// the partitions reached.
    /// </summary>
    /// <returns><see langword="true"/> if the range map changed.</returns>
    public Task<bool> AdvanceAsync(Func<PartitionId, bool>? filter, CancellationToken cancellationToken)
        => AdvanceAsync(filter, MigrationWork.All, cancellationToken);

    /// <summary>
    /// Advances every migration whose destination matches <paramref name="filter"/> by the steps <paramref name="work"/>
    /// selects. Each bucket records the phases its partitions reached on its own, and a handover continues until its
    /// destination has opened, so no bucket waits for another's steps.
    /// </summary>
    /// <returns><see langword="true"/> if the range map changed.</returns>
    public async Task<bool> AdvanceAsync(Func<PartitionId, bool>? filter, MigrationWork work, CancellationToken cancellationToken)
    {
        var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var editor = new RangeMapBuckets(view.Map);
        var buckets = new List<int>();
        for (var bucket = 0; bucket < editor.BucketCount; bucket++)
        {
            var steps = editor.GetMigrations(bucket);
            if (steps.Count > 0 && (filter is null || filter(steps[^1].Destination)) && HasWork(steps, work))
            {
                buckets.Add(bucket);
            }
        }

        if (buckets.Count == 0)
        {
            return false;
        }

        // Handovers take the first slots, so they never queue behind consolidations.
        buckets.Sort((left, right) => IsHandover(editor.GetMigrations(right)).CompareTo(IsHandover(editor.GetMigrations(left))));
        var changed = 0;
        await Parallel.ForEachAsync(
            buckets,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellationToken },
            async (bucket, token) =>
            {
                if (await AdvanceBucketAsync(bucket, editor.GetMigrations(bucket), filter, work, token).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref changed, 1);
                }
            }).ConfigureAwait(false);
        return changed != 0;
    }

    /// <summary>
    /// Runs a bucket's next step and records the phase it reached, repeating while a handover advances, so the
    /// destination opens as soon as the seal is recorded.
    /// </summary>
    private async Task<bool> AdvanceBucketAsync(int bucket, IReadOnlyList<MigrationStep> steps, Func<PartitionId, bool>? filter, MigrationWork work, CancellationToken cancellationToken)
    {
        var changed = false;
        while (true)
        {
            await StepAsync(bucket, steps, work, cancellationToken).ConfigureAwait(false);
            if (!await RecordObservedAsync([bucket], cancellationToken).ConfigureAwait(false))
            {
                return changed;
            }

            changed = true;
            steps = new RangeMapBuckets(router.View.Map).GetMigrations(bucket);
            if (work == MigrationWork.Completion || !IsHandover(steps) || filter is not null && !filter(steps[^1].Destination))
            {
                return changed;
            }
        }
    }

    private static bool IsHandover(IReadOnlyList<MigrationStep> steps) => steps.Count > 0 && steps[^1].Phase < MigrationPhase.Opened;

    private static bool HasWork(IReadOnlyList<MigrationStep> steps, MigrationWork work) => work switch
    {
        MigrationWork.Handover => IsHandover(steps),
        MigrationWork.Completion => !IsHandover(steps) || steps.Any(step => step.Phase == MigrationPhase.Complete),
        _ => true,
    };

    /// <summary>Runs every migration step until the range map has no migrations, or <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task CompleteAllAsync(CancellationToken cancellationToken)
    {
        while ((await ReadAsync(cancellationToken).ConfigureAwait(false)).Map.Migrations.Length > 0)
        {
            if (!await AdvanceAsync(filter: null, cancellationToken).ConfigureAwait(false))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Records, by compare-and-swap, the migration phases the partitions have reached for <paramref name="buckets"/>:
    /// a source sealed at a migration's version makes it <see cref="MigrationPhase.Sealed"/>; a destination open at that
    /// version, <see cref="MigrationPhase.Opened"/>; and done, <see cref="MigrationPhase.Done"/>. A done migration
    /// completes it and every earlier migration of the bucket, and a completed migration whose source has reclaimed and
    /// purged its copy is removed.
    /// </summary>
    /// <returns><see langword="true"/> if the range map changed.</returns>
    public async Task<bool> RecordObservedAsync(IReadOnlyCollection<int> buckets, CancellationToken cancellationToken)
    {
        while (true)
        {
            var view = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var editor = new RangeMapBuckets(view.Map);
            var queries = new HashSet<(PartitionId Partition, int Bucket)>();
            foreach (var bucket in buckets)
            {
                var steps = editor.GetMigrations(bucket);
                if (steps.Count == 0)
                {
                    continue;
                }

                queries.Add((steps[^1].Source, bucket));
                queries.Add((steps[^1].Destination, bucket));
                foreach (var step in steps)
                {
                    queries.Add((step.Source, bucket));
                }
            }

            // An unavailable partition leaves only the buckets which need its state unobserved.
            var states = new Dictionary<(PartitionId Partition, int Bucket), BucketState>();
            await Parallel.ForEachAsync(
                queries,
                new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellationToken },
                async (query, token) =>
                {
                    if (await Migrator.TryGetBucketAsync(query.Partition, query.Bucket, token).ConfigureAwait(false) is { } state)
                    {
                        lock (states)
                        {
                            states[query] = state;
                        }
                    }
                }).ConfigureAwait(false);

            var changed = false;
            foreach (var bucket in buckets)
            {
                changed |= Observe(editor, bucket, states);
            }

            if (!changed)
            {
                return false;
            }

            if (await TryWriteAsync(editor.ToMap(), view, cancellationToken).ConfigureAwait(false) is not null)
            {
                return true;
            }
        }
    }

    private static bool Observe(RangeMapBuckets editor, int bucket, Dictionary<(PartitionId, int), BucketState> states)
    {
        var steps = editor.GetMigrations(bucket);
        if (steps.Count == 0)
        {
            return false;
        }

        var changed = false;
        var latest = steps[^1];
        var phase = latest.Phase;
        if (phase == MigrationPhase.Committed
            && states.TryGetValue((latest.Source, bucket), out var source)
            && source.State == GarnetJournalProtocol.SealedStateName && source.Version == latest.StartedAtVersion)
        {
            phase = MigrationPhase.Sealed;
        }

        if (states.TryGetValue((latest.Destination, bucket), out var destination)
            && destination.State == GarnetJournalProtocol.OpenStateName && destination.Version == latest.StartedAtVersion)
        {
            if (phase == MigrationPhase.Sealed)
            {
                phase = MigrationPhase.Opened;
            }

            if (phase == MigrationPhase.Opened && destination.Done)
            {
                phase = MigrationPhase.Done;
            }
        }

        if (phase != latest.Phase)
        {
            changed |= editor.TryAdvance(bucket, latest, phase);
        }

        // A done migration ends every read path at its destination, so its source and every earlier source of the
        // bucket are unreachable (D42).
        steps = editor.GetMigrations(bucket);
        var lastDone = -1;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Phase >= MigrationPhase.Done)
            {
                lastDone = i;
            }
        }

        for (var i = 0; i <= lastDone; i++)
        {
            changed |= editor.TryAdvance(bucket, steps[i], MigrationPhase.Complete);
        }

        // Migrations are removed oldest first, so the remaining ones still form a chain ending at the bucket's owner.
        foreach (var step in editor.GetMigrations(bucket).ToList())
        {
            // The source's copy, sealed at the migration's version, is reclaimed and purged, or the source has since
            // reopened the bucket, which it does only after reclaiming and purging that copy.
            if (step.Phase != MigrationPhase.Complete
                || !states.TryGetValue((step.Source, bucket), out var reclaimed)
                || !(reclaimed.Reclaimed && reclaimed.IndexCount == 0 && reclaimed.MarkerCount == 0 && reclaimed.Version == step.StartedAtVersion
                    || reclaimed.Version > step.StartedAtVersion))
            {
                break;
            }

            changed |= editor.TryRemove(bucket, step);
        }

        return changed;
    }

    /// <summary>Runs the next step of a bucket's migrations which <paramref name="work"/> selects, from their recorded phases.</summary>
    private async Task StepAsync(int bucket, IReadOnlyList<MigrationStep> steps, MigrationWork work, CancellationToken cancellationToken)
    {
        try
        {
            var latest = steps[^1];
            var handover = work != MigrationWork.Completion;
            var completion = work != MigrationWork.Handover;
            switch (latest.Phase)
            {
                case MigrationPhase.Committed when handover:
                    await Migrator.TransitionAsync(latest.Source, bucket, GarnetJournalProtocol.BucketSealOperation, latest.StartedAtVersion, source: null, cancellationToken).ConfigureAwait(false);
                    break;
                case MigrationPhase.Sealed when handover:
                    {
                        // The seal is recorded, so the destination opens after it (the open must follow the seal). A sealed
                        // copy collects no more markers, so its collection generation is final, and the destination inherits it.
                        var source = await Migrator.GetBucketAsync(latest.Source, bucket, cancellationToken).ConfigureAwait(false);
                        await Migrator.TransitionAsync(latest.Destination, bucket, GarnetJournalProtocol.BucketOpenOperation, latest.StartedAtVersion, latest.Source, cancellationToken, source.CollectionGeneration).ConfigureAwait(false);
                        break;
                    }

                case MigrationPhase.Opened when completion:
                    await Migrator.ConsolidateAsync(bucket, latest.Destination, cancellationToken).ConfigureAwait(false);
                    break;
            }

            foreach (var step in steps)
            {
                if (completion && step.Phase == MigrationPhase.Complete)
                {
                    await Migrator.ReclaimAsync(step.Source, bucket, step.StartedAtVersion, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is TimeoutException || GarnetJournalRouter.IsTransient(exception))
        {
            // A partition is unavailable; a later pass retries from the recorded phase.
            logger?.LogDebug(exception, "Migration step for Garnet journal bucket {Bucket} did not complete.", bucket);
        }
    }

    private async Task<RangeMapSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var view = await router.MetaStore.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Garnet journal range map was removed from its MetaStore.");
        router.TryApplyView(view);
        return view;
    }

    /// <returns>The written snapshot, or <see langword="null"/> if another write intervened or the outcome is unknown, in which case the caller reads the map and retries.</returns>
    private async Task<RangeMapSnapshot?> TryWriteAsync(RangeMap next, RangeMapSnapshot view, CancellationToken cancellationToken)
    {
        RangeMapWriteResult result;
        try
        {
            result = await router.MetaStore.TryWriteAsync(next, view.Version, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            // The write's outcome is unknown (D24). Every change is recomputed from the map read next, so it is
            // applied at most once.
            logger?.LogDebug(exception, "The outcome of a Garnet journal range map write is unknown.");
            return null;
        }

        if (result.Current is { } current)
        {
            router.TryApplyView(current);
        }

        return result.Written ? result.Current : null;
    }
}

/// <summary>The migration steps which <see cref="GarnetJournalMigrationCoordinator.AdvanceAsync(Func{PartitionId, bool}?, MigrationWork, CancellationToken)"/> drives.</summary>
internal enum MigrationWork
{
    /// <summary>Every step.</summary>
    All,

    /// <summary>Sealing sources and opening destinations: the steps between which a bucket is unavailable.</summary>
    Handover,

    /// <summary>Consolidating destinations, recording completion, and reclaiming sources.</summary>
    Completion,
}

/// <summary>The outcome of <see cref="GarnetJournalMigrationCoordinator.TryMoveAsync"/>.</summary>
/// <param name="Status">Whether the move was committed, and if not, why.</param>
/// <param name="View">The range map after the attempt.</param>
internal readonly record struct MoveResult(MoveStatus Status, RangeMapSnapshot View);

/// <summary>Whether a move was committed.</summary>
internal enum MoveStatus
{
    /// <summary>The move was committed.</summary>
    Committed,

    /// <summary>The destination already owns every requested bucket.</summary>
    AlreadyOwned,

    /// <summary>A requested bucket's previous move has not reached <see cref="MigrationPhase.Opened"/>.</summary>
    PreviousMoveNotOpened,

    /// <summary>The destination holds a copy of a requested bucket which it has not reclaimed.</summary>
    DestinationHoldsCopy,

    /// <summary>
    /// A requested bucket's migration chain is at <see cref="GarnetJournalMigrator.MaxChainLength"/>; it shortens as its
    /// migrations complete.
    /// </summary>
    ChainTooLong,
}
