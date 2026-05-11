using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Runtime.Internal;

namespace Orleans.Journaling;

internal sealed partial class JournaledStateManager : IStateManager, IStateResolver, IJournalStreamWriterTarget, IJournalEntryWriterCompletion, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const int MinApplicationJournalStreamId = 8;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IJournaledState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, IJournaledState> _statesMap = [];
    private readonly IJournalStorage _storage;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IJournalFormat _writeJournalFormat;
    private readonly string _writeJournalFormatKey;
    private readonly string _legacyJournalFormatKey;
    private readonly ILogger<JournaledStateManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateDirectory _journalStreamDirectory;
    private readonly RetiredStateTracker _retirementTracker;
    private readonly TimeSpan _retirementGracePeriod;
    private Task? _workLoop;
    private ManagerState _state;
    private Task? _pendingWrite;
    private ulong _nextJournalStreamId = MinApplicationJournalStreamId;
    private IJournalBatchWriter? _currentJournalBatchWriter;
    private bool _migrationSnapshotRequired;

    public JournaledStateManager(
        IJournalStorage storage,
        ILogger<JournaledStateManager> logger,
        IOptions<StateManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider,
        [FromKeyedServices(JournalFormatServices.JournalFormatKeyServiceKey)] string journalFormatKey)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        _storage = storage;
        _serviceProvider = serviceProvider;
        _writeJournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey);
        _writeJournalFormat = JournalFormatServices.GetRequiredJournalFormat(serviceProvider, _writeJournalFormatKey);
        var journalStreamIdsCodec = JournalFormatServices.GetRequiredOperationCodec<IDurableDictionaryOperationCodec<string, ulong>>(serviceProvider, _writeJournalFormatKey);
        var retirementTrackerCodec = JournalFormatServices.GetRequiredOperationCodec<IDurableDictionaryOperationCodec<string, DateTime>>(serviceProvider, _writeJournalFormatKey);
        _logger = logger;
        _timeProvider = timeProvider;
        _legacyJournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(options.Value.LegacyJournalFormatKey);
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        // The list of known states is itself stored as a durable state with the implicit id 0.
        // This allows us to recover the list of states ids without having to store it separately.
        _journalStreamDirectory = new StateDirectory(this, journalStreamIdsCodec);
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;

        // The retirement tracker is a special internal state with a fixed id.
        // It is not stored in _journalStreamDirectory and does not participate in the general name->id mapping.
        _retirementTracker = new RetiredStateTracker(this, retirementTrackerCodec);
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;
    }

    internal JournaledStateManager(
        IJournalStorage storage,
        ILogger<JournaledStateManager> logger,
        IOptions<StateManagerOptions> options,
        IDurableDictionaryOperationCodec<string, ulong> journalStreamIdsCodec,
        IDurableDictionaryOperationCodec<string, DateTime> retirementTrackerCodec,
        TimeProvider timeProvider,
        IJournalFormat journalFormat,
        string? journalFormatKey = null)
    {
        ArgumentNullException.ThrowIfNull(journalFormat);
        _storage = storage;
        _writeJournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey ?? OrleansBinaryJournalFormat.JournalFormatKey);
        _writeJournalFormat = journalFormat;
        _logger = logger;
        _timeProvider = timeProvider;
        _legacyJournalFormatKey = JournalFormatServices.ValidateJournalFormatKey(options.Value.LegacyJournalFormatKey);
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        _journalStreamDirectory = new StateDirectory(this, journalStreamIdsCodec);
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;

        _retirementTracker = new RetiredStateTracker(this, retirementTrackerCodec);
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;
    }

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
                    foreach (var entry in vessel.FormattedEntries)
                    {
                        entry.Apply(state);
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

            _workQueue.Enqueue(new WorkItem(WorkItemType.RegisterState, completion: null)
            {
                Context = name
            });
        }

        _workSignal.Signal();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _shutdownCancellation.Token.ThrowIfCancellationRequested();
        Debug.Assert(_workLoop is null, "InitializeAsync should only be called once.");
        _workLoop = Start();

        Task task;
        lock (_lock)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            _workQueue.Enqueue(new WorkItem(WorkItemType.Initialize, completion));
        }

        _workSignal.Signal();
        await task;
    }

    private Task Start()
    {
        using var suppressExecutionContext = new ExecutionContextSuppressor();
        return WorkLoop();
    }

    private async Task WorkLoop()
    {
        var cancellationToken = _shutdownCancellation.Token;
        using var cancellationRegistration = cancellationToken.Register(state => ((JournaledStateManager)state!)._workSignal.Signal(), this);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.ForceYielding);
        var needsRecovery = true;
        while (true)
        {
            try
            {
                await _workSignal.WaitAsync().ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();

                while (true)
                {
                    if (needsRecovery)
                    {
                        await RecoverAsync(cancellationToken).ConfigureAwait(true);
                        needsRecovery = false;
                    }

                    WorkItem workItem;
                    lock (_lock)
                    {
                        if (!_workQueue.TryDequeue(out workItem))
                        {
                            // Wait for the queue to be signaled again.
                            break;
                        }
                    }

                    try
                    {
                        // Note that the implementation of each command is inlined to avoid allocating unnecessary async states.
                        // We are ok sacrificing some code organization for performance in the inner loop.
                        if (workItem.Type is WorkItemType.AppendJournal or WorkItemType.WriteSnapshot)
                        {
                            // TODO: decide whether it's best to snapshot or append. Eg, by summing the size of the most recent snapshots and the current journal length.
                            //       If the current journal length is greater than the snapshot size, then take a snapshot instead of appending more journal entries.
                            var isSnapshot = workItem.Type is WorkItemType.WriteSnapshot;
                            IJournalBatchWriter? journalBatchWriter;
                            ArcBuffer committedBuffer = default;

                            lock (_lock)
                            {
                                if (isSnapshot)
                                {
                                    // If there are pending writes, reset them since they will be captured by the snapshot instead.
                                    // If we did not do this, the journal would begin with some writes which would be followed by a snapshot which also included those writes.
                                    _currentJournalBatchWriter?.Reset();

                                    if (_retirementTracker.Count > 0)
                                    {
                                        RetireOrResurectStates();
                                    }

                                    if (_migrationSnapshotRequired)
                                    {
                                        ThrowIfMigrationBlockedByRetiredStates();
                                    }
                                }

                                var currentJournalBatchWriter = GetOrCreateCurrentJournalBatchWriter();

                                // The map of state ids is itself stored as a durable state with the id 0.
                                // This must be stored first, since it includes the identities of all other states, which are needed when replaying the journal.
                                // If we removed retired states, this snapshot will persist that change.
                                AppendUpdatesOrSnapshotState(currentJournalBatchWriter, isSnapshot, StateDirectory.Id, _journalStreamDirectory);

                                foreach (var (id, state) in _statesMap)
                                {
                                    if (id is 0 || state is null)
                                    {
                                        continue;
                                    }

                                    AppendUpdatesOrSnapshotState(currentJournalBatchWriter, isSnapshot, id, state);
                                }

                                committedBuffer = currentJournalBatchWriter.GetCommittedBuffer();
                                if (committedBuffer.Length == 0)
                                {
                                    committedBuffer.Dispose();
                                    journalBatchWriter = null;
                                }
                                else
                                {
                                    journalBatchWriter = currentJournalBatchWriter;
                                    _currentJournalBatchWriter = null;
                                }
                            }

                            if (journalBatchWriter is not null)
                            {
                                var writeSequence = committedBuffer.AsReadOnlySequence();
#if DEBUG
                                // Defensive: copy the sequence into a pooled buffer so we can poison it
                                // after the storage call returns. Any IJournalStorage implementation that
                                // retains the sequence past task completion (a violation of the documented
                                // contract on AppendAsync/ReplaceAsync) will read 0xCC bytes when it next
                                // touches the buffer, surfacing the bug loudly in tests instead of letting
                                // recycled pool data hide it.
                                var debugPoisonLength = checked((int)writeSequence.Length);
                                var debugPoisonBuffer = ArrayPool<byte>.Shared.Rent(debugPoisonLength);
                                writeSequence.CopyTo(debugPoisonBuffer);
                                writeSequence = new ReadOnlySequence<byte>(debugPoisonBuffer, 0, debugPoisonLength);
#endif

                                var writeSucceeded = false;
                                try
                                {
                                    if (isSnapshot)
                                    {
                                        await _storage.ReplaceAsync(writeSequence, cancellationToken).ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        await _storage.AppendAsync(writeSequence, cancellationToken).ConfigureAwait(true);
                                    }

                                    writeSucceeded = true;
                                }
                                finally
                                {
                                    committedBuffer.Dispose();
#if DEBUG
                                    debugPoisonBuffer.AsSpan(0, debugPoisonLength).Fill(0xCC);
                                    ArrayPool<byte>.Shared.Return(debugPoisonBuffer);
#endif
                                    if (!writeSucceeded)
                                    {
                                        journalBatchWriter.Dispose();
                                    }
                                }

                                // Notify all states that the operation completed.
                                lock (_lock)
                                {
                                    if (_currentJournalBatchWriter is null)
                                    {
                                        journalBatchWriter.Reset();
                                        _currentJournalBatchWriter = journalBatchWriter;
                                    }
                                    else
                                    {
                                        journalBatchWriter.Dispose();
                                    }

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
                        }
                        else if (workItem.Type is WorkItemType.DeleteState)
                        {
                            // Clear storage.
                            await _storage.DeleteAsync(cancellationToken).ConfigureAwait(true);

                            lock (_lock)
                            {
                                // Reset the state id collection.
                                _journalStreamDirectory.ResetVolatileState();

                                // Allocate new state ids for each state.
                                // Doing so will trigger a reset, since _journalStreamDirectory will bind the state in question.
                                _nextJournalStreamId = MinApplicationJournalStreamId;
                                foreach (var (name, state) in _states)
                                {
                                    var id = _nextJournalStreamId++;
                                    _journalStreamDirectory.Set(name, id);
                                }
                            }
                        }
                        else if (workItem.Type is WorkItemType.Initialize)
                        {
                            lock (_lock)
                            {
                                _state = ManagerState.Ready;
                            }
                        }
                        else if (workItem.Type is WorkItemType.RegisterState)
                        {
                            lock (_lock)
                            {
                                if (_state is not ManagerState.Unknown)
                                {
                                    throw new NotSupportedException("Registering a state after activation is not supported.");
                                }

                                var name = (string)workItem.Context!;
                                if (!_journalStreamDirectory.ContainsKey(name))
                                {
                                    // Doing so will trigger a reset, since _journalStreamDirectory will bind the state in question.
                                    _journalStreamDirectory.Set(name, _nextJournalStreamId++);
                                }
                            }
                        }
                        else
                        {
                            Debug.Fail($"The command {workItem.Type} is unsupported");
                        }

                        workItem.CompletionSource?.SetResult();
                    }
                    catch (Exception exception)
                    {
                        workItem.CompletionSource?.SetException(exception);
                        needsRecovery = true;
                    }
                }
            }
            catch (Exception exception)
            {
                needsRecovery = true;
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                FaultQueuedWorkItems(exception);
                LogErrorProcessingWorkItems(_logger, exception);
            }
        }
    }

    private void FaultQueuedWorkItems(Exception exception)
    {
        lock (_lock)
        {
            while (_workQueue.TryDequeue(out var workItem))
            {
                workItem.CompletionSource?.TrySetException(exception);
            }
        }
    }

    private void RetireOrResurectStates()
    {
        foreach (var (name, timestamp) in _retirementTracker)
        {
            var isDuetime = _timeProvider.GetUtcNow().UtcDateTime - timestamp >= _retirementGracePeriod;
            if (isDuetime && _journalStreamDirectory.TryGetValue(name, out var id))
            {
                var state = _states[name];

                Debug.Assert(state is not null);

                if (state is RetiredState)
                {
                    LogRemovingRetiredState(_logger, name);

                    // Since we are permanently removing this state, we will clean it up by reseting it.
                    state.Reset(CreateJournalStreamWriter(new(id)));

                    _statesMap.Remove(id);
                    // We remove these from memory only, since the snapshot will persist these changes.
                    _journalStreamDirectory.ApplyRemove(name);
                    _retirementTracker.ApplyRemove(name);
                }
                else
                {
                    LogRetiredStateComebackDetected(_logger, name);
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
            if (state is RetiredState { FormattedEntries.Count: > 0 } retiredState)
            {
                throw new InvalidOperationException(
                    $"Cannot migrate journal to format key '{_writeJournalFormatKey}' because stream " +
                    $"{retiredState.StreamId.Value} belongs to a state which is not currently registered. Register the state so it can be decoded " +
                    "and snapshotted in the configured format, or keep the previous journal format configured until the state can be retired.");
            }
        }
    }

    private IJournalBatchWriter GetOrCreateCurrentJournalBatchWriter() => _currentJournalBatchWriter ??= _writeJournalFormat.CreateWriter();

    private JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    private static void AppendUpdatesOrSnapshotState(IJournalBatchWriter journalBatchWriter, bool isSnapshot, ulong id, IJournaledState state)
    {
        var writer = journalBatchWriter.CreateJournalStreamWriter(new(id));
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
        lock (_lock)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            task = completion.Task;
            _workQueue.Enqueue(new WorkItem(WorkItemType.DeleteState, completion));
        }

        _workSignal.Signal();
        await task;
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            ResetForRecovery();
        }

        var recoveryConsumer = new RecoveryJournalStorageConsumer(this);
        await _storage.ReadAsync(recoveryConsumer, cancellationToken).ConfigureAwait(true);

        lock (_lock)
        {
            foreach ((var name, var state) in _states)
            {
                state.OnRecoveryCompleted();

                if (state is RetiredState)
                {
                    // We can use TryAdd since recovery has finished.
                    if (_retirementTracker.TryAdd(name, _timeProvider.GetUtcNow().UtcDateTime))
                    {
                        LogRetiredStateDetected(_logger, name);
                    }
                }
            }
        }
    }

    private void ResetForRecovery()
    {
        _currentJournalBatchWriter?.Reset();
        _migrationSnapshotRequired = false;
        _statesMap.Clear();
        _statesMap[StateDirectory.Id] = _journalStreamDirectory;
        _statesMap[RetiredStateTracker.Id] = _retirementTracker;
        _nextJournalStreamId = MinApplicationJournalStreamId;

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

    IJournaledState IStateResolver.ResolveState(JournalStreamId streamId)
        => ResolveState(streamId);

    private IJournaledState ResolveState(JournalStreamId streamId)
    {
        if (!_statesMap.TryGetValue(streamId.Value, out var state))
        {
            state = new RetiredState(streamId);
            _statesMap[streamId.Value] = state;
        }

        return state;
    }

    object IStateResolver.GetOperationCodec(IJournaledState state)
        => GetOperationCodec(state, _writeJournalFormatKey);

    private object GetOperationCodec(IJournaledState state, string journalFormatKey)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.Equals(journalFormatKey, _writeJournalFormatKey, StringComparison.Ordinal))
        {
            return state.OperationCodec;
        }

        return JournalFormatServices.GetRequiredOperationCodec(
            GetServiceProviderForFormat(journalFormatKey),
            journalFormatKey,
            state.OperationCodecServiceType);
    }

    private IJournalFormat GetJournalFormat(string journalFormatKey)
    {
        if (string.Equals(journalFormatKey, _writeJournalFormatKey, StringComparison.Ordinal))
        {
            return _writeJournalFormat;
        }

        return JournalFormatServices.GetRequiredJournalFormat(
            GetServiceProviderForFormat(journalFormatKey),
            journalFormatKey);
    }

    private IServiceProvider GetServiceProviderForFormat(string journalFormatKey)
        => _serviceProvider ?? throw new InvalidOperationException(
            $"Cannot recover journal format key '{journalFormatKey}' because this state manager was constructed without a service provider for keyed format resolution.");

    private void ProcessRecoveryBuffer(JournalReadBuffer buffer, IJournalFileMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (buffer.Length == 0)
        {
            return;
        }

        var journalFormatKey = metadata.Format is { } storedFormatKey
            ? JournalFormatServices.ValidateJournalFormatKey(storedFormatKey)
            : _legacyJournalFormatKey;
        try
        {
            if (!string.Equals(journalFormatKey, _writeJournalFormatKey, StringComparison.Ordinal))
            {
                _migrationSnapshotRequired = true;
            }

            var journalFormat = GetJournalFormat(journalFormatKey);
            journalFormat.Read(buffer, new RecoveryStateResolver(this, journalFormatKey));

            if (buffer.IsCompleted && buffer.Length > 0)
            {
                throw new InvalidOperationException("The journal format did not consume the completed journal data.");
            }
        }
        catch (Exception exception) when (ShouldWrapRecoveryFormatException(exception))
        {
            throw CreateRecoveryFormatException(exception, journalFormatKey);
        }
    }

    private static bool ShouldWrapRecoveryFormatException(Exception exception) =>
        exception is not OperationCanceledException && !IsRecoveryFormatException(exception);

    private static bool IsRecoveryFormatException(Exception exception) =>
        exception is InvalidOperationException { InnerException: not null }
        && exception.Message?.StartsWith("Failed to recover journaling state using journal format key ", StringComparison.Ordinal) == true;

    private InvalidOperationException CreateRecoveryFormatException(Exception exception, string journalFormatKey) =>
        new(
            $"Failed to recover journaling state using journal format key '{journalFormatKey}'. " +
            $"The configured write journal format key is '{_writeJournalFormatKey}'.",
            exception);

    public async ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task? pendingWrite;
        var didEnqueue = false;
        lock (_lock)
        {
            // If the pending write is faulted, recovery will need to be performed.
            // For now, await it so that we can propagate the exception consistently.
            if (_pendingWrite is not { IsFaulted: true })
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingWrite = completion.Task;
                var workItemType = _migrationSnapshotRequired || _storage.IsCompactionRequested
                    ? WorkItemType.WriteSnapshot
                    : WorkItemType.AppendJournal;

                _workQueue.Enqueue(new WorkItem(workItemType, completion));
                didEnqueue = true;
            }

            pendingWrite = _pendingWrite;
        }

        if (didEnqueue)
        {
            _workSignal.Signal();
        }

        if (pendingWrite is { } task)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    private void BindState(string name, ulong id)
    {
        lock (_lock)
        {
            if (id >= _nextJournalStreamId)
            {
                _nextJournalStreamId = id + 1;
            }

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
        _currentJournalBatchWriter?.Dispose();
    }

    JournalEntryWriter IJournalStreamWriterTarget.BeginEntry(JournalStreamId streamId, IJournalEntryWriterCompletion? completion)
    {
        if (completion is not null)
        {
            throw new InvalidOperationException("Manager-backed journal writers do not support external completion callbacks.");
        }

        EnterLock();
        try
        {
            return GetOrCreateCurrentJournalBatchWriter().CreateJournalStreamWriter(streamId).BeginEntryWriter(this);
        }
        catch
        {
            ExitLock();
            throw;
        }
    }

    void IJournalStreamWriterTarget.AppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
    {
        EnterLock();
        try
        {
            GetOrCreateCurrentJournalBatchWriter().CreateJournalStreamWriter(streamId).AppendFormattedEntry(entry);
        }
        finally
        {
            ExitLock();
        }
    }

    bool IJournalStreamWriterTarget.TryAppendFormattedEntry(JournalStreamId streamId, IFormattedJournalEntry entry)
    {
        EnterLock();
        try
        {
            return GetOrCreateCurrentJournalBatchWriter().CreateJournalStreamWriter(streamId).TryAppendFormattedEntry(entry);
        }
        finally
        {
            ExitLock();
        }
    }

    void IJournalEntryWriterCompletion.CompleteEntryWrite() => ExitLock();

    private void EnterLock()
    {
#if NET9_0_OR_GREATER
        _lock.Enter();
#else
        Monitor.Enter(_lock);
#endif
    }

    private void ExitLock()
    {
#if NET9_0_OR_GREATER
        _lock.Exit();
#else
        Monitor.Exit(_lock);
#endif
    }

    private readonly struct WorkItem(WorkItemType type, TaskCompletionSource? completion)
    {
        public WorkItemType Type { get; } = type;
        public TaskCompletionSource? CompletionSource { get; } = completion;
        public object? Context { get; init; }
    }

    private enum WorkItemType
    {
        Initialize,
        AppendJournal,
        WriteSnapshot,
        DeleteState,
        RegisterState
    }

    private enum ManagerState
    {
        Unknown,
        Ready
    }

    private sealed class RecoveryJournalStorageConsumer(JournaledStateManager manager) : IJournalStorageConsumer
    {
        public void Consume(JournalReadBuffer buffer, IJournalFileMetadata metadata) => manager.ProcessRecoveryBuffer(buffer, metadata);
    }

    private sealed class RecoveryStateResolver(JournaledStateManager manager, string journalFormatKey) : IStateResolver
    {
        public IJournaledState ResolveState(JournalStreamId streamId) => manager.ResolveState(streamId);

        public object GetOperationCodec(IJournaledState state) => manager.GetOperationCodec(state, journalFormatKey);
    }

    private sealed class StateDirectory(
        JournaledStateManager manager,
        IDurableDictionaryOperationCodec<string, ulong> codec) : IJournaledState, IDurableDictionaryOperationHandler<string, ulong>
    {
        public const int Id = 0;

        private readonly JournaledStateManager _manager = manager;
        private readonly IDurableDictionaryOperationCodec<string, ulong> _codec = codec;
        private readonly Dictionary<string, ulong> _ids = new(StringComparer.Ordinal);
        private JournalStreamWriter _storage;

        public ulong this[string name] => _ids[name];

        object IJournaledState.OperationCodec => _codec;

        Type IJournaledState.OperationCodecServiceType => typeof(IDurableDictionaryOperationCodec<string, ulong>);

        public bool ContainsKey(string name) => _ids.ContainsKey(name);

        public bool TryGetValue(string name, out ulong id) => _ids.TryGetValue(name, out id);

        public void Set(string name, ulong id)
        {
            _codec.WriteSet(name, id, GetStorage());
            ApplySet(name, id);
        }

        public bool ApplyRemove(string name) => _ids.Remove(name);

        public void ResetVolatileState() => ((IJournaledState)this).Reset(_manager.CreateJournalStreamWriter(new(Id)));

        void IJournaledState.Reset(JournalStreamWriter writer)
        {
            _ids.Clear();
            _storage = writer;
        }

        void IJournaledState.AppendEntries(JournalStreamWriter writer) { }

        void IJournaledState.AppendSnapshot(JournalStreamWriter writer) => _codec.WriteSnapshot(_ids, writer);

        IJournaledState IJournaledState.DeepCopy() => throw new NotSupportedException();

        void IDurableDictionaryOperationHandler<string, ulong>.ApplySet(string key, ulong value) => ApplySet(key, value);

        void IDurableDictionaryOperationHandler<string, ulong>.ApplyRemove(string key) => ApplyRemove(key);

        void IDurableDictionaryOperationHandler<string, ulong>.ApplyClear() => _ids.Clear();

        void IDurableDictionaryOperationHandler<string, ulong>.Reset(int capacityHint)
        {
            _ids.Clear();
            _ids.EnsureCapacity(capacityHint);
        }

        private void ApplySet(string name, ulong id)
        {
            _ids[name] = id;
            _manager.BindState(name, id);
        }

        private JournalStreamWriter GetStorage()
        {
            Debug.Assert(_storage.IsInitialized);
            return _storage;
        }
    }

    /// <summary>
    /// Used to track states that are not registered via user-code anymore, until time-based purging has elapsed.
    /// </summary>
    /// <remarks>Resurrecting of retired states is supported.</remarks>
    private sealed class RetiredStateTracker(
        JournaledStateManager manager, IDurableDictionaryOperationCodec<string, DateTime> codec)
            : DurableDictionary<string, DateTime>(codec)
    {
        public const int Id = 1;

        private readonly JournalStreamWriter _journalWriter = manager.CreateJournalStreamWriter(new(Id));

        public void ResetVolatileState() => ((IJournaledState)this).Reset(_journalWriter);

        protected override JournalStreamWriter GetStorage() => _journalWriter;
    }

    /// <summary>
    /// Used to keep retired states into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the journal upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredState Id = {StreamId.Value}")]
    private sealed class RetiredState(JournalStreamId streamId) : IJournaledState, IFormattedJournalEntryBuffer
    {
        private static readonly object NoOpCodec = new();
        private readonly List<IFormattedJournalEntry> _formattedEntries = [];

        public JournalStreamId StreamId { get; } = streamId;

        public IReadOnlyList<IFormattedJournalEntry> FormattedEntries => _formattedEntries;

        object IJournaledState.OperationCodec => NoOpCodec;

        Type IJournaledState.OperationCodecServiceType => typeof(object);

        public void AddFormattedEntry(IFormattedJournalEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _formattedEntries.Add(entry);
        }

        void IJournaledState.AppendSnapshot(JournalStreamWriter snapshotWriter)
        {
            foreach (var entry in _formattedEntries)
            {
                snapshotWriter.AppendFormattedEntry(entry);
            }
        }

        void IJournaledState.Reset(JournalStreamWriter writer) => _formattedEntries.Clear();
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
