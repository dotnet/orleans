using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Runtime.Internal;

namespace Orleans.Journaling;

internal sealed partial class JournalStateMachineManager : IStateMachineManager, IStateMachineResolver, IJournalStreamWriterTarget, IJournalEntryWriterCompletion, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const int MinApplicationJournalStreamId = 8;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IDurableStateMachine> _stateMachines = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, IDurableStateMachine> _stateMachinesMap = [];
    private readonly IJournalStorage _storage;
    private readonly IJournalFormat _journalFormat;
    private readonly string _journalFormatKey;
    private readonly ILogger<JournalStateMachineManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateMachineDirectory _journalStreamDirectory;
    private readonly RetiredStateMachineTracker _retirementTracker;
    private readonly TimeSpan _retirementGracePeriod;
    private Task? _workLoop;
    private ManagerState _state;
    private Task? _pendingWrite;
    private ulong _nextJournalStreamId = MinApplicationJournalStreamId;
    private IJournalBatchWriter? _currentJournalBatchWriter;

    public JournalStateMachineManager(
        IJournalStorage storage,
        ILogger<JournalStateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider,
        [FromKeyedServices(JournalFormatServices.JournalFormatKeyServiceKey)] string journalFormatKey)
    {
        _storage = storage;
        _journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey);
        _journalFormat = JournalFormatServices.GetRequiredKeyedService<IJournalFormat>(serviceProvider, _journalFormatKey);
        var dictionaryCodecProvider = JournalFormatServices.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(serviceProvider, _journalFormatKey);
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        // The list of known state machines is itself stored as a durable state machine with the implicit id 0.
        // This allows us to recover the list of state machines ids without having to store it separately.
        _journalStreamDirectory = new StateMachineDirectory(this, dictionaryCodecProvider.GetCodec<string, ulong>());
        _stateMachinesMap[StateMachineDirectory.Id] = _journalStreamDirectory;

        // The retirement tracker is a special internal state machine with a fixed id.
        // It is not stored in _journalStreamDirectory and does not participate in the general name->id mapping.
        _retirementTracker = new RetiredStateMachineTracker(this, dictionaryCodecProvider.GetCodec<string, DateTime>());
        _stateMachinesMap[RetiredStateMachineTracker.Id] = _retirementTracker;
    }

    internal JournalStateMachineManager(
        IJournalStorage storage,
        ILogger<JournalStateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        IDurableDictionaryOperationCodec<string, ulong> journalStreamIdsCodec,
        IDurableDictionaryOperationCodec<string, DateTime> retirementTrackerCodec,
        TimeProvider timeProvider,
        IJournalFormat? journalFormat = null,
        string? journalFormatKey = null)
    {
        _storage = storage;
        _journalFormatKey = JournalFormatServices.ValidateJournalFormatKey(journalFormatKey ?? OrleansBinaryJournalFormat.JournalFormatKey);
        _journalFormat = journalFormat ?? OrleansBinaryJournalFormat.Instance;
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        _journalStreamDirectory = new StateMachineDirectory(this, journalStreamIdsCodec);
        _stateMachinesMap[StateMachineDirectory.Id] = _journalStreamDirectory;

        _retirementTracker = new RetiredStateMachineTracker(this, retirementTrackerCodec);
        _stateMachinesMap[RetiredStateMachineTracker.Id] = _retirementTracker;
    }

    public void RegisterStateMachine(string name, IDurableStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        _shutdownCancellation.Token.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_stateMachines.TryGetValue(name, out var machine))
            {
                if (machine is RetiredStateMachine vessel)
                {
                    // If the existing machine is a vessel for a retired one, it means the machine was loaded from a previous
                    // journal during recovery but has not been re-registered. We effectively are "staging" the resurrection of the machine.
                    // The removal from the tracker is handled within the serialized loop. This is to prevent logical race conditions with the recovery process.
                    // We also make sure to apply any buffered data that could have occured while the vessel took this machine's place.
                    stateMachine.Reset(CreateJournalStreamWriter(new(_journalStreamDirectory[name])));
                    foreach (var entry in vessel.FormattedEntries)
                    {
                        entry.Apply(stateMachine);
                    }

                    var id = _journalStreamDirectory[name];
                    _stateMachines[name] = stateMachine;
                    _stateMachinesMap[id] = stateMachine;
                }
                else
                {
                    // A real state machine is already registered with this name, this must be a developer error.
                    throw new ArgumentException($"A state machine with the key '{name}' has already been registered.");
                }
            }
            else
            {
                _stateMachines.Add(name, stateMachine);
            }

            _workQueue.Enqueue(new WorkItem(WorkItemType.RegisterStateMachine, completion: null)
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
        using var cancellationRegistration = cancellationToken.Register(state => ((JournalStateMachineManager)state!)._workSignal.Signal(), this);
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
                        // Note that the implementation of each command is inlined to avoid allocating unnecessary async state machines.
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
                                        RetireOrResurectStateMachines();
                                    }
                                }

                                var currentJournalBatchWriter = GetOrCreateCurrentJournalBatchWriter();

                                // The map of state machine ids is itself stored as a durable state machine with the id 0.
                                // This must be stored first, since it includes the identities of all other state machines, which are needed when replaying the journal.
                                // If we removed retired machines, this snapshot will persist that change.
                                AppendUpdatesOrSnapshotStateMachine(currentJournalBatchWriter, isSnapshot, StateMachineDirectory.Id, _journalStreamDirectory);

                                foreach (var (id, stateMachine) in _stateMachinesMap)
                                {
                                    if (id is 0 || stateMachine is null)
                                    {
                                        continue;
                                    }

                                    AppendUpdatesOrSnapshotStateMachine(currentJournalBatchWriter, isSnapshot, id, stateMachine);
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
                                var writeSucceeded = false;
                                try
                                {
                                    if (isSnapshot)
                                    {
                                        await _storage.ReplaceAsync(committedBuffer.AsReadOnlySequence(), cancellationToken).ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        await _storage.AppendAsync(committedBuffer.AsReadOnlySequence(), cancellationToken).ConfigureAwait(true);
                                    }

                                    writeSucceeded = true;
                                }
                                finally
                                {
                                    committedBuffer.Dispose();
                                    if (!writeSucceeded)
                                    {
                                        journalBatchWriter.Dispose();
                                    }
                                }

                                // Notify all state machines that the operation completed.
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

                                    foreach (var stateMachine in _stateMachines.Values)
                                    {
                                        stateMachine.OnWriteCompleted();
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
                                // Reset the state machine id collection.
                                _journalStreamDirectory.ResetVolatileState();

                                // Allocate new state machine ids for each state machine.
                                // Doing so will trigger a reset, since _journalStreamDirectory will bind the state machine in question.
                                _nextJournalStreamId = MinApplicationJournalStreamId;
                                foreach (var (name, stateMachine) in _stateMachines)
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
                        else if (workItem.Type is WorkItemType.RegisterStateMachine)
                        {
                            lock (_lock)
                            {
                                if (_state is not ManagerState.Unknown)
                                {
                                    throw new NotSupportedException("Registering a state machine after activation is not supported.");
                                }

                                var name = (string)workItem.Context!;
                                if (!_journalStreamDirectory.ContainsKey(name))
                                {
                                    // Doing so will trigger a reset, since _journalStreamDirectory will bind the state machine in question.
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

    private void RetireOrResurectStateMachines()
    {
        foreach (var (name, timestamp) in _retirementTracker)
        {
            var isDuetime = _timeProvider.GetUtcNow().UtcDateTime - timestamp >= _retirementGracePeriod;
            if (isDuetime && _journalStreamDirectory.TryGetValue(name, out var id))
            {
                var stateMachine = _stateMachines[name];

                Debug.Assert(stateMachine is not null);

                if (stateMachine is RetiredStateMachine)
                {
                    LogRemovingRetiredStateMachine(_logger, name);

                    // Since we are permanently removing this state machine, we will clean it up by reseting it.
                    stateMachine.Reset(CreateJournalStreamWriter(new(id)));

                    _stateMachinesMap.Remove(id);
                    // We remove these from memory only, since the snapshot will persist these changes.
                    _journalStreamDirectory.ApplyRemove(name);
                    _retirementTracker.ApplyRemove(name);
                }
                else
                {
                    LogRetiredStateMachineComebackDetected(_logger, name);
                    // We remove the tracker from memory only, since the snapshot will persist the change.
                    _retirementTracker.ApplyRemove(name);
                }
            }
        }
    }

    private IJournalBatchWriter GetOrCreateCurrentJournalBatchWriter() => _currentJournalBatchWriter ??= _journalFormat.CreateWriter();

    private JournalStreamWriter CreateJournalStreamWriter(JournalStreamId streamId) => new(streamId, this);

    private static void AppendUpdatesOrSnapshotStateMachine(IJournalBatchWriter journalBatchWriter, bool isSnapshot, ulong id, IDurableStateMachine stateMachine)
    {
        var writer = journalBatchWriter.CreateJournalStreamWriter(new(id));
        if (isSnapshot)
        {
            stateMachine.AppendSnapshot(writer);
        }
        else
        {
            stateMachine.AppendEntries(writer);
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
            foreach ((var name, var stateMachine) in _stateMachines)
            {
                stateMachine.OnRecoveryCompleted();

                if (stateMachine is RetiredStateMachine)
                {
                    // We can use TryAdd since recovery has finished.
                    if (_retirementTracker.TryAdd(name, _timeProvider.GetUtcNow().UtcDateTime))
                    {
                        LogRetiredStateMachineDetected(_logger, name);
                    }
                }
            }
        }
    }

    private void ResetForRecovery()
    {
        _currentJournalBatchWriter?.Reset();
        _stateMachinesMap.Clear();
        _stateMachinesMap[StateMachineDirectory.Id] = _journalStreamDirectory;
        _stateMachinesMap[RetiredStateMachineTracker.Id] = _retirementTracker;
        _nextJournalStreamId = MinApplicationJournalStreamId;

        List<string>? retiredNames = null;
        foreach (var (name, stateMachine) in _stateMachines)
        {
            if (stateMachine is RetiredStateMachine)
            {
                (retiredNames ??= []).Add(name);
            }
        }

        if (retiredNames is not null)
        {
            foreach (var name in retiredNames)
            {
                _stateMachines.Remove(name);
            }
        }

        _journalStreamDirectory.ResetVolatileState();
        _retirementTracker.ResetVolatileState();
    }

    IDurableStateMachine IStateMachineResolver.ResolveStateMachine(JournalStreamId streamId)
    {
        if (!_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
        {
            stateMachine = new RetiredStateMachine(streamId);
            _stateMachinesMap[streamId.Value] = stateMachine;
        }

        return stateMachine;
    }

    private void ProcessRecoveryBuffer(JournalReadBuffer buffer)
    {
        try
        {
            _journalFormat.Read(buffer, this);

            if (buffer.IsCompleted && buffer.Length > 0)
            {
                throw new InvalidOperationException("The journal format did not consume the completed journal data.");
            }
        }
        catch (Exception exception) when (ShouldWrapRecoveryFormatException(exception))
        {
            throw CreateRecoveryFormatException(exception);
        }
    }

    private static bool ShouldWrapRecoveryFormatException(Exception exception) =>
        exception is not OperationCanceledException && !IsRecoveryFormatException(exception);

    private static bool IsRecoveryFormatException(Exception exception) =>
        exception is InvalidOperationException { InnerException: not null }
        && exception.Message.StartsWith("Failed to recover journaling state using configured journal format key ", StringComparison.Ordinal);

    private InvalidOperationException CreateRecoveryFormatException(Exception exception) =>
        new(
            $"Failed to recover journaling state using configured journal format key '{_journalFormatKey}'. " +
            "If this grain previously used another journaling format key, restore that key or migrate the data.",
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
                var workItemType = _storage.IsCompactionRequested switch
                {
                    true => WorkItemType.WriteSnapshot,
                    false => WorkItemType.AppendJournal,
                };

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

    private void BindStateMachine(string name, ulong id)
    {
        lock (_lock)
        {
            if (id >= _nextJournalStreamId)
            {
                _nextJournalStreamId = id + 1;
            }

            if (_stateMachines.TryGetValue(name, out var stateMachine))
            {
                _stateMachinesMap[id] = stateMachine;
                stateMachine.Reset(CreateJournalStreamWriter(new(id)));
            }
            else
            {
                var vessel = new RetiredStateMachine(new(id));

                // We must not make the vessel self-register with the manager, since it will
                // result in a late-registration after the manager is 'ready'. Instead we add it inline here.

                _stateMachines.Add(name, vessel);
                _stateMachinesMap[id] = vessel;
            }
        }
    }

    public bool TryGetStateMachine(string name, [NotNullWhen(true)] out IDurableStateMachine? stateMachine) => _stateMachines.TryGetValue(name, out stateMachine);

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
        RegisterStateMachine
    }

    private enum ManagerState
    {
        Unknown,
        Ready
    }

    private sealed class RecoveryJournalStorageConsumer(JournalStateMachineManager manager) : IJournalStorageConsumer
    {
        public void Consume(JournalReadBuffer buffer) => manager.ProcessRecoveryBuffer(buffer);
    }

    private sealed class StateMachineDirectory(
        JournalStateMachineManager manager,
        IDurableDictionaryOperationCodec<string, ulong> codec) : IDurableStateMachine, IDurableDictionaryOperationHandler<string, ulong>
    {
        public const int Id = 0;

        private readonly JournalStateMachineManager _manager = manager;
        private readonly IDurableDictionaryOperationCodec<string, ulong> _codec = codec;
        private readonly Dictionary<string, ulong> _ids = new(StringComparer.Ordinal);
        private JournalStreamWriter _storage;

        public ulong this[string name] => _ids[name];

        object IDurableStateMachine.OperationCodec => _codec;

        public bool ContainsKey(string name) => _ids.ContainsKey(name);

        public bool TryGetValue(string name, out ulong id) => _ids.TryGetValue(name, out id);

        public void Set(string name, ulong id)
        {
            _codec.WriteSet(name, id, GetStorage());
            ApplySet(name, id);
        }

        public bool ApplyRemove(string name) => _ids.Remove(name);

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(_manager.CreateJournalStreamWriter(new(Id)));

        void IDurableStateMachine.Reset(JournalStreamWriter writer)
        {
            _ids.Clear();
            _storage = writer;
        }

        void IDurableStateMachine.AppendEntries(JournalStreamWriter writer) { }

        void IDurableStateMachine.AppendSnapshot(JournalStreamWriter writer) => _codec.WriteSnapshot(_ids, writer);

        IDurableStateMachine IDurableStateMachine.DeepCopy() => throw new NotSupportedException();

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
            _manager.BindStateMachine(name, id);
        }

        private JournalStreamWriter GetStorage()
        {
            Debug.Assert(_storage.IsInitialized);
            return _storage;
        }
    }

    /// <summary>
    /// Used to track state machines that are not registered via user-code anymore, until time-based purging has elapsed.
    /// </summary>
    /// <remarks>Resurrecting of retired machines is supported.</remarks>
    private sealed class RetiredStateMachineTracker(
        JournalStateMachineManager manager, IDurableDictionaryOperationCodec<string, DateTime> codec)
            : DurableDictionary<string, DateTime>(codec)
    {
        public const int Id = 1;

        private readonly JournalStreamWriter _journalWriter = manager.CreateJournalStreamWriter(new(Id));

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(_journalWriter);

        protected override JournalStreamWriter GetStorage() => _journalWriter;
    }

    /// <summary>
    /// Used to keep retired machines into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the journal upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredStateMachine Id = {StreamId.Value}")]
    private sealed class RetiredStateMachine(JournalStreamId streamId) : IDurableStateMachine, IFormattedJournalEntryBuffer
    {
        private static readonly object NoOpCodec = new();
        private readonly List<IFormattedJournalEntry> _formattedEntries = [];

        public JournalStreamId StreamId { get; } = streamId;

        public IReadOnlyList<IFormattedJournalEntry> FormattedEntries => _formattedEntries;

        object IDurableStateMachine.OperationCodec => NoOpCodec;

        public void AddFormattedEntry(IFormattedJournalEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _formattedEntries.Add(entry);
        }

        void IDurableStateMachine.AppendSnapshot(JournalStreamWriter snapshotWriter)
        {
            foreach (var entry in _formattedEntries)
            {
                snapshotWriter.AppendFormattedEntry(entry);
            }
        }

        void IDurableStateMachine.Reset(JournalStreamWriter writer) => _formattedEntries.Clear();
        void IDurableStateMachine.AppendEntries(JournalStreamWriter writer) { }
        IDurableStateMachine IDurableStateMachine.DeepCopy() => throw new NotSupportedException();
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Error processing work items.")]
    private static partial void LogErrorProcessingWorkItems(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "State machine \"{Name}\" was not found. I have substituted a placeholder for graceful time-based retirement.")]
    private static partial void LogRetiredStateMachineDetected(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "State machine \"{Name}\" was previously retired (but not removed), and has hence been re-introduced. " +
                  "There is still time left before its permanent removal, so I will resurrect it.")]
    private static partial void LogRetiredStateMachineComebackDetected(ILogger logger, string name);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Removing retired state machine \"{Name}\" and its data. Operation will be durably persisted shortly after compaction has finalized.")]
    private static partial void LogRemovingRetiredStateMachine(ILogger logger, string name);
}
