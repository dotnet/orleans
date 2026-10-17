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
using Orleans.Journaling;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes one attempt of a durable task within a grain activation.
/// </summary>
/// <remarks>
/// The runtime cancels the attempt when the activation stops, or when the task is aborted or deleted with its execution
/// tree, through <see cref="CancelAttempt"/>. From then on, the attempt's durable operations throw
/// <see cref="DurableTaskAttemptCanceledException"/>, and so do the changes which its code makes to the grain's journaled
/// state, through <see cref="MutationFence"/>. The runtime drains the canceled attempt and discards its response; after
/// the activation stops, the next activation replays the task in a new context.
/// </remarks>
internal sealed class GrainDurableTaskContext : DurableTaskContext
{
    // The segment prefix of DurableTask.Once records, which share each occurrence of a name with children.
    private const string EffectSegmentPrefix = "$once:";
    private readonly IDurableTaskGrainRuntime _runtime;
    private readonly TaskScheduler _scheduler;
    private long _utcNowTicks;

    // Closed when the runtime cancels the attempt, so the journaled state changes which the attempt's code makes throw.
    private readonly JournaledStateMutationFence _mutationFence;

    // Why the runtime canceled the attempt, or null. Set on the activation's scheduler. Guarded by _lock.
    private DurableTaskAttemptCancellationReason? _attemptCancellationReason;

    // The attempt's waits for events which messages deliver: those which are pending, and those which resumed but whose
    // waiting code hasn't continued yet. The attempt is parked while a wait is pending and none has resumed. Guarded by
    // _lock.
    private int _pendingWaits;
    private int _resumedWaits;
    private bool _parked;
    private bool _ended;

    // The number of sends of each signal which this attempt has begun, by marker segment. Guarded by _lock.
    private Dictionary<string, int>? _signalSends;

    // The operations which earlier attempts recorded and this attempt hasn't reached yet, released once the attempt has
    // reached each of them; and, while the attempt replays, those which it has reached, released when the replay ends.
    // The replay ends earlier than the unreached set empties when the attempt starts an operation which no earlier
    // attempt recorded. Guarded by _lock; _unreached and _replaying are read without it.
    private HashSet<TaskId>? _unreached;
    private HashSet<TaskId>? _reached;
    private volatile bool _replaying;

    // The versions which this attempt decided, by change name. Guarded by _lock.
    private Dictionary<string, int>? _versions;

    // The version which the runtime canceled this attempt for, when its execution recorded one which the running code
    // doesn't support. Guarded by _lock.
    private DurableTaskVersionException? _unsupportedVersion;
#if NET9_0_OR_GREATER
    private readonly Lock _lock = new();
#else
    private readonly object _lock = new();
#endif

    public GrainDurableTaskContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        TaskScheduler scheduler,
        DateTimeOffset? utcNow = null)
        : base(taskId)
    {
        _runtime = runtime;
        _scheduler = scheduler;
        _utcNowTicks = (utcNow ?? runtime.UtcNow).UtcTicks;
        _mutationFence = new(static state => ((GrainDurableTaskContext)state!).CreateAttemptCanceledException(), this);
        _unreached = runtime.GetRecordedOperations(taskId);
        _replaying = _unreached is not null;
    }

    /// <summary>
    /// Gets the fence which the runtime enters around the attempt's code, and which closes when the runtime cancels the
    /// attempt.
    /// </summary>
    internal JournaledStateMutationFence MutationFence => _mutationFence;

    /// <summary>Gets whether the runtime canceled this attempt.</summary>
    internal bool IsAttemptCanceled
    {
        get
        {
            lock (_lock)
            {
                return _attemptCancellationReason is not null;
            }
        }
    }

    /// <remarks>
    /// The runtime supplies the operations which earlier attempts recorded in this execution's tree when the attempt
    /// starts: its children, including subscriptions to other tasks and records of detached tasks, signal-send markers,
    /// and effect records, but not signal values, which senders record, invoked descendants, or cancellations which
    /// arrived before their requests. The attempt reaches an operation when it starts or posts it, looks up its handle,
    /// signals under its marker, selects a completion under its decision, or runs an effect under its name. Starting an
    /// operation which no earlier attempt recorded ends the replay; reaching an operation again doesn't.
    /// </remarks>
    public override bool IsReplaying => _replaying;

    /// <summary>
    /// Gets whether earlier attempts recorded operations which this attempt hasn't reached yet, so an earlier run went
    /// past the attempt's current point.
    /// </summary>
    /// <remarks>
    /// An execution's body runs its durable operations in one logical flow, and concurrent work runs in child executions,
    /// so a deterministic replay reaches the recorded operations in the order in which the earlier run created them.
    /// </remarks>
    private bool HasUnreachedOperations => Volatile.Read(ref _unreached) is not null;

    /// <summary>
    /// Gets the grain which runs this execution, which qualifies the root identifiers of the detached tasks which the
    /// execution starts without a name, so executions with the same identifier on different grains derive different ones.
    /// </summary>
    protected override string HostId => _runtime.GrainId.ToString();

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
                    if (_replaying)
                    {
                        (_reached ??= []).Add(operationId);
                    }

                    return;
                }

                Volatile.Write(ref _unreached, null);
            }
            else if (!_replaying || !recordsOperation || _reached?.Contains(operationId) is true)
            {
                return;
            }

            _reached = null;
            if (!_replaying)
            {
                return;
            }

            _replaying = false;
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
    /// Cancels this attempt for <paramref name="reason"/>: from now on, its durable operations and the journaled state
    /// changes which its code makes throw <see cref="DurableTaskAttemptCanceledException"/>.
    /// </summary>
    /// <returns><see langword="true"/> when this call canceled the attempt, which the runtime then signals.</returns>
    /// <remarks>
    /// The runtime cancels attempts on the activation's scheduler, where the attempt's code runs, so a synchronous run of
    /// that code observes one state of the attempt. The attempt's tokens are canceled by <see cref="SignalAttemptCanceled"/>,
    /// which the runtime calls in a later work item, so code which observes them doesn't run within the runtime's operation.
    /// </remarks>
    internal bool CancelAttempt(DurableTaskAttemptCancellationReason reason)
    {
        Debug.Assert(TaskScheduler.Current == _scheduler, "The runtime cancels attempts on the activation's scheduler.");
        lock (_lock)
        {
            if (_attemptCancellationReason is not null)
            {
                return false;
            }

            _attemptCancellationReason = reason;
        }

        _mutationFence.Close();
        return true;
    }

    /// <summary>
    /// Cancels <see cref="DurableTaskContext.AttemptCancellationToken"/> and
    /// <see cref="DurableTaskContext.CancellationToken"/> of an attempt which <see cref="CancelAttempt"/> canceled.
    /// </summary>
    /// <exception cref="AggregateException">A registration on a token threw.</exception>
    internal void SignalAttemptCanceled()
    {
        DurableTaskAttemptCancellationReason reason;
        lock (_lock)
        {
            Debug.Assert(_attemptCancellationReason is not null, "Only a canceled attempt is signaled.");
            reason = _attemptCancellationReason.GetValueOrDefault();
        }

        DurableTaskRuntimeHelper.CancelAttempt(this, reason);
    }

    /// <summary>
    /// Releases the attempt's parked state once the attempt has ended, which a wait that the attempt left pending would
    /// otherwise keep.
    /// </summary>
    internal void OnAttemptCompleted()
    {
        lock (_lock)
        {
            _ended = true;
            if (_parked)
            {
                _parked = false;
                _runtime.OnAttemptUnparked();
            }
        }
    }

    /// <summary>
    /// Runs a wait for an event which a message delivers, such as a child's result, a signal, or the first of several
    /// completions, and reports the attempt parked while the wait is pending.
    /// </summary>
    /// <param name="wait">The wait.</param>
    /// <param name="cancellationToken">The wait's token, whose cancellation resumes it.</param>
    /// <param name="awaited">The tasks or signal records whose completion resumes the wait.</param>
    /// <remarks>
    /// The message which ends the wait reactivates the grain, so a parked attempt needs no wake-up of its own. The wait
    /// resumes synchronously when the runtime completes what it awaits, or when its token is canceled, before the waiting
    /// code continues in a later work item, so the attempt counts as runnable from that moment.
    /// </remarks>
    private async ValueTask<T> ParkAsync<T>(ValueTask<T> wait, CancellationToken cancellationToken, IReadOnlyList<TaskId> awaited)
    {
        if (wait.IsCompleted)
        {
            return await wait;
        }

        var parked = new ParkedWait(this);
        lock (_lock)
        {
            _pendingWaits++;
            UpdateParked();
        }

        foreach (var id in awaited)
        {
            _runtime.RegisterParkedWait(id, parked);
        }

        try
        {
            using var registration = cancellationToken.UnsafeRegister(static state => ((ParkedWait)state!).Resume(), parked);
            return await wait;
        }
        finally
        {
            foreach (var id in awaited)
            {
                _runtime.UnregisterParkedWait(id, parked);
            }

            parked.End();
        }
    }

    /// <summary>Notes that a pending wait resumed, so the attempt runs again once its code continues.</summary>
    internal void OnWaitResumed()
    {
        lock (_lock)
        {
            _pendingWaits--;
            _resumedWaits++;
            UpdateParked();
        }
    }

    /// <summary>Notes that a wait ended, after it resumed or without resuming.</summary>
    internal void OnWaitEnded(bool resumed)
    {
        lock (_lock)
        {
            if (resumed)
            {
                _resumedWaits--;
            }
            else
            {
                _pendingWaits--;
            }

            UpdateParked();
        }
    }

    private void UpdateParked()
    {
        var parked = !_ended && _pendingWaits > 0 && _resumedWaits == 0;
        if (parked == _parked)
        {
            return;
        }

        _parked = parked;
        if (parked)
        {
            _runtime.OnAttemptParked();
        }
        else
        {
            _runtime.OnAttemptUnparked();
        }
    }

    /// <summary>Throws <see cref="DurableTaskAttemptCanceledException"/> when the runtime canceled this attempt.</summary>
    private void ThrowIfAttemptCanceled()
    {
        if (IsAttemptCanceled)
        {
            throw CreateAttemptCanceledException();
        }
    }

    private DurableTaskAttemptCanceledException CreateAttemptCanceledException(Exception? innerException = null)
    {
        DurableTaskAttemptCancellationReason? reason;
        lock (_lock)
        {
            reason = _attemptCancellationReason;
            innerException ??= _unsupportedVersion;
        }

        Debug.Assert(reason is not null, "Only a canceled attempt throws its cancellation.");
        return new(reason.GetValueOrDefault(), AttemptCancellationToken, innerException);
    }

    /// <summary>
    /// Completes an operation of this attempt with its outcome, or with the attempt's cancellation once the runtime
    /// canceled the attempt.
    /// </summary>
    /// <remarks>
    /// The runtime cancels an attempt before it signals the attempt's tokens, so an operation which completes in between,
    /// such as the wait for a child whose abort committed, ends with the attempt's cancellation rather than its outcome.
    /// </remarks>
    private async ValueTask<T> CompleteOperationAsync<T>(ValueTask<T> operation)
    {
        T result;
        try
        {
            result = await operation;
        }
        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptCanceled)
        {
            throw CreateAttemptCanceledException(exception);
        }

        ThrowIfAttemptCanceled();
        return result;
    }

    /// <inheritdoc cref="CompleteOperationAsync{T}(ValueTask{T})"/>
    private async ValueTask CompleteOperationAsync(ValueTask operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptCanceled)
        {
            throw CreateAttemptCanceledException(exception);
        }

        ThrowIfAttemptCanceled();
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
        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        var handle = await CompleteOperationAsync(_runtime.ScheduleChildAsync(taskId, task, shielded, waitForAcceptance, operationCts.Token));
        return CreateExecutionHandle(handle);
    }

    private async ValueTask<IScheduledTaskHandle> StartDetachedCoreAsync(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        ThrowIfAttemptCanceled();
        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        var handle = await CompleteOperationAsync(_runtime.StartDetachedAsync(recordId, rootId, request, shielded, waitForAcceptance, operationCts.Token));
        return CreateExecutionHandle(handle);
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

        ThrowIfAttemptCanceled();
        var shielded = SchedulesShieldedChildren;
        using var _ = JournaledStateMutationFence.Suspend();
        if (options.HasFlag(DurableTaskStartOptions.Detached))
        {
            var (recordId, request) = GetDetachedRecord(taskId, task);
            return CreateExecutionHandle(_runtime.PostDetached(recordId, taskId, request, shielded));
        }

        ObserveOperation(taskId, recordsOperation: true);
        ValidateChild(taskId, task);
        return CreateExecutionHandle(_runtime.PostChild(taskId, task, shielded));
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
        ThrowIfAttemptCanceled();
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

        ThrowIfAttemptCanceled();

        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(_runtime.ScheduleDelayAsync(taskId, dueTime, operationCts.Token));
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
    /// completes on another scheduler has its response handled by the attempt, and a canceled attempt's response is
    /// discarded.
    /// </remarks>
    protected override void OnBodyCompleted(DurableTaskResponse response)
    {
        if (TaskScheduler.Current != _scheduler || IsAttemptCanceled)
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
        ThrowIfAttemptCanceled();
        ObserveOperation(effectId, recordsOperation: true);
        if (TryGetOtherKindId(effectId, out var stepId))
        {
            _runtime.ThrowIfRecordedAsOtherKind(effectId, stepId, "a DurableTask.Once effect");
        }

        return _runtime.RunEffect(effectId, state, effect);
    }

    /// <remarks>
    /// The first call for a name in an attempt decides the version and memoizes it, so loops and helpers which check the
    /// same change read the same version. A decision which no record holds yet is <c>0</c> while earlier attempts'
    /// operations remain unreached, rather than while <see cref="IsReplaying"/> reports a replay: an operation which a
    /// deployment inserted earlier in the body ends the replay without changing how far the earlier run went. The record
    /// is staged in the work item which calls this on the activation and doesn't end the replay.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller isn't running on the activation's scheduler.</exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The runtime canceled the attempt, for example because the execution's version lies outside the supported range,
    /// whose <see cref="DurableTaskVersionException"/> is then the inner exception.
    /// </exception>
    protected override int GetVersion(string name, int minSupported, int maxSupported)
    {
        if (TaskScheduler.Current != _scheduler)
        {
            throw new InvalidOperationException(
                $"DurableTask.GetVersion runs on the scheduler of the grain activation which runs durable task '{Id}', so its record commits with the execution's other records. Code which continues elsewhere, for example after ConfigureAwait(false), can't read versions.");
        }

        ThrowIfAttemptCanceled();
        int version;
        lock (_lock)
        {
            if (_versions is null || !_versions.TryGetValue(name, out version))
            {
                version = -1;
            }
        }

        if (version < 0)
        {
            version = _runtime.GetOrRecordVersion(Id, name, HasUnreachedOperations ? 0 : maxSupported);
            lock (_lock)
            {
                _versions ??= [];
                _versions[name] = version;
            }
        }

        if (version < minSupported || version > maxSupported)
        {
            // The runtime cancels the attempt and keeps its task pending, so code which supports the version replays it.
            var unsupported = new DurableTaskVersionException(Id, name, version, minSupported, maxSupported);
            lock (_lock)
            {
                _unsupportedVersion ??= unsupported;
            }

            _runtime.StallAttempt(this, unsupported);
            throw CreateAttemptCanceledException();
        }

        return version;
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
        return CreateExecutionHandle(_runtime.GetScheduledTaskHandle(taskId));
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
        ThrowIfAttemptCanceled();
        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        var recordId = _runtime.GetSignalRecordId(Id, name);
        var (value, recordedAt) = await CompleteOperationAsync(ParkAsync(
            _runtime.WaitForSignalAsync(Id, name, operationCts.Token),
            operationCts.Token,
            recordId is { } id ? [id] : []));
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
    /// Task cancellation of this execution doesn't abandon the send; the cancellation of the attempt does.
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
        ThrowIfAttemptCanceled();
        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(_runtime.SendSignalAsync(markerId, target, taskId, name, value, throwOnRejection, operationCts.Token));
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

        ThrowIfAttemptCanceled();

        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(ParkAsync(_runtime.SelectCompletionAsync(decisionId, candidates, operationCts.Token), operationCts.Token, candidates));
    }

    private void ThrowIfNotChildTaskId(TaskId taskId)
    {
        if (taskId == Id || !Id.IsAncestorOf(taskId))
        {
            throw new InvalidOperationException($"The provided task ID '{taskId}' is not a descendant of this task '{Id}'.");
        }
    }

    /// <summary>Links an operation's token with the attempt's cancellation, which ends every operation of the attempt.</summary>
    private CancellationTokenSource CreateOperationCancellationSource(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            AttemptCancellationToken);

    // The runtime's work on behalf of the attempt, such as recording a child's start, runs outside the attempt's mutation
    // fence: the attempt's operations check its cancellation before they reach the runtime.
    private static ValueTask RunOnScheduler(TaskScheduler scheduler, Func<ValueTask> callback)
    {
        if (TaskScheduler.Current == scheduler)
        {
            using var _ = JournaledStateMutationFence.Suspend();
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state =>
                {
                    using var _ = JournaledStateMutationFence.Suspend();
                    await ((Func<ValueTask>)state!)();
                },
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
            using var _ = JournaledStateMutationFence.Suspend();
            return callback();
        }

        return new(
            Task.Factory.StartNew(
                static async state =>
                {
                    using var _ = JournaledStateMutationFence.Suspend();
                    return await ((Func<ValueTask<TResult>>)state!)();
                },
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
            using var _ = JournaledStateMutationFence.Suspend();
            return callback();
        }

        return Task.Factory.StartNew(
            static state =>
            {
                using var _ = JournaledStateMutationFence.Suspend();
                return ((Func<TResult>)state!)();
            },
            callback,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            scheduler).GetAwaiter().GetResult();
    }

    /// <summary>Wraps a handle of this execution's operation, which the activation's scheduler runs.</summary>
    private ExecutionTaskHandle CreateExecutionHandle(IScheduledTaskHandle handle)
    {
        var targetTaskId = _runtime.GetTargetTaskId(handle.TaskId, out var addressedHost);
        return new(handle, this, targetTaskId, addressedHost);
    }

    private sealed class ExecutionTaskHandle(
        IScheduledTaskHandle inner,
        GrainDurableTaskContext owner,
        TaskId targetTaskId,
        GrainId addressedHost) : IScheduledTaskHandle
    {
        public TaskId TaskId => inner.TaskId;

        public TaskId TargetTaskId { get; } = targetTaskId;

        /// <summary>
        /// Returns the <see cref="DurableTaskAddress"/> of a detached or subscribed task, as
        /// <see cref="IDurableTaskReference"/>.
        /// </summary>
        public TFeature? GetFeature<TFeature>()
            where TFeature : class =>
            addressedHost.IsDefault ? null : new DurableTaskAddress(addressedHost, TargetTaskId) as TFeature;

        public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

        public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => CancelCoreAsync(reason, cancellationToken));

        private async ValueTask CancelCoreAsync(string? reason, CancellationToken cancellationToken)
        {
            owner.ThrowIfAttemptCanceled();
            using var operationCts = owner.CreateOperationCancellationSource(cancellationToken);
            await owner.CompleteOperationAsync(inner.CancelAsync(reason, operationCts.Token));
        }

        public ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => AbortCoreAsync(reason, cancellationToken));

        private async ValueTask AbortCoreAsync(string reason, CancellationToken cancellationToken)
        {
            owner.ThrowIfAttemptCanceled();
            using var operationCts = owner.CreateOperationCancellationSource(cancellationToken);
            await owner.CompleteOperationAsync(inner.AbortAsync(reason, operationCts.Token));
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
            owner.ThrowIfAttemptCanceled();
            using var operationCts = owner.CreateOperationCancellationSource(cancellationToken);
            return Observe(await owner.CompleteOperationAsync(inner.PollAsync(options, operationCts.Token)));
        }

        public ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            RunOnScheduler(
                owner._scheduler,
                () => WaitCoreAsync(cancellationToken));

        private async ValueTask<DurableTaskResponse> WaitCoreAsync(CancellationToken cancellationToken)
        {
            owner.ThrowIfAttemptCanceled();
            using var operationCts = owner.CreateOperationCancellationSource(cancellationToken);
            return Observe(await owner.CompleteOperationAsync(owner.ParkAsync(inner.WaitAsync(operationCts.Token), operationCts.Token, [inner.TaskId])));
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
