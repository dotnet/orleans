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

internal sealed partial class LogStateMachineManager : IStateMachineManager, IStateMachineResolver, ILogStreamWriterTarget, ILogEntryWriterCompletion, ILifecycleParticipant<IGrainLifecycle>, ILifecycleObserver, IDisposable
{
    private const int MinApplicationLogStreamId = 8;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif
    private readonly Dictionary<string, IDurableStateMachine> _stateMachines = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, IDurableStateMachine> _stateMachinesMap = [];
    private readonly ILogStorage _storage;
    private readonly ILogFormat _logFormat;
    private readonly string _logFormatKey;
    private readonly ILogger<LogStateMachineManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SingleWaiterAutoResetEvent _workSignal = new() { RunContinuationsAsynchronously = true };
    private readonly Queue<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly StateMachineDirectory _logStreamDirectory;
    private readonly RetiredStateMachineTracker _retirementTracker;
    private readonly TimeSpan _retirementGracePeriod;
    private Task? _workLoop;
    private ManagerState _state;
    private Task? _pendingWrite;
    private ulong _nextLogStreamId = MinApplicationLogStreamId;
    private ILogBatchWriter? _currentLogBatchWriter;

    public LogStateMachineManager(
        ILogStorage storage,
        ILogger<LogStateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        TimeProvider timeProvider,
        IServiceProvider serviceProvider,
        [FromKeyedServices(LogFormatServices.LogFormatKeyServiceKey)] string logFormatKey)
    {
        _storage = storage;
        _logFormatKey = LogFormatServices.ValidateLogFormatKey(logFormatKey);
        _logFormat = LogFormatServices.GetRequiredKeyedService<ILogFormat>(serviceProvider, _logFormatKey);
        var dictionaryCodecProvider = LogFormatServices.GetRequiredKeyedService<IDurableDictionaryOperationCodecProvider>(serviceProvider, _logFormatKey);
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        // The list of known state machines is itself stored as a durable state machine with the implicit id 0.
        // This allows us to recover the list of state machines ids without having to store it separately.
        _logStreamDirectory = new StateMachineDirectory(this, dictionaryCodecProvider.GetCodec<string, ulong>());
        _stateMachinesMap[StateMachineDirectory.Id] = _logStreamDirectory;

        // The retirement tracker is a special internal state machine with a fixed id.
        // It is not stored in _logStreamDirectory and does not participate in the general name->id mapping.
        _retirementTracker = new RetiredStateMachineTracker(this, dictionaryCodecProvider.GetCodec<string, DateTime>());
        _stateMachinesMap[RetiredStateMachineTracker.Id] = _retirementTracker;
    }

    internal LogStateMachineManager(
        ILogStorage storage,
        ILogger<LogStateMachineManager> logger,
        IOptions<StateMachineManagerOptions> options,
        IDurableDictionaryOperationCodec<string, ulong> logStreamIdsCodec,
        IDurableDictionaryOperationCodec<string, DateTime> retirementTrackerCodec,
        TimeProvider timeProvider,
        ILogFormat? logFormat = null,
        string? logFormatKey = null)
    {
        _storage = storage;
        _logFormatKey = LogFormatServices.ValidateLogFormatKey(logFormatKey ?? OrleansBinaryLogFormat.LogFormatKey);
        _logFormat = logFormat ?? OrleansBinaryLogFormat.Instance;
        _logger = logger;
        _timeProvider = timeProvider;
        _retirementGracePeriod = options.Value.RetirementGracePeriod;

        _logStreamDirectory = new StateMachineDirectory(this, logStreamIdsCodec);
        _stateMachinesMap[StateMachineDirectory.Id] = _logStreamDirectory;

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
                    // log during recovery but has not been re-registered. We effectively are "staging" the resurrection of the machine.
                    // The removal from the tracker is handled within the serialized loop. This is to prevent logical race conditions with the recovery process.
                    // We also make sure to apply any buffered data that could have occured while the vessel took this machine's place.
                    stateMachine.Reset(CreateLogStreamWriter(new(_logStreamDirectory[name])));
                    foreach (var entry in vessel.FormattedEntries)
                    {
                        stateMachine.Apply(new ReadOnlySequence<byte>(entry.Payload));
                    }

                    var id = _logStreamDirectory[name];
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
        using var cancellationRegistration = cancellationToken.Register(state => ((LogStateMachineManager)state!)._workSignal.Signal(), this);
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
                            ILogBatchWriter? logBatchWriter;
                            ArcBuffer committedBuffer = default;

                            lock (_lock)
                            {
                                if (isSnapshot)
                                {
                                    // If there are pending writes, reset them since they will be captured by the snapshot instead.
                                    // If we did not do this, the log would begin with some writes which would be followed by a snapshot which also included those writes.
                                    _currentLogBatchWriter?.Reset();

                                    if (_retirementTracker.Count > 0)
                                    {
                                        RetireOrResurectStateMachines();
                                    }
                                }

                                var currentLogBatchWriter = GetOrCreateCurrentLogBatchWriter();

                                // The map of state machine ids is itself stored as a durable state machine with the id 0.
                                // This must be stored first, since it includes the identities of all other state machines, which are needed when replaying the log.
                                // If we removed retired machines, this snapshot will persist that change.
                                AppendUpdatesOrSnapshotStateMachine(currentLogBatchWriter, isSnapshot, StateMachineDirectory.Id, _logStreamDirectory);

                                foreach (var (id, stateMachine) in _stateMachinesMap)
                                {
                                    if (id is 0 || stateMachine is null)
                                    {
                                        continue;
                                    }

                                    AppendUpdatesOrSnapshotStateMachine(currentLogBatchWriter, isSnapshot, id, stateMachine);
                                }

                                committedBuffer = currentLogBatchWriter.GetCommittedBuffer();
                                if (committedBuffer.Length == 0)
                                {
                                    committedBuffer.Dispose();
                                    logBatchWriter = null;
                                }
                                else
                                {
                                    logBatchWriter = currentLogBatchWriter;
                                    _currentLogBatchWriter = null;
                                }
                            }

                            if (logBatchWriter is not null)
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
                                        logBatchWriter.Dispose();
                                    }
                                }

                                // Notify all state machines that the operation completed.
                                lock (_lock)
                                {
                                    if (_currentLogBatchWriter is null)
                                    {
                                        logBatchWriter.Reset();
                                        _currentLogBatchWriter = logBatchWriter;
                                    }
                                    else
                                    {
                                        logBatchWriter.Dispose();
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
                                _logStreamDirectory.ResetVolatileState();

                                // Allocate new state machine ids for each state machine.
                                // Doing so will trigger a reset, since _logStreamDirectory will bind the state machine in question.
                                _nextLogStreamId = MinApplicationLogStreamId;
                                foreach (var (name, stateMachine) in _stateMachines)
                                {
                                    var id = _nextLogStreamId++;
                                    _logStreamDirectory.Set(name, id);
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
                                if (!_logStreamDirectory.ContainsKey(name))
                                {
                                    // Doing so will trigger a reset, since _logStreamDirectory will bind the state machine in question.
                                    _logStreamDirectory.Set(name, _nextLogStreamId++);
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
            if (isDuetime && _logStreamDirectory.TryGetValue(name, out var id))
            {
                var stateMachine = _stateMachines[name];

                Debug.Assert(stateMachine is not null);

                if (stateMachine is RetiredStateMachine)
                {
                    LogRemovingRetiredStateMachine(_logger, name);

                    // Since we are permanently removing this state machine, we will clean it up by reseting it.
                    stateMachine.Reset(CreateLogStreamWriter(new(id)));

                    _stateMachinesMap.Remove(id);
                    // We remove these from memory only, since the snapshot will persist these changes.
                    _logStreamDirectory.ApplyRemove(name);
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

    private ILogBatchWriter GetOrCreateCurrentLogBatchWriter() => _currentLogBatchWriter ??= _logFormat.CreateWriter();

    private LogStreamWriter CreateLogStreamWriter(LogStreamId streamId) => new(streamId, this);

    private static void AppendUpdatesOrSnapshotStateMachine(ILogBatchWriter logBatchWriter, bool isSnapshot, ulong id, IDurableStateMachine stateMachine)
    {
        var writer = logBatchWriter.CreateLogStreamWriter(new(id));
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

        var recoveryConsumer = new RecoveryLogStorageConsumer(this);
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
        _currentLogBatchWriter?.Reset();
        _stateMachinesMap.Clear();
        _stateMachinesMap[StateMachineDirectory.Id] = _logStreamDirectory;
        _stateMachinesMap[RetiredStateMachineTracker.Id] = _retirementTracker;
        _nextLogStreamId = MinApplicationLogStreamId;

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

        _logStreamDirectory.ResetVolatileState();
        _retirementTracker.ResetVolatileState();
    }

    IDurableStateMachine IStateMachineResolver.ResolveStateMachine(LogStreamId streamId)
    {
        if (!_stateMachinesMap.TryGetValue(streamId.Value, out var stateMachine))
        {
            stateMachine = new RetiredStateMachine(streamId);
            _stateMachinesMap[streamId.Value] = stateMachine;
        }

        return stateMachine;
    }

    private void ProcessRecoveryBuffer(ArcBufferReader reader, bool isCompleted)
    {
        try
        {
            while (_logFormat.TryRead(reader, this, isCompleted))
            {
            }

            if (isCompleted && reader.Length > 0)
            {
                throw new InvalidOperationException("The log format did not consume the completed log data.");
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
        && exception.Message.StartsWith("Failed to recover journaling state using configured log format key ", StringComparison.Ordinal);

    private InvalidOperationException CreateRecoveryFormatException(Exception exception) =>
        new(
            $"Failed to recover journaling state using configured log format key '{_logFormatKey}'. " +
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

    private void BindStateMachine(string name, ulong id)
    {
        lock (_lock)
        {
            if (id >= _nextLogStreamId)
            {
                _nextLogStreamId = id + 1;
            }

            if (_stateMachines.TryGetValue(name, out var stateMachine))
            {
                _stateMachinesMap[id] = stateMachine;
                stateMachine.Reset(CreateLogStreamWriter(new(id)));
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
        _currentLogBatchWriter?.Dispose();
    }

    LogEntryWriter ILogStreamWriterTarget.BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion)
    {
        if (completion is not null)
        {
            throw new InvalidOperationException("Manager-backed log writers do not support external completion callbacks.");
        }

        EnterLock();
        try
        {
            return GetOrCreateCurrentLogBatchWriter().CreateLogStreamWriter(streamId).BeginEntryWriter(this);
        }
        catch
        {
            ExitLock();
            throw;
        }
    }

    void ILogStreamWriterTarget.AppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
    {
        EnterLock();
        try
        {
            GetOrCreateCurrentLogBatchWriter().CreateLogStreamWriter(streamId).AppendFormattedEntry(entry);
        }
        finally
        {
            ExitLock();
        }
    }

    bool ILogStreamWriterTarget.TryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
    {
        EnterLock();
        try
        {
            return GetOrCreateCurrentLogBatchWriter().CreateLogStreamWriter(streamId).TryAppendFormattedEntry(entry);
        }
        finally
        {
            ExitLock();
        }
    }

    void ILogEntryWriterCompletion.CompleteEntryWrite() => ExitLock();

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

    private sealed class RecoveryLogStorageConsumer(LogStateMachineManager manager) : ILogStorageConsumer
    {
        public void Consume(LogReadBuffer buffer) => manager.ProcessRecoveryBuffer(buffer.Reader, buffer.IsCompleted);
    }

    private sealed class StateMachineDirectory(
        LogStateMachineManager manager,
        IDurableDictionaryOperationCodec<string, ulong> codec) : IDurableStateMachine, IDurableDictionaryOperationHandler<string, ulong>
    {
        public const int Id = 0;

        private readonly LogStateMachineManager _manager = manager;
        private readonly IDurableDictionaryOperationCodec<string, ulong> _codec = codec;
        private readonly Dictionary<string, ulong> _ids = new(StringComparer.Ordinal);
        private LogStreamWriter _storage;

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

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(_manager.CreateLogStreamWriter(new(Id)));

        void IDurableStateMachine.Reset(LogStreamWriter writer)
        {
            _ids.Clear();
            _storage = writer;
        }

        void IDurableStateMachine.Apply(ReadOnlySequence<byte> entry) => _codec.Apply(entry, this);

        void IDurableStateMachine.AppendEntries(LogStreamWriter writer) { }

        void IDurableStateMachine.AppendSnapshot(LogStreamWriter writer) => _codec.WriteSnapshot(_ids, writer);

        IDurableStateMachine IDurableStateMachine.DeepCopy() => throw new NotSupportedException();

        void IDurableDictionaryOperationHandler<string, ulong>.ApplySet(string key, ulong value) => ApplySet(key, value);

        void IDurableDictionaryOperationHandler<string, ulong>.ApplyRemove(string key) => ApplyRemove(key);

        void IDurableDictionaryOperationHandler<string, ulong>.ApplyClear() => _ids.Clear();

        void IDurableDictionaryOperationHandler<string, ulong>.ApplySnapshotStart(int count)
        {
            _ids.Clear();
            _ids.EnsureCapacity(count);
        }

        void IDurableDictionaryOperationHandler<string, ulong>.ApplySnapshotItem(string key, ulong value) => ApplySet(key, value);

        private void ApplySet(string name, ulong id)
        {
            _ids[name] = id;
            _manager.BindStateMachine(name, id);
        }

        private LogStreamWriter GetStorage()
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
        LogStateMachineManager manager, IDurableDictionaryOperationCodec<string, DateTime> codec)
            : DurableDictionary<string, DateTime>(codec)
    {
        public const int Id = 1;

        private readonly LogStreamWriter _logWriter = manager.CreateLogStreamWriter(new(Id));

        public void ResetVolatileState() => ((IDurableStateMachine)this).Reset(_logWriter);

        protected override LogStreamWriter GetStorage() => _logWriter;
    }

    /// <summary>
    /// Used to keep retired machines into a purgatory state until time-based purging or if a comeback occurs.
    /// This keeps buffering entries and dumps them back into the log upon compaction.
    /// </summary>
    [DebuggerDisplay("RetiredStateMachine Id = {StreamId.Value}")]
    private sealed class RetiredStateMachine(LogStreamId streamId) : IDurableStateMachine, IFormattedLogEntryBuffer
    {
        private static readonly object NoOpCodec = new();
        private readonly List<IFormattedLogEntry> _formattedEntries = [];

        public LogStreamId StreamId { get; } = streamId;

        public IReadOnlyList<IFormattedLogEntry> FormattedEntries => _formattedEntries;

        object IDurableStateMachine.OperationCodec => NoOpCodec;

        public void AddFormattedEntry(IFormattedLogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            _formattedEntries.Add(entry);
        }

        void IDurableStateMachine.AppendSnapshot(LogStreamWriter snapshotWriter)
        {
            foreach (var entry in _formattedEntries)
            {
                snapshotWriter.AppendFormattedEntry(entry);
            }
        }

        void IDurableStateMachine.Reset(LogStreamWriter writer) => _formattedEntries.Clear();
        void IDurableStateMachine.Apply(ReadOnlySequence<byte> logEntry)
        {
            throw new InvalidOperationException(
                "Retired state machines can only buffer formatted entries supplied by the active log format.");
        }

        void IDurableStateMachine.AppendEntries(LogStreamWriter writer) { }
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
