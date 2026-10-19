using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Diagnostics;
using Orleans.Serialization.Buffers;
using Orleans.Runtime.Internal;

namespace Orleans.Journaling;

internal partial class JournaledStateManager : IJournaledStateManager, IJournaledStateContextHost, IJournalStorageConsumer, IJournalReplayTarget, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const uint MinApplicationJournalStreamId = 8u;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IStateMachine> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, IStateMachine> _statesMap = [];

    // Whether a registered state journals changes which StagedChangeCount can't observe.
    private bool _hasUnreportedChanges;
    private readonly JournaledStateManagerShared _shared;
    private readonly IJournalStorage _storage;
    private readonly IGrainContext? _grainContext;
    private readonly JournalBufferWriter _journalWriter;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateDirectory _journalStreamDirectory;
    private readonly RetiredStateTracker _retirementTracker;
    private readonly JournalPositionState _positions;
    private readonly IDurableDictionaryCommandCodec<string, uint> _directoryCodec;

    // The states whose accesses run through a context, by their names' stable ids; null marks ids which two names share.
    private readonly Dictionary<int, JournaledStateAccessTarget?> _accessTargets = [];

    // The logical position of the next committed entry is _positionOffset plus the journal writer's committed entry count.
    // Storage holds the entries up to _storedEntryCount of that count.
    private long _positionOffset;
    private long _storedEntryCount;
    private long _replayedEntryCount;

    // The number of consecutive snapshots which barriers deferred.
    private int _deferredSnapshots;

    // Whether a deletion of the journal is replacing its history, so positions aren't offered.
    private bool _positionsSuspended;

    /// <summary>The number of consecutive snapshots which barriers defer, after which the next one resolves their reads.</summary>
    internal const int MaxDeferredSnapshots = 16;

    /// <summary>
    /// Gets or sets the provider of the context in which code accesses this manager's states, which a host installs, or
    /// <see langword="null"/> when every access runs against the state directly.
    /// </summary>
    public JournaledStateContextProvider? ContextProvider { get; set; }

    /// <summary>Gets the depth of the host's work which <see cref="EnterHostAccess"/> marked.</summary>
    internal int HostAccessDepth { get; private set; }

    /// <inheritdoc/>
    public void EnterHostAccess() => HostAccessDepth++;

    /// <inheritdoc/>
    public void ExitHostAccess()
    {
        Debug.Assert(HostAccessDepth > 0, "The host exits only the work which it entered.");
        HostAccessDepth--;
    }

    // The names of the states whose accesses run through a context, by identifier, which records of accesses store.
    private Dictionary<int, string>? _accessTargetNames;

    /// <summary>
    /// Registers a state whose accesses run through this manager's context, which identifies the state by
    /// <see cref="JournaledStateAccessTarget.Id"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another state of this manager has the same identifier.</exception>
    internal void RegisterAccessTarget(JournaledStateAccessTarget target)
    {
        _accessTargetNames ??= [];
        if (_accessTargetNames.TryAdd(target.Id, target.Name)
            || string.Equals(_accessTargetNames[target.Id], target.Name, StringComparison.Ordinal))
        {
            if (target.History is not null || !_accessTargets.ContainsKey(target.Id))
            {
                _accessTargets[target.Id] = target;
            }

            return;
        }

        throw new InvalidOperationException(
            $"The journaled states '{_accessTargetNames[target.Id]}' and '{target.Name}' have the same identifier, #{target.Id:x8}, "
            + "which identifies a state in the records of durable task bodies' accesses. Rename one of the states.");
    }
    private Task? _workLoop;
    private ManagerState _state;
    private long _deletionGeneration;
    private Exception? _failure;
    private bool _migrationSnapshotRequired;
    private int _disposed;

    public JournaledStateManager(JournaledStateManagerShared shared, IJournalStorageProvider storageProvider, IGrainContext grainContext)
        : this(shared, CreateStorage(storageProvider, CreateJournalId(grainContext)), grainContext)
    {
        try
        {
            ((ILifecycleParticipant<IGrainLifecycle>)this).Participate(grainContext.ObservableLifecycle);
        }
        catch
        {
            ((IDisposable)this).Dispose();
            throw;
        }
    }

    public JournaledStateManager(JournaledStateManagerShared shared, IJournalStorageProvider storageProvider, JournalId journalId)
        : this(shared, CreateStorage(storageProvider, journalId))
    {
    }

    internal JournaledStateManager(
        JournaledStateManagerShared shared,
        IJournalStorage storage,
        IGrainContext? grainContext = null)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(storage);
        _shared = shared;
        _storage = storage;
        _grainContext = grainContext;
        var serviceProvider = ServiceProvider;
        var journalStreamIdsCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, uint>>(serviceProvider, WriteJournalFormatKey);
        var retirementTrackerCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, DateTime>>(serviceProvider, WriteJournalFormatKey);
        _journalWriter = _shared.JournalFormat.CreateWriter();
        _directoryCodec = journalStreamIdsCodec;

        // The list of known states is itself stored as a durable state with the implicit id 0.
        // This allows us to recover the list of states ids without having to store it separately.
        _journalStreamDirectory = new StateDirectory(this, journalStreamIdsCodec);
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;

        // The retirement tracker is a special internal state with a fixed id.
        // It is not stored in _journalStreamDirectory and does not participate in the general name->id mapping.
        _retirementTracker = new RetiredStateTracker(this, retirementTrackerCodec);
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;

        // The journal's compaction barriers are another internal state with a fixed id.
        _positions = new JournalPositionState(
            () => JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, string>>(ServiceProvider, WriteJournalFormatKey));
        _statesMap[JournalPositionState.Id] = _positions;
    }

    private static JournalId CreateJournalId(IGrainContext grainContext)
    {
        ArgumentNullException.ThrowIfNull(grainContext);
        return JournalId.FromGrainId(grainContext.GrainId);
    }

    internal static IJournalStorage CreateStorage(IJournalStorageProvider storageProvider, JournalId journalId)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);
        if (journalId.IsDefault)
        {
            throw new ArgumentException("The journal id must not be the default value.", nameof(journalId));
        }

        return storageProvider.CreateStorage(journalId) ?? throw new InvalidOperationException("The configured journal storage provider returned null.");
    }

    internal string WriteJournalFormatKey => _shared.JournalFormatKey;

    internal IServiceProvider ServiceProvider => _grainContext is { } context ? context.ActivationServices : _shared.ServiceProvider;

    public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IStateMachine? stateMachine)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        lock (_lock)
        {
            ThrowIfFenced();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_states.TryGetValue(name, out var existing) && existing is not RetiredStateMachine)
            {
                stateMachine = existing;
                return true;
            }
        }

        stateMachine = null;
        return false;
    }

    protected void EnsureRegistrationAllowed()
    {
        lock (_lock)
        {
            ThrowIfFenced();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            if (_workLoop is not null)
            {
                throw new InvalidOperationException("New states cannot be registered after journaled state manager initialization has begun.");
            }
        }
    }

    public void RegisterStateMachine(string name, IStateMachine stateMachine)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(stateMachine);

        lock (_lock)
        {
            EnsureRegistrationAllowed();
            if (_states.TryGetValue(name, out var existing))
            {
                throw new InvalidOperationException(
                    $"A state named '{name}' is already registered with type '{existing.GetType()}' and cannot be registered as '{stateMachine.GetType()}'.");
            }

            _states.Add(name, stateMachine);
            if (stateMachine is not IReportsStagedChanges) _hasUnreportedChanges = true;
            _workQueue.Enqueue(new RegisterStateWorkItem(name));
        }

        _workSignal.Signal();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        bool didEnqueue;
        lock (_lock)
        {
            _shutdownCancellation.Token.ThrowIfCancellationRequested();
            ThrowIfFenced();
            if (_workLoop is null)
            {
                _workLoop = Start();
            }

            task = EnqueueOrGetPendingWorkItem<InitializeWorkItem>(out didEnqueue);
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        await task.WaitAsync(cancellationToken);
    }

    private Task Start()
    {
        using var suppressExecutionContext = new ExecutionContextSuppressor();
        return WorkLoop();
    }

    private async Task WorkLoop()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        while (!_shutdownCancellation.IsCancellationRequested)
        {
            try
            {
                await RecoverAsync(_shutdownCancellation.Token).ConfigureAwait(true);
                _workSignal.Signal();
                break;
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                try
                {
                    LogErrorProcessingWorkItems(_shared.Logger, exception);
                }
                finally
                {
                    lock (_lock)
                    {
                        FaultQueuedWorkItemsUnderLock(exception);
                    }
                }
            }

            // Signals can remain from the failed attempt. Retry only for newly queued initialization work.
            while (true)
            {
                await _workSignal.WaitAsync().ConfigureAwait(true);
                if (_shutdownCancellation.IsCancellationRequested)
                {
                    return;
                }

                lock (_lock)
                {
                    if (_workQueue.Count > 0)
                    {
                        break;
                    }
                }
            }
        }

        while (!_shutdownCancellation.Token.IsCancellationRequested)
        {
            try
            {
                await _workSignal.WaitAsync().ConfigureAwait(true);
                _shutdownCancellation.Token.ThrowIfCancellationRequested();

                while (true)
                {
                    WorkItem workItem;
                    lock (_lock)
                    {
                        if (!_workQueue.TryDequeue(out var dequeuedWorkItem))
                        {
                            // Wait for the queue to be signaled again.
                            break;
                        }

                        workItem = dequeuedWorkItem;
                    }

                    var processingTimestamp = _shared.TimeProvider.GetTimestamp();
                    var queueOperation = GetStorageQueueOperation(workItem);
                    var queueDuration = queueOperation is null
                        ? default
                        : _shared.TimeProvider.GetElapsedTime(workItem.EnqueuedTimestamp, processingTimestamp);
                    var recordQueueDuration = workItem is DeleteStateWorkItem;
                    Activity? storageActivity = queueOperation is null
                        ? null
                        : ActivitySources.StorageGrainSource.StartActivity(
                            $"journal {queueOperation}",
                            ActivityKind.Internal,
                            workItem.TraceParent,
                            tags: null,
                            links: workItem.TraceLinks);
                    if (storageActivity is not null)
                    {
                        storageActivity.SetTag(ActivityTagKeys.JournalStorageOperation, queueOperation);
                    }

                    try
                    {
                        if (workItem is AppendJournalWorkItem or WriteSnapshotWorkItem
                            && workItem.DeletionGeneration != Volatile.Read(ref _deletionGeneration))
                        {
                            throw new InvalidOperationException(
                                "The journaled state operation was queued before deletion reset its state.");
                        }

                        var isSnapshot = false;
                        JournalHistoryReplay? positionHistory = null;
                        if (workItem is AppendJournalWorkItem or WriteSnapshotWorkItem)
                        {
                            // TODO: decide whether it's best to snapshot or append. Eg, by summing the size of the most recent snapshots and the current journal length.
                            //       If the current journal length is greater than the snapshot size, then take a snapshot instead of appending more journal entries.
                            isSnapshot = workItem is WriteSnapshotWorkItem
                                || _migrationSnapshotRequired
                                || _storage.IsCompactionRequested;
                            if (isSnapshot && _positions.HasBarriers && !HoldsPositionalReads())
                            {
                                // Holders keep their barriers while idle, so executions which come and go journal none.
                                _positions.RemoveAll();
                            }

                            if (isSnapshot && _positions.HasBarriers)
                            {
                                // A snapshot replaces the history which positional reads refer to. Barriers defer it, and
                                // eventually it resolves their reads first. A migration can't wait.
                                if (!_migrationSnapshotRequired && _deferredSnapshots < MaxDeferredSnapshots)
                                {
                                    _deferredSnapshots++;
                                    isSnapshot = false;
                                }
                                else
                                {
                                    positionHistory = await ReadPositionHistoryAsync(_shutdownCancellation.Token).ConfigureAwait(true);
                                }
                            }

                            try
                            {
                                for (var prepared = false; !prepared;)
                                {
                                    await PrepareStates(ref prepared, _shutdownCancellation.Token).ConfigureAwait(true);
                                }

                                _shutdownCancellation.Token.ThrowIfCancellationRequested();
                            }
                            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
                            {
                                workItem.TrySetCanceled(_shutdownCancellation.Token);
                                return;
                            }
                            catch (Exception exception)
                            {
                                LogStatePreparationFailed(_shared.Logger, exception);
                                storageActivity?.SetStatus(ActivityStatusCode.Error, "Journal state preparation failed.");
                                workItem.SetException(new JournaledStatePreCommitException(exception));
                                continue;
                            }
                        }

                        // Note that the implementation of each command is inlined to avoid allocating unnecessary async states.
                        // We are ok sacrificing some code organization for performance in the inner loop.
                        switch (workItem)
                        {
                            case AppendJournalWorkItem:
                            case WriteSnapshotWorkItem:
                                {
                                    if (isSnapshot && _positions.HasBarriers && positionHistory is not { HasReadStorage: true } && !_migrationSnapshotRequired)
                                    {
                                        // Reads which took positions while the states prepared need the history which the
                                        // snapshot would replace: append now, and let a later snapshot resolve them.
                                        isSnapshot = false;
                                        positionHistory = null;
                                    }

                                    var operationLabel = isSnapshot
                                        ? JournalingInstruments.OperationSnapshot
                                        : JournalingInstruments.OperationAppend;
                                    if (isSnapshot)
                                    {
                                        var compactionReason = workItem is WriteSnapshotWorkItem
                                            ? JournalingInstruments.CompactionReasonUserSnapshot
                                            : _migrationSnapshotRequired
                                                ? JournalingInstruments.CompactionReasonMigration
                                                : JournalingInstruments.CompactionReasonStorageRequested;
                                        _shared.Instruments.OnCompactionTriggered(compactionReason);
                                    }
                                    _shared.Instruments.OnWriteCoalesced(operationLabel, workItem.CallerCount);
                                    var gatherStartTimestamp = _shared.TimeProvider.GetTimestamp();
                                    var statesScanned = 0L;
                                    ArcBuffer committedBuffer = default;
                                    ArcBuffer bufferToConsume = default;
                                    var hasCommittedBuffer = false;
                                    var hasBufferToConsume = false;
                                    var bufferToConsumeIsCommittedBuffer = false;
                                    var writeVersion = 0L;
                                    var capturedEntryCount = 0L;

                                    // Each write captures every state, including a change which another thread is writing into an entry
                                    // which it opened before the write. The write waits for that entry to close, whether it appends or
                                    // replaces the journal, so that it captures the change.
                                    while (true)
                                    {
                                        Task? activeEntryClosed;
                                        lock (_lock)
                                        {
                                            // Capture journals changes which states already staged, so it doesn't count as staging.
                                            var stagedChangeCount = _journalWriter.StagedChangeCount;
                                            activeEntryClosed = _journalWriter.GetActiveEntryClosedTask();
                                            if (activeEntryClosed is null && isSnapshot)
                                            {
                                                if (positionHistory is not null)
                                                {
                                                    // Resolves in the same synchronous capture as the snapshot, so no read can
                                                    // take a position which the snapshot replaces.
                                                    ResolvePositions(positionHistory);
                                                }

                                                _deferredSnapshots = 0;
                                                using var snapshotWriter = _shared.JournalFormat.CreateWriter();
                                                if (_retirementTracker.Count > 0)
                                                {
                                                    RetireOrResurectStates();
                                                }

                                                if (_migrationSnapshotRequired)
                                                {
                                                    ThrowIfMigrationBlockedByRetiredStates();
                                                }

                                                // The map of state ids is itself stored as a durable state with the id 0.
                                                // This must be stored first, since it includes the identities of all other states, which are needed when replaying the journal.
                                                // If we removed retired states, this snapshot will persist that change.
                                                AppendUpdatesOrSnapshotState(snapshotWriter, isSnapshot: true, StateDirectory.Id, _journalStreamDirectory);
                                                statesScanned++;

                                                foreach (var (id, state) in _statesMap)
                                                {
                                                    if (id is 0 || state is null)
                                                    {
                                                        continue;
                                                    }

                                                    AppendUpdatesOrSnapshotState(snapshotWriter, isSnapshot: true, id, state);
                                                    statesScanned++;
                                                }

                                                // Positions continue from the snapshot's entries, which replace storage's content.
                                                capturedEntryCount = _journalWriter.CommittedEntryCount;
                                                _positionOffset = snapshotWriter.CommittedEntryCount - capturedEntryCount;

                                                bufferToConsume = _journalWriter.GetCommittedBuffer();
                                                if (bufferToConsume.Length > 0)
                                                {
                                                    hasBufferToConsume = true;
                                                }
                                                else
                                                {
                                                    bufferToConsume.Dispose();
                                                }

                                                committedBuffer = snapshotWriter.GetCommittedBuffer();
                                            }
                                            else if (activeEntryClosed is null)
                                            {
                                                // The map of state ids is itself stored as a durable state with the id 0.
                                                // This must be stored first, since it includes the identities of all other states, which are needed when replaying the journal.
                                                AppendUpdatesOrSnapshotState(_journalWriter, isSnapshot: false, StateDirectory.Id, _journalStreamDirectory);
                                                statesScanned++;

                                                foreach (var (id, state) in _statesMap)
                                                {
                                                    if (id is 0 || state is null)
                                                    {
                                                        continue;
                                                    }

                                                    AppendUpdatesOrSnapshotState(_journalWriter, isSnapshot: false, id, state);
                                                    statesScanned++;
                                                }

                                                committedBuffer = _journalWriter.GetCommittedBuffer();
                                                capturedEntryCount = _journalWriter.CommittedEntryCount;
                                                bufferToConsume = committedBuffer;
                                                bufferToConsumeIsCommittedBuffer = true;
                                            }

                                            if (activeEntryClosed is null)
                                            {
                                                if (committedBuffer.Length == 0)
                                                {
                                                    // Nothing was staged, so the write captured nothing which awaits acknowledgement.
                                                    committedBuffer.Dispose();
                                                    if (bufferToConsumeIsCommittedBuffer)
                                                    {
                                                        bufferToConsume = default;
                                                    }
                                                }
                                                else
                                                {
                                                    hasCommittedBuffer = true;
                                                    if (!isSnapshot)
                                                    {
                                                        hasBufferToConsume = true;
                                                    }

                                                    writeVersion = OnWriteCaptured();
                                                }
                                            }

                                            _journalWriter.StagedChangeCount = stagedChangeCount;
                                        }

                                        if (activeEntryClosed is null)
                                        {
                                            break;
                                        }

                                        await activeEntryClosed.WaitAsync(_shutdownCancellation.Token).ConfigureAwait(true);
                                    }

                                    _shared.Instruments.OnGather(
                                        operationLabel,
                                        _shared.TimeProvider.GetElapsedTime(gatherStartTimestamp),
                                        statesScanned);

                                    if (!hasCommittedBuffer && hasBufferToConsume && !bufferToConsumeIsCommittedBuffer)
                                    {
                                        bufferToConsume.Dispose();
                                    }

                                    if (hasCommittedBuffer)
                                    {
                                        recordQueueDuration = true;
                                        var writeSequence = committedBuffer.AsReadOnlySequence();
#if DEBUG
                                        // Defensive: copy the sequence into a pooled buffer so we can poison it
                                        // after the storage call returns. Any IJournalStorage implementation that
                                        // retains the sequence past task completion (a violation of the documented
                                        // contract on AppendAsync/ReplaceAsync) will read 0x67 bytes when it next
                                        // touches the buffer, surfacing the bug loudly in tests instead of letting
                                        // recycled pool data hide it.
                                        var debugPoisonLength = checked((int)writeSequence.Length);
                                        var debugPoisonBuffer = ArrayPool<byte>.Shared.Rent(debugPoisonLength);
                                        writeSequence.CopyTo(debugPoisonBuffer);
                                        writeSequence = new ReadOnlySequence<byte>(debugPoisonBuffer, 0, debugPoisonLength);
#endif

                                        try
                                        {
                                            if (isSnapshot)
                                            {
                                                await ReplaceStorageAsync(writeSequence, _shutdownCancellation.Token).ConfigureAwait(true);
                                            }
                                            else
                                            {
                                                await AppendStorageAsync(writeSequence, _shutdownCancellation.Token).ConfigureAwait(true);
                                            }

                                            lock (_lock)
                                            {
                                                if (hasBufferToConsume)
                                                {
                                                    _journalWriter.Consume(bufferToConsume);
                                                }

                                                _storedEntryCount = capturedEntryCount;
                                            }
                                        }
                                        finally
                                        {
                                            committedBuffer.Dispose();
                                            if (hasBufferToConsume && !bufferToConsumeIsCommittedBuffer)
                                            {
                                                bufferToConsume.Dispose();
                                            }
#if DEBUG
                                            debugPoisonBuffer.AsSpan(0, debugPoisonLength).Fill(0x67);
                                            ArrayPool<byte>.Shared.Return(debugPoisonBuffer);
#endif
                                        }
                                    }

                                    if (writeVersion != 0)
                                    {
                                        // Acknowledge only after every state's pending entries were captured.
                                        lock (_lock)
                                        {
                                            foreach (var state in _states.Values)
                                            {
                                                state.OnWriteCompleted();
                                            }

                                            OnWriteAcknowledged(writeVersion);
                                            if (isSnapshot)
                                            {
                                                _migrationSnapshotRequired = false;
                                            }
                                        }
                                    }

                                    break;
                                }

                            case DeleteStateWorkItem:
                                {
                                    // Deletion replaces the history which positional reads refer to: reads record values
                                    // until it completes, and those already recorded resolve first.
                                    _positionsSuspended = true;
                                    if (_positions.HasBarriers && HoldsPositionalReads())
                                    {
                                        var history = await ReadPositionHistoryAsync(_shutdownCancellation.Token).ConfigureAwait(true);
                                        lock (_lock)
                                        {
                                            ResolvePositions(history);
                                        }
                                    }

                                    // Clear storage.
                                    await DeleteStorageAsync(_shutdownCancellation.Token).ConfigureAwait(true);

                                    lock (_lock)
                                    {
                                        // Discard entries for the state being reset before writing the new directory.
                                        _journalWriter.Reset();

                                        // Reset the state id collection.
                                        _journalStreamDirectory.ResetVolatileState();

                                        // Allocate new state ids for each state.
                                        // Doing so will trigger a reset, since _journalStreamDirectory will bind the state in question.
                                        foreach (var (name, state) in _states)
                                        {
                                            var id = _journalStreamDirectory.GetNextJournalStreamId();
                                            _journalStreamDirectory.Set(name, id);
                                        }

                                        _deletionGeneration++;

                                        // Storage is empty: entries which the writer holds start its new content.
                                        _positions.ResetVolatileState();
                                        _positionOffset = -_storedEntryCount;
                                        _deferredSnapshots = 0;
                                        _positionsSuspended = false;
                                    }
                                    break;
                                }

                            case InitializeWorkItem:
                                {
                                    lock (_lock)
                                    {
                                        ThrowIfFenced();
                                        _state = ManagerState.Ready;
                                    }
                                    break;
                                }

                            case RegisterStateWorkItem registerState:
                                {
                                    lock (_lock)
                                    {
                                        if (_state is not ManagerState.Unknown)
                                        {
                                            throw new NotSupportedException("Registering a state after activation is not supported.");
                                        }

                                        var name = registerState.Name;
                                        if (!_journalStreamDirectory.ContainsKey(name))
                                        {
                                            // Doing so will trigger a reset, since _journalStreamDirectory will bind the state in question.
                                            _journalStreamDirectory.Set(name, _journalStreamDirectory.GetNextJournalStreamId());
                                        }
                                    }
                                    break;
                                }

                            default:
                                {
                                    Debug.Fail($"The command {workItem.GetType().FullName} is unsupported");
                                    break;
                                }
                        }

                        if (recordQueueDuration && queueOperation is not null)
                        {
                            _shared.Instruments.OnStorageOperationQueued(queueOperation, queueDuration, succeeded: true);
                        }

                        storageActivity?.SetStatus(ActivityStatusCode.Ok);
                        workItem.SetResult();
                    }
                    catch (Exception exception)
                    {
                        if (recordQueueDuration && queueOperation is not null)
                        {
                            _shared.Instruments.OnStorageOperationQueued(queueOperation, queueDuration, succeeded: false);
                        }

                        if (storageActivity is not null)
                        {
                            storageActivity.SetStatus(ActivityStatusCode.Error, exception.Message);
                            if (storageActivity.IsAllDataRequested)
                            {
                                storageActivity.SetTag(ActivityTagKeys.ExceptionType, exception.GetType().FullName);
                                storageActivity.SetTag(ActivityTagKeys.ExceptionMessage, exception.Message);
                                storageActivity.SetTag(ActivityTagKeys.ExceptionStacktrace, exception.ToString());
                                storageActivity.SetTag(ActivityTagKeys.ExceptionEscaped, true);
                            }
                        }

                        try
                        {
                            Fence(exception);
                        }
                        finally
                        {
                            workItem.SetException(exception);
                        }

                        return;
                    }
                    finally
                    {
                        storageActivity?.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                Fence(exception);
                return;
            }
        }
    }

    // Completes when every state is synchronously ready; otherwise returns the first incomplete preparation.
    private ValueTask PrepareStates(ref bool prepared, CancellationToken cancellationToken)
    {
        prepared = true;
        lock (_lock)
        {
            foreach (var state in _states.Values)
            {
                var preparation = state.PrepareWriteAsync(cancellationToken);
                if (!preparation.IsCompletedSuccessfully)
                {
                    prepared = false;
                    return preparation;
                }

                preparation.GetAwaiter().GetResult();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Fence(Exception exception)
    {
        lock (_lock)
        {
            if (_state is ManagerState.Fenced)
            {
                return;
            }

            _state = ManagerState.Fenced;
            _failure = exception;
        }

        try
        {
            if (!_shutdownCancellation.IsCancellationRequested)
            {
                LogErrorProcessingWorkItems(_shared.Logger, exception);
                _grainContext?.Deactivate(new DeactivationReason(
                    DeactivationReasonCode.ApplicationError, exception, "Journaled state operation failed."));
            }
        }
        finally
        {
            // Watchers complete cleanly only when shutdown canceled the operation. Any other failure leaves staged changes uncertain.
            CompleteObservers(exception is OperationCanceledException && _shutdownCancellation.IsCancellationRequested ? null : exception);
            lock (_lock)
            {
                FaultQueuedWorkItemsUnderLock(exception);
            }
        }
    }

    private void FaultQueuedWorkItemsUnderLock(Exception exception)
    {
        while (_workQueue.TryDequeue(out var workItem))
        {
            workItem.TrySetException(exception);
        }
    }

    private void RetireOrResurectStates()
    {
        foreach (var (name, timestamp) in _retirementTracker)
        {
            var isDuetime = _shared.TimeProvider.GetUtcNow().UtcDateTime - timestamp >= _shared.RetirementGracePeriod;
            if (isDuetime && _journalStreamDirectory.TryGetValue(name, out var id))
            {
                var state = _states[name];

                Debug.Assert(state is not null);

                if (state is RetiredStateMachine)
                {
                    LogRemovingRetiredState(_shared.Logger, name);

                    // Since we are permanently removing this state, we will clean it up by reseting it.
                    state.Reset(CreateJournalStreamWriter(new(id)));

                    _statesMap.Remove(id);
                    // We remove these from memory only, since the snapshot will persist these changes.
                    _journalStreamDirectory.ApplyRemove(name);
                    _retirementTracker.ApplyRemove(name);
                }
                else
                {
                    LogRetiredStateComebackDetected(_shared.Logger, name);
                    // We remove the tracker from memory only, since the snapshot will persist the change.
                    _retirementTracker.ApplyRemove(name);
                }
            }
        }
    }

    private void ThrowIfMigrationBlockedByRetiredStates()
    {
        foreach (var state in _statesMap.Values)
        {
            if (state is RetiredStateMachine { PreservedEntries.Count: > 0 } retiredState)
            {
                throw new InvalidOperationException(
                    $"Cannot migrate journal to format key '{_shared.JournalFormatKey}' because stream " +
                    $"{retiredState.StreamId.Value} belongs to a state which is not currently registered. Register the state so it can be decoded " +
                    "and snapshotted in the configured format, or keep the previous journal format configured until the state can be retired.");
            }
        }
    }

    private JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => _journalWriter.CreateJournalStreamWriter(streamId);

    private static void AppendUpdatesOrSnapshotState(JournalBufferWriter journalWriter, bool isSnapshot, uint id, IStateMachine state)
    {
        var writer = journalWriter.CreateJournalStreamWriter(new(id));
        if (isSnapshot)
        {
            state.WriteSnapshot(writer);
        }
        else
        {
            state.WritePendingEntries(writer);
        }
    }

    public async ValueTask DeleteStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task task;
        bool didEnqueue;
        lock (_lock)
        {
            ThrowIfStateOperationsUnavailable();
            task = EnqueueOrGetPendingWorkItem<DeleteStateWorkItem>(out didEnqueue);
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await task.WaitAsync(cancellationToken);
            _shared.Instruments.OnStateDeleteRequest(_shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStateDeleteRequest(_shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: false);
            throw;
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        lock (_lock)
        {
            ResetForRecovery();
        }

        try
        {
            await ReadStorageAsync(this, cancellationToken).ConfigureAwait(true);
            _shared.Instruments.OnRecovery(_shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnRecovery(_shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: false);
            throw;
        }

        lock (_lock)
        {
            // Positions count storage's entries; the writer holds none yet.
            _positionOffset = _replayedEntryCount;
            _storedEntryCount = 0;
            _deferredSnapshots = 0;
            foreach (var (name, state) in _states)
            {
                if (state is not RetiredStateMachine && !_journalStreamDirectory.ContainsKey(name))
                {
                    var id = _journalStreamDirectory.GetNextJournalStreamId();
                    while (_statesMap.ContainsKey(id))
                    {
                        id = checked(id + 1);
                    }

                    _journalStreamDirectory.Set(name, id);
                }
            }

            foreach ((var name, var state) in _states)
            {
                state.OnRecoveryCompleted();

                if (state is RetiredStateMachine)
                {
                    // We can use TryAdd since recovery has finished.
                    if (_retirementTracker.TryAdd(name, _shared.TimeProvider.GetUtcNow().UtcDateTime))
                    {
                        LogRetiredStateDetected(_shared.Logger, name);
                    }
                }
            }
        }

        // Recovered positional reads need history which the next snapshot could replace, and their executions are about to
        // replay them: resolve them now, from a second read of the same history.
        if (_positions.HasBarriers)
        {
            var history = await ReadPositionHistoryAsync(cancellationToken).ConfigureAwait(true);
            lock (_lock)
            {
                ResolvePositions(history);
            }
        }
    }

    private void ResetForRecovery()
    {
        _journalWriter.Reset();
        _migrationSnapshotRequired = false;
        _statesMap.Clear();
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;
        _statesMap[JournalPositionState.Id] = _positions;
        _replayedEntryCount = 0;

        List<string>? retiredNames = null;
        foreach (var (name, state) in _states)
        {
            if (state is RetiredStateMachine)
            {
                (retiredNames ??= []).Add(name);
            }
        }

        if (retiredNames is not null)
        {
            foreach (var name in retiredNames)
            {
                _states.Remove(name);
            }
        }

        _journalStreamDirectory.ResetVolatileState();
        _retirementTracker.ResetVolatileState();
        _positions.ResetVolatileState();
    }

    internal void BindStateForReplay(JournalStreamId streamId, IStateMachine state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            _statesMap[streamId.Value] = state;
            state.Reset(CreateJournalStreamWriter(streamId));
        }
    }

    internal IStateMachine ResolveStateMachine(JournalStreamId streamId)
    {
        _replayedEntryCount++;
        if (!_statesMap.TryGetValue(streamId.Value, out var state))
        {
            state = new RetiredStateMachine(streamId);
            _statesMap[streamId.Value] = state;
        }

        return state;
    }

    string IJournalReplayTarget.WriteJournalFormatKey => WriteJournalFormatKey;

    IServiceProvider IJournalReplayTarget.ServiceProvider => ServiceProvider;

    IStateMachine IJournalReplayTarget.ResolveStateMachine(JournalStreamId streamId) => ResolveStateMachine(streamId);

    /// <summary>
    /// Gets the logical position of the journal's next entry, which a positional read records, when the manager offers
    /// positions: it's ready, and no format migration is pending and no deletion is in progress.
    /// </summary>
    /// <remarks>
    /// A read observes the entries before the position. Callers run on the activation's scheduler, as every access to its
    /// states does.
    /// </remarks>
    internal bool TryGetPosition(out long position)
    {
        if (_state is ManagerState.Ready && !_migrationSnapshotRequired && !_positionsSuspended)
        {
            position = _positionOffset + _journalWriter.CommittedEntryCount;
            return true;
        }

        position = 0;
        return false;
    }

    /// <summary>
    /// Holds a compaction barrier for <paramref name="owner"/> from <paramref name="position"/>, unless it holds one: no
    /// snapshot replaces the history after it until the owner releases it or the manager resolves the owner's reads.
    /// </summary>
    /// <remarks>
    /// The barrier is journaled with the next write. The manager releases it when it resolves the owner's reads, or, before
    /// a snapshot or at recovery, when no holder holds positional reads.
    /// </remarks>
    internal void SetBarrier(string owner, long position) => _positions.Set(owner, position);

    /// <summary>Gets whether any owner holds a compaction barrier.</summary>
    internal bool HasBarriers => _positions.HasBarriers;


    internal IReadOnlyDictionary<int, JournaledStateAccessTarget?> AccessTargets => _accessTargets;

    private bool HoldsPositionalReads()
    {
        foreach (var state in _states.Values)
        {
            if (state is IJournalPositionHolder { HoldsPositionalReads: true })
            {
                return true;
            }
        }

        return false;
    }

    // Collects the holders' positional reads and replays storage's history into replicas, resolving every read which
    // storage's content reaches. The history must end where this manager last wrote: otherwise another writer changed
    // the journal.
    private async ValueTask<JournalHistoryReplay> ReadPositionHistoryAsync(CancellationToken cancellationToken)
    {
        JournalHistoryReplay history;
        long storedPosition;
        lock (_lock)
        {
            history = new JournalHistoryReplay(this, _shared.JournalFormat, _directoryCodec);
            foreach (var state in _states.Values)
            {
                if (state is IJournalPositionHolder holder && holder.CollectPositionalReads(long.MinValue) is { } reads)
                {
                    history.Add(holder, reads);
                }
            }

            storedPosition = _positionOffset + _storedEntryCount;
        }

        // A barrier which no read needs any longer releases without reading history.
        if (history.Batches.Count == 0)
        {
            return history;
        }

        await ReadStorageAsync(history, cancellationToken).ConfigureAwait(true);
        history.HasReadStorage = true;
        history.ResolveCurrent();
        if (history.Position != storedPosition)
        {
            throw new InvalidOperationException(
                $"The journal's history ends at position {history.Position}, and this activation last wrote it at position {storedPosition}: another writer changed the journal.");
        }

        return history;
    }

    // Applies the resolved reads, then resolves the reads which grain turns made after the history was read, which follow
    // storage's content, from the entries which the manager committed but hasn't written; then releases the barriers.
    private void ResolvePositions(JournalHistoryReplay history)
    {
        var storedPosition = _positionOffset + _storedEntryCount;
        foreach (var state in _states.Values)
        {
            if (state is not IJournalPositionHolder holder)
            {
                continue;
            }

            if (history.Batches.TryGetValue(holder, out var resolved))
            {
                holder.ApplyResolvedReads(resolved);
            }

            history.Batches.Remove(holder);
            if (holder.CollectPositionalReads(storedPosition) is { } later)
            {
                history.Add(holder, later);
            }
        }

        if (history.Batches.Count > 0 && _journalWriter.CommittedEntryCount > _storedEntryCount)
        {
            using var committed = _journalWriter.GetCommittedBuffer();
            history.ReplayBuffered(committed);
        }

        history.ResolveCurrent();
        foreach (var state in _states.Values)
        {
            if (state is IJournalPositionHolder holder)
            {
                if (history.Batches.TryGetValue(holder, out var later))
                {
                    holder.ApplyResolvedReads(later);
                }

                holder.ReleasePositions();
            }
        }

        // Every holder's reads are resolved, or can't be: no history needs to stay, including for owners which no longer exist.
        _positions.RemoveAll();
    }

    internal IJournalFormat GetJournalFormat(string journalFormatKey)
    {
        if (string.Equals(journalFormatKey, _shared.JournalFormatKey, StringComparison.Ordinal))
        {
            return _shared.JournalFormat;
        }

        return JournalFormatServices.GetRequiredJournalFormat(ServiceProvider, journalFormatKey);
    }

    private void ProcessRecoveryBuffer(JournalBufferReader buffer, IJournalMetadata? metadata)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var journalFormatKey = metadata?.FormatKey is { } storedFormatKey
            ? JournalFormatServices.ValidateJournalFormatKey(storedFormatKey)
            : _shared.JournalFormatKey;
        try
        {
            if (!string.Equals(journalFormatKey, _shared.JournalFormatKey, StringComparison.Ordinal))
            {
                _migrationSnapshotRequired = true;
            }

            var journalFormat = GetJournalFormat(journalFormatKey);
            var replayContext = new JournalReplayContext(this);
            journalFormat.Replay(buffer, replayContext);

            if (buffer.IsCompleted && buffer.Length > 0)
            {
                throw new InvalidOperationException("The journal format did not read the completed journal data.");
            }
        }
        catch (Exception exception) when (ShouldWrapRecoveryFormatException(exception))
        {
            throw CreateRecoveryFormatException(exception, journalFormatKey);
        }
    }

    void IJournalStorageConsumer.Read(JournalBufferReader buffer, IJournalMetadata? metadata) => ProcessRecoveryBuffer(buffer, metadata);

    private static bool ShouldWrapRecoveryFormatException(Exception exception) =>
        exception is not OperationCanceledException && !IsRecoveryFormatException(exception);

    private static bool IsRecoveryFormatException(Exception exception) =>
        exception is InvalidOperationException { InnerException: not null }
        && exception.Message?.StartsWith("Failed to recover journaling state using journal format key ", StringComparison.Ordinal) == true;

    private InvalidOperationException CreateRecoveryFormatException(Exception exception, string journalFormatKey) =>
        new(
            $"Failed to recover journaling state using journal format key '{journalFormatKey}'. " +
            $"The configured write journal format key is '{_shared.JournalFormatKey}'.",
            exception);

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task pendingWrite;
        bool didEnqueue;
        string operation;
        lock (_lock)
        {
            ThrowIfStateOperationsUnavailable();
            var isSnapshot = _migrationSnapshotRequired || _storage.IsCompactionRequested;
            operation = isSnapshot ? JournalingInstruments.OperationSnapshot : JournalingInstruments.OperationAppend;
            pendingWrite = isSnapshot
                ? EnqueueOrGetPendingWorkItem<WriteSnapshotWorkItem>(out didEnqueue)
                : EnqueueOrGetPendingWorkItem<AppendJournalWorkItem>(out didEnqueue);
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await pendingWrite.WaitAsync(cancellationToken);
            _shared.Instruments.OnStateWriteRequest(operation, _shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStateWriteRequest(operation, _shared.TimeProvider.GetElapsedTime(startTimestamp), succeeded: false);
            throw;
        }
    }

    private void ThrowIfStateOperationsUnavailable()
    {
        _shutdownCancellation.Token.ThrowIfCancellationRequested();
        ThrowIfFenced();
        if (_state is not ManagerState.Ready)
        {
            throw new InvalidOperationException("The journaled state manager has not been initialized.");
        }
    }

    private void ThrowIfFenced()
    {
        if (_state is ManagerState.Fenced)
        {
            throw new InvalidOperationException(
                "Journaled state operations are fenced because an operation failed. Recover using a new grain activation or dispose this manager and create a new instance.",
                _failure);
        }
    }

    private async ValueTask ReadStorageAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
    {
        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await _storage.ReadAsync(consumer, cancellationToken).ConfigureAwait(true);
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationRead, _shared.TimeProvider.GetElapsedTime(startTimestamp), bytes: 0, succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationRead, _shared.TimeProvider.GetElapsedTime(startTimestamp), bytes: 0, succeeded: false);
            throw;
        }
    }

    private async ValueTask AppendStorageAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await _storage.AppendAsync(value, cancellationToken).ConfigureAwait(true);
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationAppend, _shared.TimeProvider.GetElapsedTime(startTimestamp), value.Length, succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationAppend, _shared.TimeProvider.GetElapsedTime(startTimestamp), value.Length, succeeded: false);
            throw;
        }
    }

    private async ValueTask ReplaceStorageAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
    {
        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await _storage.ReplaceAsync(value, cancellationToken).ConfigureAwait(true);
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationReplace, _shared.TimeProvider.GetElapsedTime(startTimestamp), value.Length, succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationReplace, _shared.TimeProvider.GetElapsedTime(startTimestamp), value.Length, succeeded: false);
            throw;
        }
    }

    private async ValueTask DeleteStorageAsync(CancellationToken cancellationToken)
    {
        var startTimestamp = _shared.TimeProvider.GetTimestamp();
        try
        {
            await _storage.DeleteAsync(cancellationToken).ConfigureAwait(true);
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationDelete, _shared.TimeProvider.GetElapsedTime(startTimestamp), bytes: 0, succeeded: true);
        }
        catch
        {
            _shared.Instruments.OnStorageOperation(JournalingInstruments.OperationDelete, _shared.TimeProvider.GetElapsedTime(startTimestamp), bytes: 0, succeeded: false);
            throw;
        }
    }

    private Task EnqueueOrGetPendingWorkItem<TWorkItem>(out bool didEnqueue)
        where TWorkItem : WorkItem, new()
    {
        foreach (var workItem in _workQueue)
        {
            if (workItem.GetType() != typeof(TWorkItem))
            {
                continue;
            }

            if (workItem is AppendJournalWorkItem or WriteSnapshotWorkItem
                && workItem.DeletionGeneration != _deletionGeneration)
            {
                continue;
            }

            workItem.RecordTraceContext();
            workItem.AddCaller();
            didEnqueue = false;
            return workItem.Task;
        }

        var newWorkItem = new TWorkItem();
        newWorkItem.EnqueuedTimestamp = _shared.TimeProvider.GetTimestamp();
        newWorkItem.DeletionGeneration = _deletionGeneration;
        newWorkItem.RecordTraceContext();
        _workQueue.Enqueue(newWorkItem);
        didEnqueue = true;
        return newWorkItem.Task;
    }

    private void BindState(string name, uint id)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(name, out var state))
            {
                _statesMap[id] = state;
                state.Reset(CreateJournalStreamWriter(new(id)));
            }
            else
            {
                var vessel = new RetiredStateMachine(new(id));

                // We must not make the vessel self-register with the manager, since it will
                // result in a late-registration after the manager is 'ready'. Instead we add it inline here.

                _states.Add(name, vessel);
                _statesMap[id] = vessel;
            }
        }
    }

    public long PendingWriteByteCount => _journalWriter.CommittedLength;

    /// <summary>
    /// Gets a count which advances whenever a registered state stages a change and never decreases, or a negative value
    /// when a registered state doesn't report its changes.
    /// </summary>
    /// <remarks>
    /// Every state which implements <see cref="IReportsStagedChanges"/> advances it when it is modified. A write captures
    /// changes without advancing it. Comparing two readings taken on the activation's scheduler shows whether any state
    /// staged a change between them. Orleans.Messaging uses it to tell whether a failed handler attempt staged changes,
    /// and treats a negative value as staged.
    /// </remarks>
    internal long StagedChangeCount => _hasUnreportedChanges ? -1 : _journalWriter.StagedChangeCount;

    void ILifecycleParticipant<IGrainLifecycle>.Participate(IGrainLifecycle observer)
        => observer.Subscribe(GrainLifecycleStage.SetupState, this);

    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken) => InitializeAsync(cancellationToken).AsTask();
    async Task ILifecycleObserver.OnStop(CancellationToken cancellationToken) => await StopAsync(cancellationToken).ConfigureAwait(false);

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _shutdownCancellation.Cancel();
        }

        _workSignal.Signal();
        try
        {
            if (_workLoop is { } task)
            {
                await task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            CancelQueuedWorkItems(_shutdownCancellation.Token);
            CompleteObservers(failure: null);
        }
    }

    private void CancelQueuedWorkItems(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            while (_workQueue.TryDequeue(out var workItem))
            {
                workItem.TrySetCanceled(cancellationToken);
            }
        }
    }

    void IDisposable.Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _shutdownCancellation.Dispose();
            _journalWriter.Dispose();
        }
    }

    private abstract class WorkItem : TaskCompletionSource
    {
        private List<ActivityLink>? _links;

        public long EnqueuedTimestamp { get; set; }

        public long DeletionGeneration { get; set; }

        public ActivityContext TraceParent { get; private set; }

        public IReadOnlyList<ActivityLink>? TraceLinks => _links;

        /// <summary>
        /// Gets the number of callers that have folded into this work item, including the
        /// caller that originally enqueued it. Mutated under the manager lock.
        /// </summary>
        public int CallerCount { get; private set; } = 1;

        protected WorkItem() : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }

        protected WorkItem(object? context) : base(context, TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }

        /// <summary>
        /// Increments the coalesced caller count when a new caller folds into this pending work item.
        /// </summary>
        public void AddCaller()
        {
            CallerCount++;
        }

        public void RecordTraceContext()
        {
            var current = Activity.Current;
            if (current is null || current.IdFormat != ActivityIdFormat.W3C)
            {
                return;
            }

            var context = current.Context;
            if (TraceParent == default)
            {
                TraceParent = context;
                return;
            }

            if (context == TraceParent)
            {
                return;
            }

            const int MaxLinks = 64;
            if (_links is null)
            {
                _links = new List<ActivityLink>();
            }
            else if (_links.Count >= MaxLinks)
            {
                return;
            }
            else
            {
                for (var i = 0; i < _links.Count; i++)
                {
                    if (_links[i].Context == context)
                    {
                        return;
                    }
                }
            }

            _links.Add(new ActivityLink(context));
        }
    }

    private static string? GetStorageQueueOperation(WorkItem workItem) =>
        workItem switch
        {
            AppendJournalWorkItem => JournalingInstruments.OperationAppend,
            WriteSnapshotWorkItem => JournalingInstruments.OperationSnapshot,
            DeleteStateWorkItem => JournalingInstruments.OperationDelete,
            _ => null
        };

    private sealed class InitializeWorkItem : WorkItem;

    private sealed class AppendJournalWorkItem : WorkItem;

    private sealed class WriteSnapshotWorkItem : WorkItem;

    private sealed class DeleteStateWorkItem : WorkItem;

    private sealed class RegisterStateWorkItem(string name) : WorkItem(name)
    {
        public string Name => (string)Task.AsyncState!;
    }

    private enum ManagerState : byte
    {
        Unknown,
        Ready,
        Fenced
    }

    private sealed class StateDirectory(
        JournaledStateManager manager,
        IDurableDictionaryCommandCodec<string, uint> codec) : IStateMachine, IDurableDictionaryCommandHandler<string, uint>
    {
        public const uint Id = 0u;

        private readonly JournaledStateManager _manager = manager;
        private readonly IDurableDictionaryCommandCodec<string, uint> _codec = codec;
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private JournalStreamWriter _writer;

        public uint this[string name] => _ids[name];

        void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            context.GetRequiredCommandCodec(entry.FormatKey, _codec).Apply(entry.Reader, this);

        public bool ContainsKey(string name) => _ids.ContainsKey(name);

        public bool TryGetValue(string name, out uint id) => _ids.TryGetValue(name, out id);

        public uint GetNextJournalStreamId()
        {
            var maxId = MinApplicationJournalStreamId - 1u;
            foreach (var id in _ids.Values)
            {
                if (id > maxId)
                {
                    maxId = id;
                }
            }

            return checked(maxId + 1);
        }

        public void Set(string name, uint id)
        {
            _codec.WriteSet(name, id, GetWriter());
            ApplySet(name, id);
        }

        public bool ApplyRemove(string name) => _ids.Remove(name);

        public void ResetVolatileState() => ((IStateMachine)this).Reset(_manager.CreateJournalStreamWriter(new(Id)));

        void IStateMachine.Reset(JournalStreamWriter writer)
        {
            _ids.Clear();
            _writer = writer;
        }

        void IStateMachine.WritePendingEntries(JournalStreamWriter writer) { }

        void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => _codec.WriteSnapshot(_ids, writer);


        void IDurableDictionaryCommandHandler<string, uint>.ApplySet(string key, uint value) => ApplySet(key, value);

        void IDurableDictionaryCommandHandler<string, uint>.ApplyRemove(string key) => ApplyRemove(key);

        void IDurableDictionaryCommandHandler<string, uint>.ApplyClear() => _ids.Clear();

        void IDurableDictionaryCommandHandler<string, uint>.Reset(int capacityHint)
        {
            _ids.Clear();
            _ids.EnsureCapacity(capacityHint);
        }

        private void ApplySet(string name, uint id)
        {
            _ids[name] = id;
            _manager.BindState(name, id);
        }

        private JournalStreamWriter GetWriter()
        {
            Debug.Assert(_writer.IsInitialized);
            return _writer;
        }
    }

    /// <summary>
    /// Used to track states that are not registered via user-code anymore, until time-based purging has elapsed.
    /// </summary>
    /// <remarks>Resurrecting of retired states is supported.</remarks>
    private sealed class RetiredStateTracker(
        JournaledStateManager manager, IDurableDictionaryCommandCodec<string, DateTime> codec)
            : DurableDictionary<string, DateTime>(codec)
    {
        public const uint Id = 1u;

        private readonly JournalStreamWriter _journalWriter = manager.CreateJournalStreamWriter(new(Id));

        public void ResetVolatileState() => ((IStateMachine)this).Reset(_journalWriter);

        protected override JournalStreamWriter GetWriter() => _journalWriter;
    }

    /// <summary>
    /// Used to keep retired states into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the journal upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredStateMachine Id = {StreamId.Value}")]
    private sealed class RetiredStateMachine(JournalStreamId streamId) : IStateMachine, IReportsStagedChanges
    {
        private readonly List<IPreservedJournalEntry> _preservedEntries = [];

        public JournalStreamId StreamId { get; } = streamId;

        public IReadOnlyList<IPreservedJournalEntry> PreservedEntries => _preservedEntries;

        void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            _preservedEntries.Add(new PreservedJournalEntry(entry.FormatKey, entry.Reader));

        void IStateMachine.WriteSnapshot(JournalStreamWriter snapshotWriter)
        {
            foreach (var entry in _preservedEntries)
            {
                snapshotWriter.AppendPreservedEntry(entry);
            }
        }

        void IStateMachine.Reset(JournalStreamWriter writer) => _preservedEntries.Clear();
        void IStateMachine.WritePendingEntries(JournalStreamWriter writer) { }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing work items.")]
    private static partial void LogErrorProcessingWorkItems(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Journal state preparation failed; pending changes remain staged.")]
    private static partial void LogStatePreparationFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "State \"{Name}\" was not found. I have substituted a placeholder for graceful time-based retirement.")]
    private static partial void LogRetiredStateDetected(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "State \"{Name}\" was previously retired (but not removed), and has hence been re-introduced. " +
                  "There is still time left before its permanent removal, so I will resurrect it.")]
    private static partial void LogRetiredStateComebackDetected(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Removing retired state \"{Name}\" and its data. Operation will be durably persisted shortly after compaction has finalized.")]
    private static partial void LogRemovingRetiredState(ILogger logger, string name);
}
