using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Orleans.Serialization.Buffers;
using Orleans.Runtime.Internal;

namespace Orleans.Journaling;

internal sealed partial class JournaledStateManager : IJournaledStateManager, IJournalStorageConsumer, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const uint MinApplicationJournalStreamId = 8u;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IJournaledState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, IJournaledState> _statesMap = [];
    private readonly JournaledStateManagerShared _shared;
    private readonly JournalBufferWriter _journalWriter;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateDirectory _journalStreamDirectory;
    private readonly RetiredStateTracker _retirementTracker;
    private Task? _workLoop;
    private ManagerState _state;
    private bool _migrationSnapshotRequired;

    public JournaledStateManager(JournaledStateManagerShared shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        _shared = shared;
        _journalWriter = _shared.JournalFormat.CreateWriter();
        var serviceProvider = _shared.ServiceProvider;
        var journalStreamIdsCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, uint>>(serviceProvider, WriteJournalFormatKey);
        var retirementTrackerCodec = JournalFormatServices.GetRequiredCommandCodec<IDurableDictionaryCommandCodec<string, DateTime>>(serviceProvider, WriteJournalFormatKey);

        // The list of known states is itself stored as a durable state with the implicit id 0.
        // This allows us to recover the list of states ids without having to store it separately.
        _journalStreamDirectory = new StateDirectory(this, journalStreamIdsCodec);
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;

        // The retirement tracker is a special internal state with a fixed id.
        // It is not stored in _journalStreamDirectory and does not participate in the general name->id mapping.
        _retirementTracker = new RetiredStateTracker(this, retirementTrackerCodec);
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;
    }

    internal string WriteJournalFormatKey => _shared.JournalFormatKey;

    internal IServiceProvider ServiceProvider => _shared.ServiceProvider;

    public void RegisterState(string name, IJournaledState state)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        _shutdownCancellation.Token.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_states.TryGetValue(name, out var existing))
            {
                if (existing is RetiredState vessel)
                {
                    // If the existing state is a vessel for a retired one, it means the state was loaded from a previous
                    // journal during recovery but has not been re-registered. We effectively are "staging" the resurrection of the state.
                    // The removal from the tracker is handled within the serialized loop. This is to prevent logical race conditions with the recovery process.
                    // We also make sure to apply any buffered data that could have occured while the vessel took this state's place.
                    state.Reset(CreateJournalStreamWriter(new(_journalStreamDirectory[name])));
                    var replayContext = new JournalReplayContext(this);
                    foreach (var entry in vessel.PreservedEntries)
                    {
                        using var buffer = new ArcBufferWriter();
                        buffer.Write(entry.Payload.Span);
                        state.ReplayEntry(new JournalEntry(entry.FormatKey, new JournalBufferReader(buffer.Reader, isCompleted: true)), replayContext);
                    }

                    var id = _journalStreamDirectory[name];
                    _states[name] = state;
                    _statesMap[id] = state;
                }
                else
                {
                    // A real state is already registered with this name, this must be a developer error.
                    throw new ArgumentException($"A state with the key '{name}' has already been registered.");
                }
            }
            else
            {
                _states.Add(name, state);
            }

            _workQueue.Enqueue(new RegisterStateWorkItem(name));
        }

        _workSignal.Signal();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdownCancellation.Token.ThrowIfCancellationRequested();
        Task task;
        bool didEnqueue;
        lock (_lock)
        {
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

        await task;
    }

    private Task Start()
    {
        using var suppressExecutionContext = new ExecutionContextSuppressor();
        return WorkLoop();
    }

    private async Task WorkLoop()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        var needsRecovery = true;
        while (!_shutdownCancellation.Token.IsCancellationRequested)
        {
            try
            {
                await _workSignal.WaitAsync().ConfigureAwait(true);
                _shutdownCancellation.Token.ThrowIfCancellationRequested();

                while (true)
                {
                    if (needsRecovery)
                    {
                        await RecoverAsync(_shutdownCancellation.Token).ConfigureAwait(true);
                        needsRecovery = false;
                    }

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

                    try
                    {
                        // Note that the implementation of each command is inlined to avoid allocating unnecessary async states.
                        // We are ok sacrificing some code organization for performance in the inner loop.
                        switch (workItem)
                        {
                        case AppendJournalWorkItem:
                        case WriteSnapshotWorkItem:
                        {
                            // TODO: decide whether it's best to snapshot or append. Eg, by summing the size of the most recent snapshots and the current journal length.
                            //       If the current journal length is greater than the snapshot size, then take a snapshot instead of appending more journal entries.
                            var isSnapshot = workItem is WriteSnapshotWorkItem
                                || _migrationSnapshotRequired
                                || _shared.Storage.IsCompactionRequested;
                            ArcBuffer committedBuffer = default;
                            ArcBuffer bufferToConsume = default;
                            var hasCommittedBuffer = false;
                            var hasBufferToConsume = false;
                            var bufferToConsumeIsCommittedBuffer = false;

                            lock (_lock)
                            {
                                if (isSnapshot)
                                {
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

                                    foreach (var (id, state) in _statesMap)
                                    {
                                        if (id is 0 || state is null)
                                        {
                                            continue;
                                        }

                                        AppendUpdatesOrSnapshotState(snapshotWriter, isSnapshot: true, id, state);
                                    }

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
                                else
                                {
                                    var flushCommittedOnly = _journalWriter.HasActiveEntry;
                                    if (!flushCommittedOnly)
                                    {
                                        // The map of state ids is itself stored as a durable state with the id 0.
                                        // This must be stored first, since it includes the identities of all other states, which are needed when replaying the journal.
                                        AppendUpdatesOrSnapshotState(_journalWriter, isSnapshot: false, StateDirectory.Id, _journalStreamDirectory);

                                        foreach (var (id, state) in _statesMap)
                                        {
                                            if (id is 0 || state is null)
                                            {
                                                continue;
                                            }

                                            AppendUpdatesOrSnapshotState(_journalWriter, isSnapshot: false, id, state);
                                        }
                                    }

                                    committedBuffer = _journalWriter.GetCommittedBuffer();
                                    bufferToConsume = committedBuffer;
                                    bufferToConsumeIsCommittedBuffer = true;
                                }

                                if (committedBuffer.Length == 0)
                                {
                                    committedBuffer.Dispose();
                                    if (bufferToConsumeIsCommittedBuffer)
                                    {
                                        bufferToConsume = default;
                                    }
                                }
                                else
                                {
                                    hasCommittedBuffer = true;
                                    hasBufferToConsume = true;
                                }
                            }

                            if (!hasCommittedBuffer && hasBufferToConsume && !bufferToConsumeIsCommittedBuffer)
                            {
                                bufferToConsume.Dispose();
                            }

                            if (hasCommittedBuffer)
                            {
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

                                var writeCompleted = false;
                                try
                                {
                                    if (isSnapshot)
                                    {
                                        await _shared.Storage.ReplaceAsync(writeSequence, _shutdownCancellation.Token).ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        await _shared.Storage.AppendAsync(writeSequence, _shutdownCancellation.Token).ConfigureAwait(true);
                                    }

                                    writeCompleted = true;
                                }
                                finally
                                {
                                    try
                                    {
                                        if (writeCompleted)
                                        {
                                            lock (_lock)
                                            {
                                                if (hasBufferToConsume)
                                                {
                                                    _journalWriter.Consume(bufferToConsume);
                                                }
                                            }
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

                                // Notify all states that the operation completed.
                                lock (_lock)
                                {
                                    foreach (var state in _states.Values)
                                    {
                                        state.OnWriteCompleted();
                                    }

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
                            // Clear storage.
                            await _shared.Storage.DeleteAsync(_shutdownCancellation.Token).ConfigureAwait(true);

                            lock (_lock)
                            {
                                // Reset the state id collection.
                                _journalStreamDirectory.ResetVolatileState();

                                // Allocate new state ids for each state.
                                // Doing so will trigger a reset, since _journalStreamDirectory will bind the state in question.
                                foreach (var (name, state) in _states)
                                {
                                    var id = _journalStreamDirectory.GetNextJournalStreamId();
                                    _journalStreamDirectory.Set(name, id);
                                }
                            }
                            break;
                        }

                        case InitializeWorkItem:
                        {
                            lock (_lock)
                            {
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

                        workItem.SetResult();
                    }
                    catch (Exception exception)
                    {
                        workItem.SetException(exception);
                        needsRecovery = true;
                    }
                }
            }
            catch (Exception exception)
            {
                needsRecovery = true;
                if (_shutdownCancellation.Token.IsCancellationRequested)
                {
                    return;
                }

                FaultQueuedWorkItems(exception);
                LogErrorProcessingWorkItems(_shared.Logger, exception);
            }
        }
    }

    private void FaultQueuedWorkItems(Exception exception)
    {
        lock (_lock)
        {
            while (_workQueue.TryDequeue(out var workItem))
            {
                workItem.TrySetException(exception);
            }

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

                if (state is RetiredState)
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
            if (state is RetiredState { PreservedEntries.Count: > 0 } retiredState)
            {
                throw new InvalidOperationException(
                    $"Cannot migrate journal to format key '{_shared.JournalFormatKey}' because stream " +
                    $"{retiredState.StreamId.Value} belongs to a state which is not currently registered. Register the state so it can be decoded " +
                    "and snapshotted in the configured format, or keep the previous journal format configured until the state can be retired.");
            }
        }
    }

    private JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => _journalWriter.CreateJournalStreamWriter(streamId);

    private static void AppendUpdatesOrSnapshotState(JournalBufferWriter journalWriter, bool isSnapshot, uint id, IJournaledState state)
    {
        var writer = journalWriter.CreateJournalStreamWriter(new(id));
        if (isSnapshot)
        {
            state.AppendSnapshot(writer);
        }
        else
        {
            state.AppendEntries(writer);
        }
    }

    public async ValueTask DeleteStateAsync(CancellationToken cancellationToken)
    {
        Task task;
        bool didEnqueue;
        lock (_lock)
        {
            task = EnqueueOrGetPendingWorkItem<DeleteStateWorkItem>(out didEnqueue);
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        await task;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ResetForRecovery();
        }

        await _shared.Storage.ReadAsync(this, cancellationToken).ConfigureAwait(true);

        lock (_lock)
        {
            foreach ((var name, var state) in _states)
            {
                state.OnRecoveryCompleted();

                if (state is RetiredState)
                {
                    // We can use TryAdd since recovery has finished.
                    if (_retirementTracker.TryAdd(name, _shared.TimeProvider.GetUtcNow().UtcDateTime))
                    {
                        LogRetiredStateDetected(_shared.Logger, name);
                    }
                }
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

        List<string>? retiredNames = null;
        foreach (var (name, state) in _states)
        {
            if (state is RetiredState)
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
    }

    internal void BindStateForReplay(JournalStreamId streamId, IJournaledState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            _statesMap[streamId.Value] = state;
            state.Reset(CreateJournalStreamWriter(streamId));
        }
    }

    internal IJournaledState ResolveState(JournalStreamId streamId)
    {
        if (!_statesMap.TryGetValue(streamId.Value, out var state))
        {
            state = new RetiredState(streamId);
            _statesMap[streamId.Value] = state;
        }

        return state;
    }

    private IJournalFormat GetJournalFormat(string journalFormatKey)
    {
        if (string.Equals(journalFormatKey, _shared.JournalFormatKey, StringComparison.Ordinal))
        {
            return _shared.JournalFormat;
        }

        return JournalFormatServices.GetRequiredJournalFormat(_shared.ServiceProvider, journalFormatKey);
    }

    private void ProcessRecoveryBuffer(JournalBufferReader buffer, IJournalFileMetadata? metadata)
    {
        if (buffer.Length == 0)
        {
            return;
        }

        var journalFormatKey = metadata?.Format is { } storedFormatKey
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

    void IJournalStorageConsumer.Read(JournalBufferReader buffer, IJournalFileMetadata? metadata) => ProcessRecoveryBuffer(buffer, metadata);

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

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task pendingWrite;
        bool didEnqueue;
        lock (_lock)
        {
            pendingWrite = _migrationSnapshotRequired || _shared.Storage.IsCompactionRequested
                ? EnqueueOrGetPendingWorkItem<WriteSnapshotWorkItem>(out didEnqueue)
                : EnqueueOrGetPendingWorkItem<AppendJournalWorkItem>(out didEnqueue);
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        await pendingWrite.WaitAsync(cancellationToken);
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

            didEnqueue = false;
            return workItem.Task;
        }

        var newWorkItem = new TWorkItem();
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
                var vessel = new RetiredState(new(id));

                // We must not make the vessel self-register with the manager, since it will
                // result in a late-registration after the manager is 'ready'. Instead we add it inline here.

                _states.Add(name, vessel);
                _statesMap[id] = vessel;
            }
        }
    }

    public bool TryGetState(string name, [NotNullWhen(true)] out IJournaledState? state) => _states.TryGetValue(name, out state);

    void ILifecycleParticipant<IGrainLifecycle>.Participate(IGrainLifecycle observer) => observer.Subscribe(GrainLifecycleStage.SetupState, this);
    Task ILifecycleObserver.OnStart(CancellationToken cancellationToken) => InitializeAsync(cancellationToken).AsTask();
    async Task ILifecycleObserver.OnStop(CancellationToken cancellationToken)
    {
        _shutdownCancellation.Cancel();
        _workSignal.Signal();
        if (_workLoop is { } task)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    void IDisposable.Dispose()
    {
        _shutdownCancellation.Dispose();
        _journalWriter.Dispose();
    }

    private abstract class WorkItem : TaskCompletionSource
    {
        protected WorkItem() : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }

        protected WorkItem(object? context) : base(context, TaskCreationOptions.RunContinuationsAsynchronously)
        {
        }

    }

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
        Ready
    }

    private sealed class StateDirectory(
        JournaledStateManager manager,
        IDurableDictionaryCommandCodec<string, uint> codec) : IJournaledState, IDurableDictionaryCommandHandler<string, uint>
    {
        public const uint Id = 0u;

        private readonly JournaledStateManager _manager = manager;
        private readonly IDurableDictionaryCommandCodec<string, uint> _codec = codec;
        private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
        private JournalStreamWriter _writer;

        public uint this[string name] => _ids[name];

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
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

        public void ResetVolatileState() => ((IJournaledState)this).Reset(_manager.CreateJournalStreamWriter(new(Id)));

        void IJournaledState.Reset(JournalStreamWriter writer)
        {
            _ids.Clear();
            _writer = writer;
        }

        void IJournaledState.AppendEntries(JournalStreamWriter writer) { }

        void IJournaledState.AppendSnapshot(JournalStreamWriter writer) => _codec.WriteSnapshot(_ids, writer);

        IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();

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

        public void ResetVolatileState() => ((IJournaledState)this).Reset(_journalWriter);

        protected override JournalStreamWriter GetWriter() => _journalWriter;
    }

    /// <summary>
    /// Used to keep retired states into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the journal upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredState Id = {StreamId.Value}")]
    private sealed class RetiredState(JournalStreamId streamId) : IJournaledState
    {
        private readonly List<IPreservedJournalEntry> _preservedEntries = [];

        public JournalStreamId StreamId { get; } = streamId;

        public IReadOnlyList<IPreservedJournalEntry> PreservedEntries => _preservedEntries;

        void IJournaledState.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
            _preservedEntries.Add(new PreservedJournalEntry(entry.FormatKey, entry.Reader));

        void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
        {
            foreach (var entry in _preservedEntries)
            {
                snapshotWriter.AppendPreservedEntry(entry);
            }
        }

        void IJournaledState.Reset(JournalStreamWriter writer) => _preservedEntries.Clear();
        void IJournaledState.AppendEntries(JournalStreamWriter writer) { }
        IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing work items.")]
    private static partial void LogErrorProcessingWorkItems(ILogger logger, Exception exception);

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
