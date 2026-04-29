using System.Buffers;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Serialization.Buffers;
using Orleans.Runtime.Internal;

namespace Orleans.Journaling;

internal sealed partial class StateMachineManager : IStateMachineManager, IStateMachineLogDataConsumer, IStateMachineLogEntryConsumer, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const int MinApplicationStateMachineId = 8;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IDurableStateMachine> _stateMachines = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, IDurableStateMachine> _stateMachinesMap = [];
    private readonly IStateMachineStorage _storage;
    private readonly IStateMachineLogFormat _logFormat;
    private readonly ILogger<StateMachineManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateMachineManagerState _stateMachineIds;
    private readonly StateMachinesRetirementTracker _retirementTracker;
    private readonly TimeSpan _retirementGracePeriod;
    private Task? _workLoop;
    private ManagerState _state;
    private Task? _pendingWrite;
    private ulong _nextStateMachineId = MinApplicationStateMachineId;
    private IStateMachineLogExtentWriter? _currentLogExtentWriter;

    public StateMachineManager(
        IStateMachineStorage storage,
        ILogger<StateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider)
    {
        _storage = storage;
        var logFormatKey = StateMachineLogFormatServices.GetValidatedLogFormatKey(storage);
        _logFormat = StateMachineLogFormatServices.GetRequiredKeyedService<IStateMachineLogFormat>(serviceProvider, logFormatKey);
        var dictionaryCodecProvider = StateMachineLogFormatServices.GetRequiredKeyedService<IDurableDictionaryCodecProvider>(serviceProvider, logFormatKey);
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        // The list of known state machines is itself stored as a durable state machine with the implicit id 0.
        // This allows us to recover the list of state machines ids without having to store it separately.
        _stateMachineIds = new StateMachineManagerState(this, dictionaryCodecProvider.GetCodec<string, ulong>());
        _stateMachinesMap[StateMachineManagerState.Id] = _stateMachineIds;

        // The retirement tracker is a special internal state machine with a fixed id.
        // It is not stored in _stateMachineIds and does not participate in the general name->id mapping.
        _retirementTracker = new StateMachinesRetirementTracker(this, dictionaryCodecProvider.GetCodec<string, DateTime>());
        _stateMachinesMap[StateMachinesRetirementTracker.Id] = _retirementTracker;
    }

    internal StateMachineManager(
        IStateMachineStorage storage,
        ILogger<StateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        IDurableDictionaryCodec<string, ulong> stateMachineIdsCodec,
        IDurableDictionaryCodec<string, DateTime> retirementTrackerCodec,
        TimeProvider timeProvider,
        IStateMachineLogFormat? logFormat = null)
    {
        _storage = storage;
        StateMachineLogFormatServices.GetValidatedLogFormatKey(storage);
        _logFormat = logFormat ?? BinaryLogExtentCodec.Instance;
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        _stateMachineIds = new StateMachineManagerState(this, stateMachineIdsCodec);
        _stateMachinesMap[StateMachineManagerState.Id] = _stateMachineIds;

        _retirementTracker = new StateMachinesRetirementTracker(this, retirementTrackerCodec);
        _stateMachinesMap[StateMachinesRetirementTracker.Id] = _retirementTracker;
    }

    public void RegisterStateMachine(string name, IDurableStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        _shutdownCancellation.Token.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_stateMachines.TryGetValue(name, out var machine))
            {
                if (machine is RetiredStateMachineVessel vessel)
                {
                    // If the existing machine is a vessel for a retired one, it means the machine was loaded from a previous
                    // log during recovery but has not been re-registered. We effectively are "staging" the resurrection of the machine.
                    // The removal from the tracker is handled within the serialized loop. This is to prevent logical race conditions with the recovery process.
                    // We also make sure to apply any buffered data that could have occured while the vessel took this machine's place.
                    stateMachine.Reset(new ManagerStateMachineLogWriter(this, new(_stateMachineIds[name])));
                    foreach (var entry in vessel.BufferedData)
                    {
                        stateMachine.Apply(new ReadOnlySequence<byte>(entry));
                    }
                    _stateMachines[name] = stateMachine;
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
        using var cancellationRegistration = cancellationToken.Register(state => ((StateMachineManager)state!)._workSignal.Signal(), this);
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
                        if (workItem.Type is WorkItemType.AppendLog or WorkItemType.WriteSnapshot)
                        {
                            // TODO: decide whether it's best to snapshot or append. Eg, by summing the size of the most recent snapshots and the current log length.
                            //       If the current log length is greater than the snapshot size, then take a snapshot instead of appending more log entries.
                            var isSnapshot = workItem.Type is WorkItemType.WriteSnapshot;
                            IStateMachineLogExtentWriter? logExtentWriter;
                            ArcBuffer committedBuffer = default;

                            lock (_lock)
                            {
                                if (isSnapshot)
                                {
                                    // If there are pending writes, reset them since they will be captured by the snapshot instead.
                                    // If we did not do this, the log would begin with some writes which would be followed by a snapshot which also included those writes.
                                    _currentLogExtentWriter?.Reset();

                                    if (_retirementTracker.Count > 0)
                                    {
                                        RetireOrResurectStateMachines();
                                    }
                                }

                                var currentLogExtentWriter = GetOrCreateCurrentLogExtentWriter();

                                // The map of state machine ids is itself stored as a durable state machine with the id 0.
                                // This must be stored first, since it includes the identities of all other state machines, which are needed when replaying the log.
                                // If we removed retired machines, this snapshot will persist that change.
                                AppendUpdatesOrSnapshotStateMachine(currentLogExtentWriter, isSnapshot, 0, _stateMachineIds);

                                foreach (var (id, stateMachine) in _stateMachinesMap)
                                {
                                    if (id is 0 || stateMachine is null)
                                    {
                                        continue;
                                    }

                                    AppendUpdatesOrSnapshotStateMachine(currentLogExtentWriter, isSnapshot, id, stateMachine);
                                }

                                committedBuffer = currentLogExtentWriter.GetCommittedBuffer();
                                if (committedBuffer.Length == 0)
                                {
                                    committedBuffer.Dispose();
                                    logExtentWriter = null;
                                }
                                else
                                {
                                    logExtentWriter = currentLogExtentWriter;
                                    _currentLogExtentWriter = null;
                                }
                            }

                            if (logExtentWriter is not null)
                            {
                                var writeSucceeded = false;
                                try
                                {
                                    if (isSnapshot)
                                    {
                                        await _storage.ReplaceAsync(committedBuffer, cancellationToken).ConfigureAwait(true);
                                    }
                                    else
                                    {
                                        await _storage.AppendAsync(committedBuffer, cancellationToken).ConfigureAwait(true);
                                    }

                                    writeSucceeded = true;
                                }
                                finally
                                {
                                    committedBuffer.Dispose();
                                    if (!writeSucceeded)
                                    {
                                        logExtentWriter.Dispose();
                                    }
                                }

                                // Notify all state machines that the operation completed.
                                lock (_lock)
                                {
                                    if (_currentLogExtentWriter is null)
                                    {
                                        logExtentWriter.Reset();
                                        _currentLogExtentWriter = logExtentWriter;
                                    }
                                    else
                                    {
                                        logExtentWriter.Dispose();
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
                                _stateMachineIds.ResetVolatileState();

                                // Allocate new state machine ids for each state machine.
                                // Doing so will trigger a reset, since _stateMachineIds will call OnSetStateMachineId, which resets the state machine in question.
                                _nextStateMachineId = 1;
                                foreach (var (name, stateMachine) in _stateMachines)
                                {
                                    var id = _nextStateMachineId++;
                                    _stateMachineIds[name] = id;
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
                                if (!_stateMachineIds.ContainsKey(name))
                                {
                                    // Doing so will trigger a reset, since _stateMachineIds will call OnSetStateMachineId, which resets the state machine in question.
                                    _stateMachineIds[name] = _nextStateMachineId++;
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
            if (isDuetime && _stateMachineIds.TryGetValue(name, out var id))
            {
                var stateMachine = _stateMachines[name];

                Debug.Assert(stateMachine is not null);

                if (stateMachine is RetiredStateMachineVessel)
                {
                    LogRemovingRetiredStateMachine(_logger, name);

                    // Since we are permanently removing this state machine, we will clean it up by reseting it.
                    stateMachine.Reset(new ManagerStateMachineLogWriter(this, new(id)));

                    _stateMachinesMap.Remove(id);
                    // We remove these from memory only, since the snapshot will persist these changes.
                    _stateMachineIds.ApplyRemove(name);
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

    private IStateMachineLogExtentWriter GetOrCreateCurrentLogExtentWriter() => _currentLogExtentWriter ??= _logFormat.CreateWriter();

    private static void AppendUpdatesOrSnapshotStateMachine(IStateMachineLogExtentWriter logExtentWriter, bool isSnapshot, ulong id, IDurableStateMachine stateMachine)
    {
        var writer = logExtentWriter.CreateLogWriter(new(id));
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
            _currentLogExtentWriter?.Reset();
            _stateMachineIds.ResetVolatileState();
        }

        await _storage.ReadAsync(this, cancellationToken).ConfigureAwait(true);

        lock (_lock)
        {
            foreach ((var name, var stateMachine) in _stateMachines)
            {
                stateMachine.OnRecoveryCompleted();

                if (stateMachine is RetiredStateMachineVessel)
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

    void IStateMachineLogEntryConsumer.OnEntry(StateMachineId streamId, ReadOnlySequence<byte> payload)
    {
        if (!_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
        {
            stateMachine = new RetiredStateMachineVessel(streamId);
            _stateMachinesMap[streamId.Value] = stateMachine;
        }

        stateMachine.Apply(payload);
    }

    void IStateMachineLogDataConsumer.OnLogData(ArcBuffer data) => _logFormat.Read(data, this);

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
                    false => WorkItemType.AppendLog,
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

    private void OnSetStateMachineId(string name, ulong id)
    {
        lock (_lock)
        {
            if (id >= _nextStateMachineId)
            {
                _nextStateMachineId = id + 1;
            }

            if (_stateMachines.TryGetValue(name, out var stateMachine))
            {
                _stateMachinesMap[id] = stateMachine;
                stateMachine.Reset(new ManagerStateMachineLogWriter(this, new(id)));
            }
            else
            {
                var vessel = new RetiredStateMachineVessel(new(id));

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
        _currentLogExtentWriter?.Dispose();
    }

    private sealed class ManagerStateMachineLogWriter(StateMachineManager manager, StateMachineId streamId) : IStateMachineLogWriter, ILogEntryWriterCompletion
    {
        private readonly StateMachineManager _manager = manager;
        private readonly StateMachineId _id = streamId;

        public StateMachineLogEntry BeginEntry()
        {
            EnterLock();
            try
            {
                return _manager.GetOrCreateCurrentLogExtentWriter().CreateLogWriter(_id).BeginEntry(this);
            }
            catch
            {
                ExitLock();
                throw;
            }
        }

        void ILogEntryWriterCompletion.CompleteEntryWrite() => ExitLock();

        private void EnterLock()
        {
#if NET9_0_OR_GREATER
            _manager._lock.Enter();
#else
            Monitor.Enter(_manager._lock);
#endif
        }

        private void ExitLock()
        {
#if NET9_0_OR_GREATER
            _manager._lock.Exit();
#else
            Monitor.Exit(_manager._lock);
#endif
        }
    }

    private readonly struct WorkItem(StateMachineManager.WorkItemType type, TaskCompletionSource? completion)
    {
        public WorkItemType Type { get; } = type;
        public TaskCompletionSource? CompletionSource { get; } = completion;
        public object? Context { get; init; }
    }

    private enum WorkItemType
    {
        Initialize,
        AppendLog,
        WriteSnapshot,
        DeleteState,
        RegisterStateMachine
    }

    private enum ManagerState
    {
        Unknown,
        Ready
    }

    private sealed class StateMachineManagerState(
        StateMachineManager manager,
        IDurableDictionaryCodec<string, ulong> codec) : DurableDictionary<string, ulong>(codec)
    {
        public const int Id = 0;

        private readonly StateMachineManager _manager = manager;

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(new ManagerStateMachineLogWriter(_manager, new(Id)));

        protected override void OnSet(string key, ulong value) => _manager.OnSetStateMachineId(key, value);
    }

    /// <summary>
    /// Used to track state machines that are not registered via user-code anymore, until time-based purging has elapsed.
    /// </summary>
    /// <remarks>Resurrecting of retired machines is supported.</remarks>
    private sealed class StateMachinesRetirementTracker(
        StateMachineManager manager, IDurableDictionaryCodec<string, DateTime> codec)
            : DurableDictionary<string, DateTime>(codec)
    {
        public const int Id = 1;

        private readonly ManagerStateMachineLogWriter _logWriter = new(manager, new(Id));

        protected override IStateMachineLogWriter GetStorage() => _logWriter;
    }

    /// <summary>
    /// Used to keep retired machines into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the log upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredStateMachineVessel Id = {StreamId.Value}")]
    private sealed class RetiredStateMachineVessel(StateMachineId streamId) : IDurableStateMachine
    {
        private readonly List<byte[]> _bufferedData = [];

        public StateMachineId StreamId { get; } = streamId;

        public ReadOnlyCollection<byte[]> BufferedData => _bufferedData.AsReadOnly();

        void IDurableStateMachine.AppendSnapshot(StateMachineLogWriter snapshotWriter)
        {
            foreach (var data in _bufferedData)
            {
                snapshotWriter.AppendPreservedDecodedPayload(data);
            }
        }

        void IDurableStateMachine.Reset(IStateMachineLogWriter storage) => _bufferedData.Clear();
        void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
        {
            // Recovery buffers are callback-scoped, so copy the decoded durable payload.
            _bufferedData.Add(logEntry.ToArray());
        }

        void IDurableStateMachine.AppendEntries(StateMachineLogWriter logWriter) { }
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
