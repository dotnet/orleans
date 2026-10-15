using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using System.Threading;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes one attempt of a durable task within a grain activation.
/// </summary>
/// <remarks>
/// The attempt's host execution-abort token is canceled when the activation shuts down or when the task is aborted:
/// <see cref="DurableTaskContext.CancellationToken"/> observes it, while
/// <see cref="DurableTaskContext.IsCancellationRequested"/> reports durable cancellation only. The runtime drains the
/// aborted attempt and discards its response; after shutdown, the next activation replays the task in a new context.
/// </remarks>
internal sealed class GrainDurableTaskContext : DurableTaskContext
{
    // The segment prefix of DurableTask.Once records, which share each occurrence of a name with children.
    private const string EffectSegmentPrefix = "$once:";
    private readonly IDurableTaskGrainRuntime _runtime;
    private readonly TaskScheduler _scheduler;
    private long _utcNowTicks;

    // The attempt's abort source. It is linked to activation shutdown through a registration which the attempt releases
    // when it ends, so a completed attempt retains nothing on the shutdown token.
    private readonly CancellationTokenSource _abortSource;
    private readonly CancellationTokenRegistration _shutdownRegistration;
    private bool _deleted;
    private bool _aborted;

    // The attempt's waits for events which messages deliver. Accessed on the activation scheduler.
    private int _parkedWaits;

    // The number of sends of each signal which this attempt has begun, by marker segment. Guarded by _lock.
    private Dictionary<string, int>? _signalSends;

    // While the attempt replays: the operations which earlier attempts recorded and this attempt hasn't reached yet, and
    // those which it has reached. Both are released when the replay ends. Guarded by _lock; _unreached is read without it
    // to report IsReplaying.
    private HashSet<TaskId>? _unreached;
    private HashSet<TaskId>? _reached;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    public GrainDurableTaskContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        TaskScheduler scheduler,
        CancellationToken shutdownToken,
        DateTimeOffset? utcNow = null)
        : this(taskId, runtime, scheduler, shutdownToken, new CancellationTokenSource(), utcNow)
    {
    }

    private GrainDurableTaskContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        TaskScheduler scheduler,
        CancellationToken shutdownToken,
        CancellationTokenSource abortSource,
        DateTimeOffset? utcNow)
        : base(taskId, abortSource.Token)
    {
        _runtime = runtime;
        _scheduler = scheduler;
        _utcNowTicks = (utcNow ?? runtime.UtcNow).UtcTicks;
        _abortSource = abortSource;
        _shutdownRegistration = shutdownToken.UnsafeRegister(
            static state => ((CancellationTokenSource)state!).Cancel(),
            abortSource);
        _unreached = runtime.GetRecordedOperations(taskId);
    }

    /// <remarks>
    /// The runtime supplies the operations which earlier attempts recorded in this execution's tree when the attempt
    /// starts: its children, including subscriptions to other tasks and records of detached tasks, signal-send markers,
    /// and effect records, but not signal values, which senders record, invoked descendants, or cancellations which
    /// arrived before their requests. The attempt reaches an operation when it starts or posts it, looks up its handle,
    /// signals under its marker, selects a completion under its decision, or runs an effect under its name. Starting an
    /// operation which no earlier attempt recorded ends the replay; reaching an operation again doesn't.
    /// </remarks>
    public override bool IsReplaying => Volatile.Read(ref _unreached) is not null;

    /// <summary>Notes that this attempt reached <paramref name="operationId"/>, which can end its replay.</summary>
    /// <param name="operationId">The operation's identifier.</param>
    /// <param name="recordsOperation">
    /// Whether the operation records itself under a new identifier, as scheduling does, rather than only looking up an
    /// existing one, as a handle lookup or a completion selection which the host derives from a durable order does.
    /// </param>
    private void ObserveOperation(TaskId operationId, bool recordsOperation)
    {
        if (Volatile.Read(ref _unreached) is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_unreached is not { } unreached)
            {
                return;
            }

            if (unreached.Remove(operationId))
            {
                if (unreached.Count > 0)
                {
                    (_reached ??= []).Add(operationId);
                    return;
                }
            }
            else if (!recordsOperation || _reached?.Contains(operationId) is true)
            {
                return;
            }

            Volatile.Write(ref _unreached, null);
            _reached = null;
        }

        _runtime.OnReplayEnded(Id);
    }

    /// <summary>
    /// Gets this execution's logical time: when the task first ran, advanced to the recorded completion time of each
    /// child whose terminal response the execution observes.
    /// </summary>
    /// <remarks>
    /// Child results keep their completion times, so a replay which observes the same children in the same order reads
    /// the same times. A delay therefore starts when the work before it completed.
    /// </remarks>
    public override DateTimeOffset UtcNow => new(Volatile.Read(ref _utcNowTicks), TimeSpan.Zero);

    private void ObserveCompletion(TaskId childId)
    {
        if (_runtime.GetCompletedAt(childId) is { } completedAt)
        {
            ObserveTime(completedAt);
        }
    }

    private void ObserveTime(DateTimeOffset time)
    {
        var ticks = time.UtcTicks;
        for (var current = Volatile.Read(ref _utcNowTicks); ticks > current;)
        {
            var observed = Interlocked.CompareExchange(ref _utcNowTicks, ticks, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    /// <summary>
    /// Marks this attempt's task as deleted with its execution tree, so the attempt can no longer schedule durable work.
    /// </summary>
    /// <remarks>The runtime deletes tasks on the activation scheduler, where this attempt schedules its work.</remarks>
    internal void MarkDeleted() => _deleted = true;

    /// <summary>
    /// Marks this attempt's task as aborted, so the attempt can no longer schedule durable work. The runtime discards the
    /// attempt's result and calls <see cref="SignalAbort"/> once the abort is durable.
    /// </summary>
    internal void MarkAborted() => _aborted = true;

    /// <summary>Cancels the attempt's host execution-abort token, so the attempt stops.</summary>
    internal void SignalAbort()
    {
        Debug.Assert(_aborted, "Only an aborted attempt is signaled.");
        _abortSource.Cancel();
    }

    /// <summary>Releases the attempt's link to activation shutdown once the attempt has ended.</summary>
    internal void OnAttemptCompleted() => _shutdownRegistration.Dispose();

    /// <summary>
    /// Runs a wait for an event which a message delivers, such as a child's result, a signal, a delay's resume, or the
    /// first of several completions, and reports the attempt parked while any such wait is pending.
    /// </summary>
    /// <remarks>
    /// The message which ends the wait reactivates the grain, so a parked attempt needs no wake-up of its own. The
    /// journal write which commits the awaited result also stages the completion of the result's handle, which the
    /// runtime reports as work, so a deactivation after that write still resumes the attempt.
    /// </remarks>
    private async ValueTask<T> ParkAsync<T>(ValueTask<T> wait)
    {
        if (_parkedWaits++ == 0)
        {
            _runtime.OnAttemptParked();
        }

        try
        {
            return await wait;
        }
        finally
        {
            if (--_parkedWaits == 0)
            {
                _runtime.OnAttemptUnparked();
            }
        }
    }

    private void ThrowIfDeleted()
    {
        if (_aborted)
        {
            throw new OperationCanceledException($"Durable task '{Id}' was aborted.", _abortSource.Token);
        }

        if (_deleted)
        {
            throw new OperationCanceledException($"Durable task '{Id}' was deleted with its execution tree.");
        }
    }

    /// <remarks>
    /// Only durable grain calls can be detached: the target recovers a root task from its serialized request. A detached
    /// start is recorded under <c>$detach:{target}:{rootId}</c>, so each detached task which this execution starts has
    /// one record, independently of other operations which a deployment adds or removes.
    /// </remarks>
    protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
        TaskId taskId,
        DurableTask task,
        DurableTaskStartOptions options,
        CancellationToken cancellationToken)
    {
        // The shield flows with the starting code, so it is read before switching schedulers.
        var shielded = SchedulesShieldedChildren;
        var waitForAcceptance = options.HasFlag(DurableTaskStartOptions.WaitForAcceptance);
        if (options.HasFlag(DurableTaskStartOptions.Detached))
        {
            var (recordId, request) = GetDetachedRecord(taskId, task);
            return RunOnScheduler(
                _scheduler,
                () => StartDetachedCoreAsync(recordId, taskId, request, shielded, waitForAcceptance, cancellationToken));
        }

        ObserveOperation(taskId, recordsOperation: true);
        return RunOnScheduler(
            _scheduler,
            () => ScheduleChildTaskCoreAsync(taskId, task, shielded, waitForAcceptance, cancellationToken));
    }

    private async ValueTask<IScheduledTaskHandle> ScheduleChildTaskCoreAsync(
        TaskId taskId,
        DurableTask task,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        ValidateChild(taskId, task);
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var handle = await _runtime.ScheduleChildAsync(taskId, task, shielded, waitForAcceptance, executionCts.Token);
        return new ExecutionTaskHandle(handle, this);
    }

    private async ValueTask<IScheduledTaskHandle> StartDetachedCoreAsync(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var handle = await _runtime.StartDetachedAsync(recordId, rootId, request, shielded, waitForAcceptance, executionCts.Token);
        return new ExecutionTaskHandle(handle, this);
    }

    /// <remarks>
    /// The start and its record are staged in the work item which calls this on the activation, so they commit in the
    /// same journal write as the journaled state which that work item changed. A replay which reaches the record returns
    /// the same handle without starting the task again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller isn't running on the activation's scheduler.</exception>
    protected override IScheduledTaskHandle PostTask(TaskId taskId, DurableTask task, DurableTaskStartOptions options)
    {
        if (TaskScheduler.Current != _scheduler)
        {
            throw new InvalidOperationException(
                $"Post runs on the scheduler of the grain activation which runs durable task '{Id}', so its start commits with the state which the calling code changes. Code which continues elsewhere, for example after ConfigureAwait(false), can't post.");
        }

        var shielded = SchedulesShieldedChildren;
        if (options.HasFlag(DurableTaskStartOptions.Detached))
        {
            var (recordId, request) = GetDetachedRecord(taskId, task);
            ThrowIfDeleted();
            return new ExecutionTaskHandle(_runtime.PostDetached(recordId, taskId, request, shielded), this);
        }

        ObserveOperation(taskId, recordsOperation: true);
        ValidateChild(taskId, task);
        return new ExecutionTaskHandle(_runtime.PostChild(taskId, task, shielded), this);
    }

    /// <summary>Returns the identifier which records a detached start in this execution, and the started request.</summary>
    /// <exception cref="NotSupportedException">The definition isn't a durable grain call.</exception>
    private (TaskId RecordId, IDurableTaskRequest Request) GetDetachedRecord(TaskId rootId, DurableTask task)
    {
        if (task is not IDurableTaskRequest { Context: { } requestContext } request)
        {
            throw new NotSupportedException("Only durable grain calls can be detached.");
        }

        var recordId = Id.Child(CreateMarkerSegment("detach", [requestContext.TargetId.ToString(), rootId.ToString()]));
        ObserveOperation(recordId, recordsOperation: true);
        return (recordId, request);
    }

    /// <summary>
    /// Throws unless <paramref name="taskId"/> can be a child of this execution which runs <paramref name="task"/>: the
    /// execution isn't deleted, and an earlier run recorded no other kind of operation for the child's name.
    /// </summary>
    private void ValidateChild(TaskId taskId, DurableTask task)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        ThrowIfDeleted();
        if (TryGetOtherKindId(taskId, out var effectId))
        {
            _runtime.ThrowIfRecordedAsOtherKind(
                taskId,
                effectId,
                task switch
                {
                    IDurableTaskRequest { Context.TargetId: var target } => $"a call to grain '{target}'",
                    IDurableTaskSubscription subscription => $"a subscription to task '{subscription.Id}' on grain '{subscription.Target}'",
                    _ => "a local child task",
                });
        }
    }

    protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        if (taskId != Id)
        {
            ObserveOperation(taskId, recordsOperation: true);
        }

        return RunOnScheduler(
            _scheduler,
            () => ScheduleDelayCoreAsync(taskId, dueTime, cancellationToken));
    }

    private async ValueTask<DurableTaskResponse> ScheduleDelayCoreAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        if (taskId != Id)
        {
            ThrowIfNotChildTaskId(taskId);
        }

        ThrowIfDeleted();

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await ParkAsync(_runtime.ScheduleDelayAsync(taskId, dueTime, executionCts.Token));
    }

    protected override IScheduledTaskHandle GetChildTaskHandle(TaskId taskId)
    {
        ObserveOperation(taskId, recordsOperation: false);
        return RunOnScheduler(
            _scheduler,
            () => GetChildTaskHandleCore(taskId));
    }

    /// <remarks>
    /// The body's final code runs in the work item which calls this, so staging the response here commits it in the same
    /// journal write as the journaled state that code changed, whichever operation requests that write. A body which
    /// completes on another scheduler, or after its attempt was aborted, has its response handled by the attempt.
    /// </remarks>
    protected override void OnBodyCompleted(DurableTaskResponse response)
    {
        if (TaskScheduler.Current != _scheduler || _aborted || _deleted || _abortSource.IsCancellationRequested)
        {
            return;
        }

        _runtime.StageBodyResponse(this, response);
    }

    /// <remarks>
    /// The effect runs, and its record is staged, in the work item which calls this on the activation, so the record
    /// commits in the same journal write as the journaled state which the effect changed. An effect which an earlier
    /// run recorded as a child, or a child which an earlier run recorded as an effect, under the same name and
    /// occurrence, or under the same generated identifier, fails as a divergence instead of running again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The caller isn't running on the activation's scheduler, or an earlier run recorded another kind of operation under
    /// the effect's name and occurrence.
    /// </exception>
    protected override DurableTaskResponse RunEffect<TState>(
        TaskId effectId,
        TState state,
        Func<TState, DurableTaskResponse> effect)
    {
        if (TaskScheduler.Current != _scheduler)
        {
            throw new InvalidOperationException(
                $"DurableTask.Once runs on the scheduler of the grain activation which runs durable task '{Id}', so its record commits with the state which it changes. Code which continues elsewhere, for example after ConfigureAwait(false), can't record effects.");
        }

        ThrowIfNotChildTaskId(effectId);
        ThrowIfDeleted();
        ObserveOperation(effectId, recordsOperation: true);
        if (TryGetOtherKindId(effectId, out var stepId))
        {
            _runtime.ThrowIfRecordedAsOtherKind(effectId, stepId, "a DurableTask.Once effect");
        }

        return _runtime.RunEffect(effectId, state, effect);
    }

    /// <summary>
    /// Gets the identifier which the other kind of named operation receives for the occurrence of a name which
    /// <paramref name="operationId"/> uses beneath this execution: a <c>DurableTask.Once</c> effect, <c>$once:x$2</c>,
    /// and a child or combinator, <c>x$2</c>, share each occurrence of a name.
    /// </summary>
    /// <returns><see langword="false"/> when the operation's segment beneath this execution is generated.</returns>
    private bool TryGetOtherKindId(TaskId operationId, out TaskId otherId)
    {
        var named = operationId;
        for (var parent = named.Parent(); parent != Id; parent = parent.Parent())
        {
            named = parent;
        }

        var executionPath = Id.ToString();
        var segment = named.ToString().AsSpan(executionPath.Length + 1);
        if (segment.StartsWith(EffectSegmentPrefix, StringComparison.Ordinal))
        {
            otherId = TaskId.Parse($"{executionPath}/{segment[EffectSegmentPrefix.Length..]}");
            return true;
        }

        if (segment[0] != '$')
        {
            otherId = TaskId.Parse($"{executionPath}/{EffectSegmentPrefix}{segment}");
            return true;
        }

        otherId = default;
        return false;
    }

    /// <summary>
    /// Creates the segment of a marker which records an operation of this execution, from the operation's identity:
    /// <c>${kind}:{part}:{part}...</c>, with <c>\</c>, <c>:</c>, and <c>$</c> escaped in each part, so distinct
    /// identities create distinct segments, and a <c>$</c> suffix can number repeated occurrences.
    /// </summary>
    private static string CreateMarkerSegment(string kind, ReadOnlySpan<string> parts)
    {
        var segment = new StringBuilder("$").Append(kind);
        foreach (var part in parts)
        {
            segment.Append(':');
            foreach (var character in part)
            {
                if (character is ':' or '\\' or '$')
                {
                    segment.Append('\\');
                }

                segment.Append(character);
            }
        }

        return segment.ToString();
    }

    private IScheduledTaskHandle GetChildTaskHandleCore(TaskId taskId)
    {
        ThrowIfNotChildTaskId(taskId);
        return new ExecutionTaskHandle(_runtime.GetScheduledTaskHandle(taskId), this);
    }

    /// <remarks>
    /// The wait observes the signals of the invoked task whose execution tree contains this execution, and advances this
    /// execution's logical time to when the signal was recorded.
    /// </remarks>
    protected override ValueTask<DurableTaskResponse> WaitForSignalAsync(string name, CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => WaitForSignalCoreAsync(name, cancellationToken));

    private async ValueTask<DurableTaskResponse> WaitForSignalCoreAsync(string name, CancellationToken cancellationToken)
    {
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        var (value, recordedAt) = await ParkAsync(_runtime.WaitForSignalAsync(Id, name, executionCts.Token));
        ObserveTime(recordedAt);
        return value;
    }

    /// <summary>
    /// Sends a signal to the invoked task <paramref name="taskId"/> on <paramref name="target"/>, recording the send under
    /// <c>$send:{target}:{taskId}:{name}</c> in this execution, so a replay doesn't send it again.
    /// </summary>
    /// <remarks>
    /// The signal's identity names the record, so adding or removing other operations doesn't change the record which a
    /// send reaches. Repeated sends of one signal in an execution, such as a retry after
    /// <see cref="DurableSignalResult.TaskNotFound"/>, are numbered by occurrence, <c>$send:{target}:{taskId}:{name}$2</c>
    /// and so on, so each reaches its own record; the target reports an equal value as
    /// <see cref="DurableSignalResult.Duplicate"/> and a different one as <see cref="DurableSignalResult.Conflict"/>.
    /// Durable cancellation of this execution doesn't abandon the send; host abort does.
    /// </remarks>
    /// <param name="throwOnRejection">Whether a rejection by a task of this grain throws rather than being recorded and returned.</param>
    internal ValueTask<DurableSignalResult> SendSignalAsync(
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        var signal = CreateMarkerSegment("send", [target.ToString(), taskId.ToString(), name]);
        int occurrence;
        lock (_lock)
        {
            _signalSends ??= [];
            _signalSends.TryGetValue(signal, out occurrence);
            _signalSends[signal] = ++occurrence;
        }

        var markerId = Id.Child(occurrence == 1 ? signal : $"{signal}${occurrence.ToString(CultureInfo.InvariantCulture)}");
        ObserveOperation(markerId, recordsOperation: true);
        return RunOnScheduler(
            _scheduler,
            () => SendSignalCoreAsync(markerId, target, taskId, name, value, throwOnRejection, cancellationToken));
    }

    private async ValueTask<DurableSignalResult> SendSignalCoreAsync(
        TaskId markerId,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        ThrowIfDeleted();
        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await _runtime.SendSignalAsync(markerId, target, taskId, name, value, throwOnRejection, executionCts.Token);
    }

    protected override ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ObserveOperation(decisionId, recordsOperation: false);
        return RunOnScheduler(
            _scheduler,
            () => SelectCompletionCoreAsync(decisionId, candidates, cancellationToken));
    }

    private async ValueTask<TaskId> SelectCompletionCoreAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfNotChildTaskId(decisionId);
        foreach (var candidate in candidates)
        {
            ThrowIfNotChildTaskId(candidate);
        }

        ThrowIfDeleted();

        using var executionCts = CreateExecutionCancellationSource(cancellationToken);
        return await ParkAsync(_runtime.SelectCompletionAsync(decisionId, candidates, executionCts.Token));
    }

    private void ThrowIfNotChildTaskId(TaskId taskId)
    {
        if (taskId == Id || !Id.IsAncestorOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a descendant of this task '{Id}'.");
        }
    }

    private CancellationTokenSource CreateExecutionCancellationSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _abortSource.Token);

    private static ValueTask RunOnScheduler(TaskScheduler scheduler, Func<ValueTask> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state => await ((Func<ValueTask>)state!)(),
                callback,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler).Unwrap());
    }

    private static ValueTask<TResult> RunOnScheduler<TResult>(
        TaskScheduler scheduler,
        Func<ValueTask<TResult>> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state => await ((Func<ValueTask<TResult>>)state!)(),
                callback,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach,
                scheduler).Unwrap());
    }

    private static TResult RunOnScheduler<TResult>(
        TaskScheduler scheduler,
        Func<TResult> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            return callback();
        }

        return Task.Factory.StartNew(
            static state => ((Func<TResult>)state!)(),
            callback,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).GetAwaiter().GetResult();
    }

    private sealed class ExecutionTaskHandle(
        IScheduledTaskHandle inner,
        GrainDurableTaskContext owner) : IScheduledTaskHandle
    {
        public TaskId TaskId => inner.TaskId;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

        public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => CancelCoreAsync(reason, cancellationToken));

        private async ValueTask CancelCoreAsync(string? reason, CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            await inner.CancelAsync(reason, executionCts.Token);
        }

        public ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => AbortCoreAsync(reason, cancellationToken));

        private async ValueTask AbortCoreAsync(string reason, CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            await inner.AbortAsync(reason, executionCts.Token);
        }

        public ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => PollCoreAsync(options, cancellationToken));

        private async ValueTask<DurableTaskResponse> PollCoreAsync(
            PollingOptions options,
            CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            return Observe(await inner.PollAsync(options, executionCts.Token));
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => WaitCoreAsync(cancellationToken));

        private async ValueTask<DurableTaskResponse> WaitCoreAsync(CancellationToken cancellationToken)
        {
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                owner._abortSource.Token);
            return Observe(await owner.ParkAsync(inner.WaitAsync(executionCts.Token)));
        }

        private DurableTaskResponse Observe(DurableTaskResponse response)
        {
            if (response.IsCompleted)
            {
                owner.ObserveCompletion(inner.TaskId);
            }

            return response;
        }
    }
}
