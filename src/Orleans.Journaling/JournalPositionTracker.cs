using System.Diagnostics;

namespace Orleans.Journaling;

/// <summary>
/// Owns a manager's journal positions: it counts them, holds the journal's compaction barriers, defers the snapshots which
/// would pass a barrier, and resolves positional reads by replaying the journal's history.
/// </summary>
/// <remarks>
/// <para>
/// A logical position is the number of journal entries in storage's current content, in storage order, followed by the
/// entries which the manager committed since. Aborted and open entries never count. Positions restart at each snapshot, at
/// its entry count: no outstanding position crosses a snapshot, because a barrier defers the snapshot or resolves its reads
/// first, in the same synchronous capture as the snapshot.
/// </para>
/// <para>
/// The manager calls the tracker on the activation's scheduler, as every access to its states runs, and under its lock
/// where a member says so.
/// </para>
/// </remarks>
internal sealed class JournalPositionTracker
{
    /// <summary>The number of consecutive snapshots which barriers defer, after which the next one resolves their reads.</summary>
    internal const int MaxDeferredSnapshots = 16;

    private readonly JournaledStateManager _manager;
    private readonly JournalBufferWriter _writer;
    private readonly IDurableDictionaryCommandCodec<string, uint> _directoryCodec;

    // The manager's replicable states and position holders. Registration closes when the manager initializes, before any
    // position is offered or resolved, so these are derived from its states once.
    private Dictionary<int, ReplicableStateEntry?>? _replicableStates;
    private IJournalPositionHolder[]? _holders;

    // The position of the writer's first committed entry, so a position is _offset plus the writer's committed entry count;
    // and the number of the writer's committed entries which storage holds.
    private long _offset;
    private long _storedEntryCount;

    // The number of entries which recovery replayed.
    private long _replayedEntryCount;

    // The number of consecutive snapshots which barriers deferred.
    private int _deferredSnapshots;

    // Whether a deletion of the journal is replacing its history, so positions aren't offered.
    private bool _suspended;

    public JournalPositionTracker(JournaledStateManager manager, JournalBufferWriter writer, IDurableDictionaryCommandCodec<string, uint> directoryCodec)
    {
        _manager = manager;
        _writer = writer;
        _directoryCodec = directoryCodec;
        Barriers = new(() => JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(manager.ServiceProvider, manager.WriteJournalFormatKey));
    }

    /// <summary>Gets the state which journals the barriers, on its reserved stream.</summary>
    public JournalBarrierState Barriers { get; }

    /// <summary>Gets whether any owner holds a compaction barrier.</summary>
    public bool HasBarriers => Barriers.HasBarriers;

    // The position at which storage's content ends.
    private long StoredPosition => _offset + _storedEntryCount;

    /// <summary>
    /// Gets the logical position of the journal's next entry, which a positional read records, when the manager offers
    /// positions: it's ready, and no format migration is pending and no deletion is in progress.
    /// </summary>
    /// <remarks>A read observes the entries before the position.</remarks>
    public bool TryGetPosition(out long position)
    {
        if (_manager.AcceptsPositions && !_suspended)
        {
            position = _offset + _writer.CommittedEntryCount;
            return true;
        }

        position = 0;
        return false;
    }

    /// <summary>
    /// Holds a compaction barrier for <paramref name="owner"/> from <paramref name="position"/>, unless it holds one: no
    /// snapshot replaces the history after it until the manager releases it.
    /// </summary>
    /// <remarks>
    /// The barrier is journaled with the next write. The manager releases it when it resolves the holders' reads, or, before
    /// a snapshot or at recovery, when no holder holds positional reads.
    /// </remarks>
    public void HoldBarrier(string owner, long position) => Barriers.Set(owner, position);

    /// <summary>Notes that recovery starts replaying storage's content, with no barriers.</summary>
    public void OnRecoveryStarting()
    {
        _replayedEntryCount = 0;
        Barriers.ResetVolatileState();
    }

    /// <summary>Counts an entry which recovery replays.</summary>
    public void OnEntryReplayed() => _replayedEntryCount++;

    /// <summary>Notes that recovery replayed storage's content: positions continue from its entries.</summary>
    public void OnRecovered()
    {
        Debug.Assert(_writer.CommittedEntryCount == 0, "Recovery starts with an empty journal writer.");
        _offset = _replayedEntryCount;
        _storedEntryCount = 0;
        _deferredSnapshots = 0;
    }

    /// <summary>
    /// Notes, under the manager's lock, that a snapshot of <paramref name="snapshotEntryCount"/> entries replaces storage's
    /// content: positions continue from its entries.
    /// </summary>
    public void OnSnapshotCaptured(long snapshotEntryCount) => _offset = snapshotEntryCount - _writer.CommittedEntryCount;

    /// <summary>Notes, under the manager's lock, that storage holds the writer's first <paramref name="committedEntryCount"/> entries.</summary>
    public void OnStored(long committedEntryCount) => _storedEntryCount = committedEntryCount;

    /// <summary>
    /// Notes, under the manager's lock, that a deletion starts replacing the journal's history: reads record values until it
    /// completes. Returns the resolution of the reads which are already recorded, or <see langword="null"/>.
    /// </summary>
    public Resolution? OnDeletionStarting()
    {
        _suspended = true;
        return BeginResolution();
    }

    /// <summary>Notes, under the manager's lock, that storage is empty: entries which the writer holds start its new content.</summary>
    public void OnDeleted()
    {
        Barriers.ResetVolatileState();
        _offset = -_storedEntryCount;
        _deferredSnapshots = 0;
        _suspended = false;
    }

    /// <summary>
    /// Decides, under the manager's lock, whether a write which would snapshot does: a held barrier defers it, up to
    /// <see cref="MaxDeferredSnapshots"/> times, and then it resolves the barriers' reads first. A migration doesn't wait.
    /// </summary>
    /// <returns>
    /// Whether the write snapshots, and the resolution of the reads which it collected before the snapshot, whose history
    /// <see cref="ReadHistoryAsync"/> reads next.
    /// </returns>
    public (bool IsSnapshot, Resolution? Resolution) PlanSnapshot(bool isMigration)
    {
        if (HasBarriers && !HoldsPositionalReads())
        {
            // Holders keep their barriers while idle, so executions which come and go journal none.
            Barriers.RemoveAll();
        }

        if (!HasBarriers)
        {
            return (true, null);
        }

        if (!isMigration && _deferredSnapshots < MaxDeferredSnapshots)
        {
            _deferredSnapshots++;
            return (false, null);
        }

        return (true, Collect() ?? CreateResolution());
    }

    /// <summary>
    /// Decides, under the manager's lock, in the synchronous capture of a planned snapshot, whether the snapshot passes the
    /// barriers: it resolves the reads of <paramref name="resolution"/>, and those which grain turns made since, and
    /// releases the barriers. Otherwise the write appends.
    /// </summary>
    public bool SnapshotPassesBarriers(Resolution? resolution, bool isMigration)
    {
        if (HasBarriers && resolution is not { HasReadStorage: true } && !isMigration)
        {
            // Reads which took positions while the states prepared need the history which the snapshot would replace:
            // append now, and let a later snapshot resolve them.
            return false;
        }

        if (resolution is not null)
        {
            // Resolves in the same synchronous capture as the snapshot, so no read can take a position which the snapshot
            // replaces.
            Resolve(resolution);
        }

        _deferredSnapshots = 0;
        return true;
    }

    /// <summary>
    /// Begins, under the manager's lock, resolving every read which the barriers hold history for: returns their resolution,
    /// whose history <see cref="ReadHistoryAsync"/> reads next, or <see langword="null"/>, after releasing the barriers when
    /// no read needs them.
    /// </summary>
    public Resolution? BeginResolution()
    {
        if (!HasBarriers)
        {
            return null;
        }

        if (Collect() is { } resolution)
        {
            return resolution;
        }

        Resolve(CreateResolution());
        return null;
    }

    /// <summary>
    /// Replays storage's history into the replicas of <paramref name="resolution"/>, resolving every read which storage's
    /// content reaches. The history must end where this manager last wrote: otherwise another writer changed the journal.
    /// </summary>
    public async ValueTask ReadHistoryAsync(Resolution resolution, CancellationToken cancellationToken)
    {
        var history = resolution.History;
        await _manager.ReadStorageAsync(history, cancellationToken).ConfigureAwait(true);
        resolution.HasReadStorage = true;
        history.ResolveCurrent();
        if (history.Position != resolution.StoredPosition)
        {
            throw new InvalidOperationException(
                $"The journal's history ends at position {history.Position}, and this activation last wrote it at position {resolution.StoredPosition}: another writer changed the journal.");
        }
    }

    /// <summary>
    /// Completes <paramref name="resolution"/>, under the manager's lock: applies its resolved reads, then resolves the reads
    /// which grain turns made after the history was read, which follow storage's content, from the entries which the
    /// manager committed but hasn't written; then releases the barriers.
    /// </summary>
    public void Resolve(Resolution resolution)
    {
        var history = resolution.History;
        var storedPosition = StoredPosition;
        List<(IJournalPositionHolder Holder, JournalPositionalReads Reads)>? later = null;
        foreach (var holder in Holders)
        {
            foreach (var (batchHolder, batch) in resolution.Batches)
            {
                if (ReferenceEquals(batchHolder, holder))
                {
                    holder.ApplyResolvedReads(batch);
                }
            }

            if (holder.CollectPositionalReads(storedPosition) is { } reads)
            {
                (later ??= []).Add((holder, reads));
                history.Add(reads);
            }
        }

        if (later is not null && _writer.CommittedEntryCount > _storedEntryCount)
        {
            using var committed = _writer.GetCommittedBuffer();
            history.ReplayBuffered(committed);
        }

        history.ResolveCurrent();
        foreach (var holder in Holders)
        {
            foreach (var (batchHolder, batch) in later ?? [])
            {
                if (ReferenceEquals(batchHolder, holder))
                {
                    holder.ApplyResolvedReads(batch);
                }
            }

            holder.OnPositionsResolved();
        }

        // Every holder's reads are resolved, or can't be: no history needs to stay, including for owners which no longer exist.
        Barriers.RemoveAll();
    }

    private IJournalPositionHolder[] Holders => _holders ??= [.. _manager.States.Values.OfType<IJournalPositionHolder>()];

    private Dictionary<int, ReplicableStateEntry?> ReplicableStates => _replicableStates ??= CreateReplicableStates();

    private Dictionary<int, ReplicableStateEntry?> CreateReplicableStates()
    {
        var states = new Dictionary<int, ReplicableStateEntry?>();
        foreach (var (name, state) in _manager.States)
        {
            if (state is not IReplicableState replicable)
            {
                continue;
            }

            var id = JournaledStateAccessTarget.ComputeId(name);
            if (!states.TryAdd(id, new(name, replicable)))
            {
                // Two names with one id can't be told apart in records of positions.
                states[id] = null;
            }
        }

        return states;
    }

    private bool HoldsPositionalReads()
    {
        foreach (var holder in Holders)
        {
            if (holder.HoldsPositionalReads)
            {
                return true;
            }
        }

        return false;
    }

    // Collects every holder's positional reads, or returns null when there are none.
    private Resolution? Collect()
    {
        Resolution? resolution = null;
        foreach (var holder in Holders)
        {
            if (holder.CollectPositionalReads(long.MinValue) is { } reads)
            {
                resolution ??= CreateResolution();
                resolution.Batches.Add((holder, reads));
                resolution.History.Add(reads);
            }
        }

        return resolution;
    }

    private Resolution CreateResolution() => new(new(_manager, ReplicableStates, _directoryCodec), StoredPosition);

    /// <summary>A resolution of positional reads, from their collection to the capture which applies their results.</summary>
    internal sealed class Resolution(JournalHistoryReplay history, long storedPosition)
    {
        /// <summary>Gets the replay of the journal's history which resolves the reads.</summary>
        public JournalHistoryReplay History { get; } = history;

        /// <summary>Gets the position at which storage's content ended when the reads were collected.</summary>
        public long StoredPosition { get; } = storedPosition;

        /// <summary>Gets the reads which each holder supplied before the history was read.</summary>
        public List<(IJournalPositionHolder Holder, JournalPositionalReads Reads)> Batches { get; } = [];

        /// <summary>Gets or sets whether storage's history was replayed, so reads of positions after it can resolve.</summary>
        public bool HasReadStorage { get; set; }
    }
}
