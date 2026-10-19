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
using Orleans.Serialization.Invocation;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes one attempt of a durable task within a grain activation.
/// </summary>
/// <remarks>
/// <para>
/// The runtime cancels the attempt when the activation stops, or when the task is aborted or deleted with its execution
/// tree, through <see cref="EndAttempt"/>. From then on, the attempt's durable operations throw
/// <see cref="DurableTaskAttemptCanceledException"/>, and the changes which its code makes to the grain's journaled state,
/// such as cleanup, commit with the grain's next journal write. The runtime discards the canceled attempt's response;
/// after the activation stops, the next activation replays the task in a new context.
/// </para>
/// <para>
/// An attempt which the running code can't replay stalls: the runtime cancels it for
/// <see cref="DurableTaskAttemptCancellationReason.UnsupportedVersion"/> when the execution recorded a version which
/// the code doesn't support, and for <see cref="DurableTaskAttemptCancellationReason.Diverged"/> when an operation
/// diverges from the execution's record, such as another operation under a recorded identifier or a recorded outcome
/// which no longer decodes. The task stays pending until code which can replay it runs.
/// </para>
/// <para>
/// The attempt runs in <em>segments</em>: the code between its durable waits. Each segment is a local request on the
/// grain activation, which follows the grain's reentrancy rules, so on a non-reentrant grain a segment excludes the
/// grain's other requests, as a grain method does. The first segment starts the body. A durable wait which is pending,
/// such as the wait for a child's result, a signal, or the first of several completions, parks the attempt: the
/// segment's request completes. Once the wait completes, a new request resumes the attempt, and the waiting code
/// continues within it. Other awaits, such as journal writes or calls to other grains, stay within the segment.
/// </para>
/// <para>
/// An attempt which is discarded, because the activation stops or the attempt stalls, is never resumed: its next
/// activation replays the execution from its records. Its open segment, if any, observes the cancellation and ends.
/// </para>
/// <para>
/// The attempt's state is confined to the activation's scheduler. Its entry points from the abstractions switch to the
/// scheduler before they read or change that state, because a body can continue elsewhere, such as after
/// <c>ConfigureAwait(false)</c>.
/// </para>
/// </remarks>
internal sealed class GrainDurableTaskContext : DurableTaskContext
{
    private static readonly LocalRequestOptions SegmentOptions = new();

    private readonly IDurableTaskGrainRuntime _runtime;
    private readonly IGrainContext _grainContext;
    private readonly TaskScheduler _scheduler;
    private long _utcNowTicks;

    // Why the runtime ended the attempt, which it cancels in a later work item, or null.
    private DurableTaskAttemptCancellationReason? _endReason;

    // The request of the segment which runs, or null while the attempt is parked or hasn't started; the segment requests
    // which are queued; the attempt's durable waits which are parked, and the tasks and signal records which they await;
    // and whether a parked wait was released and its resume isn't queued yet.
    private TaskCompletionSource? _segment;
    private int _queuedSegments;
    private int _parkedWaits;
    private HashSet<TaskId>? _awaited;
    private bool _resumePending;

    // Completes once the attempt's code has ended or, for a discarded attempt, once no segment of it runs.
    private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The number of sends of each signal which this attempt has begun, by marker segment.
    private Dictionary<string, int>? _signalSends;

    // The operations which earlier attempts recorded and this attempt hasn't reached yet, released once the attempt has
    // reached each of them; and, while the attempt replays, those which it has reached, released when the replay ends.
    // The replay ends earlier than the unreached set empties when the attempt starts an operation which no earlier
    // attempt recorded.
    private HashSet<TaskId>? _unreached;
    private HashSet<TaskId>? _reached;
    private bool _replaying;

    // The versions which this attempt decided, by change name.
    private Dictionary<string, int>? _versions;

    // Why the running code can't replay the execution, when the runtime stalled this attempt: a version which the code
    // doesn't support, or a divergence from the execution's record.
    private Exception? _stallException;

    private Guid? _incarnation;

    public GrainDurableTaskContext(
        TaskId taskId,
        IDurableTaskGrainRuntime runtime,
        IGrainContext grainContext,
        TaskScheduler scheduler,
        DateTimeOffset? utcNow = null)
        : base(taskId)
    {
        _runtime = runtime;
        _grainContext = grainContext;
        _scheduler = scheduler;
        _utcNowTicks = (utcNow ?? runtime.UtcNow).UtcTicks;
        _unreached = runtime.GetRecordedOperations(taskId);
        _replaying = _unreached is not null;
    }

    /// <inheritdoc/>
    /// <remarks>The incarnation of the invoked task whose execution tree contains this execution, read once.</remarks>
    public override Guid Incarnation => _incarnation ??= _runtime.GetIncarnation(Id);

    /// <summary>
    /// Gets why the attempt ended, by the runtime or by a divergence which the abstractions detected, or
    /// <see langword="null"/> while it runs.
    /// </summary>
    internal DurableTaskAttemptCancellationReason? AttemptEndReason => AttemptCancellationReason ?? _endReason;

    /// <summary>Gets whether the attempt ended.</summary>
    internal bool IsAttemptEnded => AttemptEndReason is not null;

    /// <summary>
    /// Gets whether the attempt ended without a record, so it is never resumed: the activation stops, or the running code
    /// can't replay the execution.
    /// </summary>
    internal bool IsDiscarded => AttemptEndReason
        is DurableTaskAttemptCancellationReason.ActivationStopping
        or DurableTaskAttemptCancellationReason.Diverged
        or DurableTaskAttemptCancellationReason.UnsupportedVersion;

    /// <summary>Gets whether a segment of the attempt runs or is queued, so the attempt makes progress without a message.</summary>
    /// <remarks>
    /// A parked wait which the runtime released, by completing what it awaits or canceling its token, resumes in a later
    /// work item. The attempt counts as runnable from the release, so a wake-up which the activation consults meanwhile
    /// still resumes it after a crash.
    /// </remarks>
    internal bool IsRunnable => _segment is not null || _queuedSegments > 0 || _resumePending;

    /// <summary>Notes that the runtime completed <paramref name="awaited"/>, a task or signal record, which can release a parked wait.</summary>
    internal void OnAwaitedCompleted(TaskId awaited)
    {
        AssertOnScheduler();
        if (_awaited?.Contains(awaited) is true)
        {
            _resumePending = true;
        }
    }

    /// <summary>Delivers the task cancellation of this attempt, which releases its parked waits.</summary>
    /// <exception cref="AggregateException">A registration on a token threw.</exception>
    internal void DeliverTaskCancellation(string? reason)
    {
        AssertOnScheduler();
        _resumePending |= _parkedWaits > 0;
        RequestTaskCancellation(reason);
    }

    /// <summary>
    /// Gets a task which completes once the attempt's code has ended or, for a discarded attempt, once no segment of it
    /// runs, so the attempt runs no more code on the activation.
    /// </summary>
    internal Task Settled => _settled.Task;

    /// <summary>Queues the segment which runs the attempt's body, which <paramref name="start"/> releases.</summary>
    internal void Start(TaskCompletionSource start) => QueueSegment(start);

    /// <summary>Queues a segment request which releases <paramref name="gate"/> when the activation admits it.</summary>
    private void QueueSegment(TaskCompletionSource gate)
    {
        AssertOnScheduler();
        _queuedSegments++;
        _resumePending = false;
        _grainContext.ReceiveLocalRequest(new DurableTaskSegment(this, gate), SegmentOptions);
    }

    /// <summary>
    /// Runs a segment which the activation admitted: releases <paramref name="gate"/>, whose waiting code continues
    /// within the request, and completes once the attempt parks or ends.
    /// </summary>
    /// <remarks>
    /// A discarded attempt isn't resumed. A segment which the activation admits while another one of the attempt runs,
    /// as a reentrant grain can, continues within the running one.
    /// </remarks>
    internal ValueTask<Response> RunSegmentAsync(TaskCompletionSource gate)
    {
        AssertOnScheduler();
        _queuedSegments--;
        if (IsDiscarded)
        {
            return new(Response.Completed);
        }

        if (_segment is not null)
        {
            gate.SetResult();
            return new(Response.Completed);
        }

        var segment = _segment = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetResult();
        return AwaitSegmentAsync(segment.Task);

        static async ValueTask<Response> AwaitSegmentAsync(Task segment)
        {
            await segment;
            return Response.Completed;
        }
    }

    /// <summary>Ends the running segment, so the activation admits its other requests.</summary>
    private void EndSegment()
    {
        if (_segment is { } segment)
        {
            _segment = null;
            segment.SetResult();
        }

        if (IsDiscarded)
        {
            _settled.TrySetResult();
        }
    }

    /// <summary>
    /// Waits for <paramref name="wait"/>, a durable wait for an event which a message delivers, parking the attempt while
    /// the wait is pending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pending wait ends the attempt's segment. Once the wait completes, a new segment request resumes the waiting code
    /// within it, unless the attempt was discarded meanwhile.
    /// </para>
    /// <para>
    /// The library's combinators can wait for several events of one attempt at once. When a resumed wait returns while
    /// another one stays parked, its segment ends, so the other wait's resume can run, and code which continues between
    /// the two runs outside a segment. Within one logical flow, which the replay model requires, at most one wait is
    /// parked at a time.
    /// </para>
    /// </remarks>
    internal async ValueTask<T> ParkAsync<T>(Task<T> wait, IReadOnlyCollection<TaskId> awaited)
    {
        await ParkAsync((Task)wait, awaited);
        return await wait;
    }

    /// <inheritdoc cref="ParkAsync{T}(Task{T}, IReadOnlyCollection{TaskId})"/>
    internal async ValueTask ParkAsync(Task wait, IReadOnlyCollection<TaskId> awaited)
    {
        AssertOnScheduler();
        if (!wait.IsCompleted)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _parkedWaits++;
            (_awaited ??= []).UnionWith(awaited);
            _ = wait.ContinueWith(
                static (_, state) =>
                {
                    var (attempt, gate) = ((GrainDurableTaskContext, TaskCompletionSource))state!;
                    attempt.QueueSegment(gate);
                },
                (this, gate),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                _scheduler);
            EndSegment();
            await gate.Task;
            if (--_parkedWaits > 0)
            {
                EndSegment();
            }
            else
            {
                _awaited = null;
            }
        }

        await wait;
    }

    [Conditional("DEBUG")]
    private void AssertOnScheduler() =>
        Debug.Assert(TaskScheduler.Current == _scheduler, "The attempt's state is confined to the activation's scheduler.");

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
    private bool HasUnreachedOperations => _unreached is not null;

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
        AssertOnScheduler();
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

            _unreached = null;
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
    public override DateTimeOffset UtcNow => new(_utcNowTicks, TimeSpan.Zero);

    private void ObserveCompletion(TaskId childId)
    {
        if (_runtime.GetCompletedAt(childId) is { } completedAt)
        {
            ObserveTime(completedAt);
        }
    }

    private void ObserveTime(DateTimeOffset time)
    {
        AssertOnScheduler();
        if (time.UtcTicks > _utcNowTicks)
        {
            _utcNowTicks = time.UtcTicks;
        }
    }

    /// <summary>
    /// Ends this attempt for <paramref name="reason"/> at once: from now on, its durable operations throw
    /// <see cref="DurableTaskAttemptCanceledException"/>, and a discarded attempt isn't resumed.
    /// <see cref="ApplyCancellation"/> then cancels the attempt's tokens.
    /// </summary>
    /// <returns><see langword="true"/> when this call ended the attempt.</returns>
    /// <remarks>
    /// The runtime ends attempts on the activation's scheduler, where the attempt's code runs, so a synchronous run of that
    /// code observes one state of the attempt. Canceling the attempt's tokens runs their registrations and can continue the
    /// attempt's code synchronously, so the runtime applies it outside its staging operations.
    /// </remarks>
    internal bool EndAttempt(DurableTaskAttemptCancellationReason reason)
    {
        AssertOnScheduler();
        if (AttemptEndReason is not null)
        {
            return false;
        }

        _endReason = reason;
        if (IsDiscarded && _segment is null)
        {
            _settled.TrySetResult();
        }

        return true;
    }

    /// <summary>Cancels the tokens of an attempt which <see cref="EndAttempt"/> ended.</summary>
    /// <exception cref="AggregateException">A registration on a token threw.</exception>
    internal void ApplyCancellation()
    {
        AssertOnScheduler();
        Debug.Assert(_endReason is not null, "Only an ended attempt is canceled.");
        _resumePending |= _parkedWaits > 0 && !IsDiscarded;
        CancelAttempt(_endReason.GetValueOrDefault());
    }

    /// <summary>Ends the attempt's segment and settles the attempt once its code has ended.</summary>
    internal void OnAttemptCompleted()
    {
        EndSegment();
        _settled.TrySetResult();
    }

    /// <summary>
    /// Stalls this attempt for <paramref name="reason"/>: the runtime cancels it and keeps its task pending, so code which
    /// can replay the execution runs it later.
    /// </summary>
    /// <returns>The exception which the stalled operation throws.</returns>
    private DurableTaskAttemptCanceledException Stall(DurableTaskAttemptCancellationReason reason, Exception cause)
    {
        _stallException ??= cause;
        _runtime.StallAttempt(this, reason, cause);
        return CreateAttemptEndedException();
    }

    /// <summary>Stalls this attempt because an operation diverged from the execution's record.</summary>
    private DurableTaskAttemptCanceledException Diverge(DurableTaskDivergenceException divergence) =>
        Stall(DurableTaskAttemptCancellationReason.Diverged, divergence);

    /// <summary>Throws <see cref="DurableTaskAttemptCanceledException"/> when the runtime canceled this attempt.</summary>
    private void ThrowIfAttemptCanceled()
    {
        if (IsAttemptEnded)
        {
            throw CreateAttemptEndedException();
        }
    }

    private DurableTaskAttemptCanceledException CreateAttemptEndedException(Exception? innerException = null)
    {
        Debug.Assert(AttemptEndReason is not null, "Only an ended attempt throws its cancellation.");
        return new(AttemptEndReason.GetValueOrDefault(), AttemptCancellationToken, innerException ?? _stallException);
    }

    /// <summary>
    /// Completes an operation of this attempt with its outcome, or with the attempt's cancellation once the runtime
    /// canceled the attempt. An operation which diverged from the execution's record stalls the attempt.
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
        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptEnded)
        {
            throw CreateAttemptEndedException(exception);
        }
        catch (DurableTaskDivergenceException divergence)
        {
            throw Diverge(divergence);
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
        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException && IsAttemptEnded)
        {
            throw CreateAttemptEndedException(exception);
        }
        catch (DurableTaskDivergenceException divergence)
        {
            throw Diverge(divergence);
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
            return RunOnScheduler(
                _scheduler,
                () => StartDetachedCoreAsync(taskId, task, shielded, waitForAcceptance, cancellationToken));
        }

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
        ObserveOperation(taskId, recordsOperation: true);
        try
        {
            ValidateChild(taskId, task);
        }
        catch (DurableTaskDivergenceException divergence)
        {
            throw Diverge(divergence);
        }

        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        var handle = await CompleteOperationAsync(_runtime.ScheduleChildAsync(taskId, task, shielded, waitForAcceptance, operationCts.Token));
        return CreateExecutionHandle(handle);
    }

    private async ValueTask<IScheduledTaskHandle> StartDetachedCoreAsync(
        TaskId rootId,
        DurableTask task,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        var (recordId, request) = GetDetachedRecord(rootId, task);
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
        try
        {
            if (options.HasFlag(DurableTaskStartOptions.Detached))
            {
                var (recordId, request) = GetDetachedRecord(taskId, task);
                return CreateExecutionHandle(_runtime.PostDetached(recordId, taskId, request, shielded));
            }

            ObserveOperation(taskId, recordsOperation: true);
            ValidateChild(taskId, task);
            return CreateExecutionHandle(_runtime.PostChild(taskId, task, shielded));
        }
        catch (DurableTaskDivergenceException divergence)
        {
            throw Diverge(divergence);
        }
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
    /// execution isn't deleted, and no effect which an earlier run recorded holds the operation beneath which the child
    /// runs, such as a named combinator whose input the child is.
    /// </summary>
    /// <remarks>The runtime checks the record which a direct child finds under its own identifier when it starts.</remarks>
    private void ValidateChild(TaskId taskId, DurableTask task)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        ThrowIfNotChildTaskId(taskId);
        ThrowIfAttemptCanceled();
        var operationId = taskId;
        for (var parent = operationId.Parent(); parent != Id; parent = parent.Parent())
        {
            operationId = parent;
        }

        if (operationId != taskId)
        {
            _runtime.ThrowIfRecordedAsEffect(
                taskId,
                operationId,
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
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => ScheduleDelayCoreAsync(taskId, dueTime, cancellationToken));

    private async ValueTask<DurableTaskResponse> ScheduleDelayCoreAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        if (taskId != Id)
        {
            ObserveOperation(taskId, recordsOperation: true);
            ThrowIfNotChildTaskId(taskId);
        }

        ThrowIfAttemptCanceled();

        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(_runtime.ScheduleDelayAsync(taskId, dueTime, operationCts.Token));
    }

    /// <remarks>
    /// The body's final code runs in the work item which calls this, so staging the response here commits it in the same
    /// journal write as the journaled state that code changed, whichever operation requests that write. A body which
    /// completes on another scheduler has its response handled by the attempt, and a canceled attempt's response is
    /// discarded.
    /// </remarks>
    protected override void OnBodyCompleted(DurableTaskResponse response)
    {
        if (TaskScheduler.Current != _scheduler || IsAttemptEnded)
        {
            return;
        }

        _runtime.StageBodyResponse(this, response);
    }

    /// <remarks>
    /// The effect runs, and its record is staged, in the work item which calls this on the activation, so the record
    /// commits in the same journal write as the journaled state which the effect changed. Effects and children share
    /// identifiers, so an effect where an earlier run recorded a child, or a child where an earlier run recorded an
    /// effect, diverges: the attempt stalls with <see cref="DurableTaskAttemptCancellationReason.Diverged"/>, whose
    /// divergence names both kinds, instead of running the effect again.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller isn't running on the activation's scheduler.</exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The attempt was canceled, or stalled because an earlier run recorded another kind of operation under the effect's
    /// identifier.
    /// </exception>
    protected override DurableTaskResponse RunEffect<TState>(
        TaskId effectId,
        TState state,
        Func<TState, DurableTaskResponse> effect)
    {
        if (TaskScheduler.Current != _scheduler)
        {
            throw new InvalidOperationException(
                $"DurableTask.RunInline runs on the scheduler of the grain activation which runs durable task '{Id}', so its record commits with the state which it changes. Code which continues elsewhere, for example after ConfigureAwait(false), can't record effects.");
        }

        ThrowIfNotChildTaskId(effectId);
        ThrowIfAttemptCanceled();
        ObserveOperation(effectId, recordsOperation: true);
        try
        {
            return _runtime.RunEffect(effectId, state, effect);
        }
        catch (DurableTaskDivergenceException divergence)
        {
            throw Diverge(divergence);
        }
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
        if (_versions is null || !_versions.TryGetValue(name, out var version))
        {
            version = -1;
        }

        if (version < 0)
        {
            try
            {
                version = _runtime.GetOrRecordVersion(Id, name, HasUnreachedOperations ? 0 : maxSupported);
            }
            catch (DurableTaskDivergenceException divergence)
            {
                throw Diverge(divergence);
            }

            (_versions ??= [])[name] = version;
        }

        if (version < minSupported || version > maxSupported)
        {
            // The runtime cancels the attempt and keeps its task pending, so code which supports the version replays it.
            throw Stall(
                DurableTaskAttemptCancellationReason.UnsupportedVersion,
                new DurableTaskVersionException(Id, name, version, minSupported, maxSupported));
        }

        return version;
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
        var (value, recordedAt) = await CompleteOperationAsync(_runtime.WaitForSignalAsync(this, Id, name, operationCts.Token));
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
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => SendSignalCoreAsync(target, taskId, name, value, throwOnRejection, cancellationToken));

    private async ValueTask<DurableSignalResult> SendSignalCoreAsync(
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken)
    {
        var signal = CreateMarkerSegment("send", [target.ToString(), taskId.ToString(), name]);
        _signalSends ??= [];
        _signalSends.TryGetValue(signal, out var occurrence);
        _signalSends[signal] = ++occurrence;
        var markerId = Id.Child(occurrence == 1 ? signal : $"{signal}${occurrence.ToString(CultureInfo.InvariantCulture)}");
        ObserveOperation(markerId, recordsOperation: true);
        ThrowIfAttemptCanceled();
        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(_runtime.SendSignalAsync(markerId, target, taskId, name, value, throwOnRejection, operationCts.Token));
    }

    protected override ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) =>
        RunOnScheduler(
            _scheduler,
            () => SelectCompletionCoreAsync(decisionId, candidates, cancellationToken));

    private async ValueTask<TaskId> SelectCompletionCoreAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ObserveOperation(decisionId, recordsOperation: false);
        ThrowIfNotChildTaskId(decisionId);
        foreach (var candidate in candidates)
        {
            ThrowIfNotChildTaskId(candidate);
        }

        ThrowIfAttemptCanceled();

        using var operationCts = CreateOperationCancellationSource(cancellationToken);
        return await CompleteOperationAsync(_runtime.SelectCompletionAsync(this, decisionId, candidates, operationCts.Token));
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

    /// <summary>Runs an entry point's <paramref name="callback"/>, which the activation's scheduler runs.</summary>
    /// <exception cref="InvalidOperationException">The caller isn't running on the activation's scheduler.</exception>
    private static ValueTask RunOnScheduler(TaskScheduler scheduler, Func<ValueTask> callback)
    {
        ThrowIfOffScheduler(scheduler);
        return callback();
    }

    /// <inheritdoc cref="RunOnScheduler(TaskScheduler, Func{ValueTask})"/>
    private static ValueTask<TResult> RunOnScheduler<TResult>(TaskScheduler scheduler, Func<ValueTask<TResult>> callback)
    {
        ThrowIfOffScheduler(scheduler);
        return callback();
    }

    /// <summary>
    /// Throws unless the caller runs on <paramref name="scheduler"/>, the activation's, which owns the attempt's state.
    /// </summary>
    /// <remarks>
    /// A body continues on the activation's scheduler at every await, unless it uses <c>ConfigureAwait(false)</c>, which
    /// ORLEANS0226 reports.
    /// </remarks>
    private static void ThrowIfOffScheduler(TaskScheduler scheduler)
    {
        if (TaskScheduler.Current != scheduler)
        {
            throw new InvalidOperationException(
                "A durable task's operations run on the scheduler of the grain activation which runs it. Code which continues elsewhere, for example after ConfigureAwait(false), can't run them.");
        }
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
            return Observe(await owner.CompleteOperationAsync(owner.ParkAsync(inner.WaitAsync(operationCts.Token).AsTask(), [inner.TaskId])));
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
