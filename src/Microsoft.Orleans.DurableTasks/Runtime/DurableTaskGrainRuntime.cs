using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Protocol;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Journaling;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Executes durable tasks for one grain activation.
/// </summary>
/// <remarks>
/// <para>
/// Task state, outgoing messages, and logical times share the grain's journal. Every operation validates and prepares
/// its changes first, then stages only changes which are safe to commit. Staged changes are shared by all callers and
/// commit with the next successful journal write; a failed storage write fences the journal and deactivates the grain.
/// </para>
/// <para>
/// The runtime is also a journal state machine which writes no entries. Capture marks the effects which depend on
/// the captured changes, and write completion runs them: starting committed requests, completing handles with
/// committed responses, and propagating committed cancellation.
/// </para>
/// <para>
/// A delay stages a resume message to its own grain together with its delay state, scheduled with
/// <see cref="Orleans.Messaging.Envelope.DeliverAt"/> for the delay's due time. The grain's own inbox accepts the message
/// in the same journal write as the delay, so the resume is durable whenever the delay is, and the inbox processes it
/// once it is due. The adapter schedules no Durable Jobs of its own: as the inbox's work source, it reports its runnable
/// executions, for which Messaging's wake-up reactivates the grain.
/// </para>
/// <para>
/// The runtime is the <see cref="IOutboxMessageSource"/> of its completion messages: the outbox journals a reference to
/// each completion, and the runtime materializes the message from the task's stored result, so the result is journaled
/// once while the completion is pending. A task's completion destinations are its subscribers: the invoker, under the
/// task's own identifier, and the durable executions' children which subscribed to the existing task under their own
/// identifiers. A subscriber on this grain receives the result in the write which stores it. A dead-lettered
/// completion's dead letter holds a copy of the result, and the
/// task keeps its own for the retention period, so polls and re-invocations still observe it. A stored result which no
/// longer decodes fails only the operations which read it: the outbox dead-letters its completion, whose retirement
/// doesn't decode the result, and activation never decodes stored results.
/// </para>
/// <para>
/// An invoked task, which was scheduled as a request, owns an execution tree: the task and its descendants which are
/// not invoked tasks themselves, such as local children, delays, completion decisions, records of detached tasks,
/// effect records, subscriptions to other tasks, and remote-child stubs. Task identifiers are chosen by callers, so an
/// invoked descendant keeps its own lifecycle.
/// Once an invoked task is terminal and no completion is pending, the runtime deletes its execution tree: in the write
/// which retires its last completion as delivered, because that destination durably holds the result, or otherwise once
/// <see cref="Orleans.Configuration.DurableTaskOptions.ResultRetentionPeriod"/> has elapsed since completion or since a
/// completion was dead-lettered. A task without a stored ancestor, such as a remote child scheduled outside a durable
/// execution, is deleted on the same retention schedule. Deleting a tree cancels its non-terminal tasks: it stages a
/// cancellation to each pending remote child and cancels each running attempt, whose result is then discarded.
/// Completions and resumes which arrive for a deleted task are ignored. A cancellation or a signal which arrives before its
/// invocation is recorded, and the invocation finds it in its tree. The grain admits an invocation until
/// <see cref="Orleans.Messaging.Configuration.DurableMessagingOptions.DeduplicationWindow"/> has elapsed since its sender
/// first staged it, and dead-letters a later one, so a record whose invocation doesn't arrive within that window is
/// deleted.
/// A subscription to a task which the grain doesn't retain as an invoked task completes as not found at once.
/// </para>
/// <para>
/// Task cancellation is cooperative: a request is recorded in task state, cancels the
/// <see cref="DurableTaskContext.CancellationToken"/> of running attempts, and terminalizes only leaves, such as delays
/// and tasks whose bodies haven't started. A started body decides its task's outcome, and recovery replays it with the
/// request applied. An abort commits a task's execution tree as failed in one write and cancels the tree's running
/// attempts, whose results are discarded.
/// </para>
/// <para>
/// Attempt cancellation stops an attempt: deactivation, an abort, and the deletion of a tree cancel attempts. A canceled
/// attempt's durable operations throw <see cref="DurableTaskAttemptCanceledException"/>, its journaled state changes,
/// such as cleanup, commit with the grain's next journal write, and its tokens are canceled in a later work item. An
/// aborted or deleted attempt drains: its parked waits resume with the cancellation. An attempt which deactivation or a
/// stall discards is never resumed, and deactivation waits only for its running segment before the next activation
/// replays its task.
/// </para>
/// <para>
/// Each attempt runs in segments, local requests on the activation; see <see cref="GrainDurableTaskContext"/>. The
/// runtime's state is confined to the activation's scheduler, as grain state is, so it uses no locks.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    [FromKeyedServices("$task-logical-times")] IDurableDictionary<TaskId, DateTimeOffset> logicalTimes,
    [FromKeyedServices("$task-invocations")] IDurableDictionary<MessageId, PendingInvocation> invocations,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IDurableTaskManager,
    IStateMachine,
    IOutboxMessageSource,
    IInboxWorkSource
{
    /// <summary>The journal state machine name of the runtime's commit hook.</summary>
    internal const string StateMachineName = "orleans.durable-tasks.runtime";

    // The runtime's state is confined to the activation's scheduler, as grain state is.
    private readonly Dictionary<TaskId, GrainDurableTaskContext> _executionContexts = [];
    private readonly Dictionary<TaskId, RunningRequest> _runningRequests = [];
    private readonly Dictionary<TaskId, TaskHandle> _taskHandles = [];
    private readonly HashSet<TaskId> _pendingStarts = [];
    private readonly Dictionary<TaskId, DurableTaskResponse> _pendingHandleResponses = [];
    private readonly Dictionary<long, CancellationPropagation> _pendingCancellationPropagations = [];
    private readonly HashSet<(TaskId TaskId, GrainId Target)> _preStagedCancellations = [];
    private readonly Dictionary<long, Task> _backgroundTasks = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;
    private readonly IDurableDictionary<TaskId, DateTimeOffset> _logicalTimes = logicalTimes;
    private readonly IDurableDictionary<MessageId, PendingInvocation> _invocations = invocations;
    private readonly IDurableTaskMessageTransport? _messageTransport = messageTransports.SingleOrDefault();
    private readonly CancellationTokenSource _deactivationCts = new();

    // The executions whose attempts stalled because the running code can't replay them, and which stay pending. The grain
    // wakes every DurableTaskOptions.StalledExecutionRetryInterval while it holds one, so a later activation replays it.
    private readonly HashSet<TaskId> _stalledExecutions = [];
    private TaskId[] _committingStarts = [];
    private KeyValuePair<TaskId, DurableTaskResponse>[] _committingHandleResponses = [];
    private long[] _committingCancellationPropagations = [];

    // The committed effects which wait for the task cancellation of attempts which an earlier or the same write committed,
    // in commit order, or null when none waits; see RunCommittedEffects.
    private List<CommittedEffects>? _orderedCommittedEffects;

    // The executions which recovery found and which start once the activation has completed.
    private List<TaskId>? _recoveredStarts;
    private bool _recovered;
    private Task? _stopTask;
    private long _backgroundWriteId;
    private long _cancellationPropagationId;
    private IGrainContext? _grainContext;
    private bool _stopping;

    // The completions pending in the outbox, by message ID. The outbox resolves recovered completions before the runtime
    // observes recovery, so the index is built from task state on first use, then maintained as completions are sent and
    // retire.
    private Dictionary<MessageId, PendingCompletion>? _pendingCompletions;

    // The earliest time at which a retained result or a cancellation record can expire. It is unknown after activation,
    // so the first check scans every task; afterwards every change which starts a retention period or records a
    // cancellation lowers it, and each scan recomputes it from the remaining tasks.
    private DateTimeOffset _nextExpiry = DateTimeOffset.MinValue;

    public GrainId GrainId => GrainContext.GrainId;

    /// <summary>Gets the activation which the runtime is bound to.</summary>
    /// <remarks>
    /// The binding is explicit, so code which runs off the activation's scheduler, where the ambient grain context is the
    /// hosted client's, still reads this activation's identity.
    /// </remarks>
    internal IGrainContext GrainContext => _grainContext ?? throw CreateUnboundException();

    public DateTimeOffset UtcNow => _shared.TimeProvider.GetUtcNow();

    private IDurableTaskMessageTransport MessageTransport => _messageTransport ?? throw new InvalidOperationException(
        "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");

    /// <summary>Binds the runtime to <paramref name="grainContext"/>, the activation whose executions it runs.</summary>
    internal void InitializeForActivation(IGrainContext grainContext) => _grainContext = grainContext;

    /// <summary>
    /// Recovers committed task state during the activation's lifecycle, deferring the recovered executions to
    /// <see cref="StartRecoveredExecutions"/>.
    /// </summary>
    internal Task RecoverAsync(CancellationToken cancellationToken)
    {
        _recovered = true;
        return ResumePendingTasksCoreAsync(cancellationToken);
    }

    /// <summary>Starts the executions which <see cref="RecoverAsync"/> recovered, once.</summary>
    /// <remarks>
    /// Runs after the grain's <c>OnActivateAsync</c> has completed, so recovered grain code observes an initialized
    /// grain, as grain calls do. A call which reaches a recovered task first starts it, and this skips it.
    /// </remarks>
    internal void StartRecoveredExecutions()
    {
        if (_recoveredStarts is not { } starts)
        {
            return;
        }

        _recoveredStarts = null;

        foreach (var taskId in starts)
        {
            StartRequest(taskId);
        }
    }

    /// <summary>
    /// Returns the candidate which completed first: the stored candidate with the lowest completion sequence, which needs
    /// no record, or a winner recorded under <paramref name="decisionId"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storage stamps each result with a sequence when it stages the result, and a later result receives a higher
    /// sequence. A candidate whose result is staged commits in the same write as, or an earlier write than, every result
    /// with a higher sequence, so when a candidate's handle completes, every candidate with a lower sequence is stored.
    /// The lowest stored sequence is therefore the same in a live attempt, which selects among the results staged so far,
    /// and in every replay, in which more candidates can have completed.
    /// </para>
    /// <para>
    /// A candidate can complete without a sequence: a child of a canceled ancestor which isn't stored, or a result stored
    /// before sequences were stamped. When such a completion is observed first, or a
    /// recorded decision already exists, the selection is recorded under <paramref name="decisionId"/> and replays from
    /// that record.
    /// </para>
    /// </remarks>
    public async ValueTask<TaskId> SelectCompletionAsync(
        GrainDurableTaskContext attempt,
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        if (_storage.TryGetTask(decisionId, out var decision))
        {
            return await SelectRecordedCompletionAsync(attempt, decisionId, decision, candidates, cancellationToken);
        }

        for (var observedCompletion = false; ; observedCompletion = true)
        {
            var (first, unordered) = FindFirstStoredCompletion(candidates);
            if (!unordered && first >= 0)
            {
                return candidates[first];
            }

            // A handle which completed without a stored, sequenced result has no durable order.
            if (unordered || observedCompletion)
            {
                return await SelectRecordedCompletionAsync(attempt, decisionId, decision: null, candidates, cancellationToken);
            }

            DurableTaskEvents.EmitSelectionWaitStarted(GrainContext, decisionId);
            await WaitForAnyAsync(attempt, candidates, cancellationToken);
            ThrowIfStopping();
        }
    }

    /// <summary>
    /// Returns the index of the stored candidate with the lowest completion sequence, or -1, and whether a candidate's
    /// stored result has no sequence.
    /// </summary>
    /// <remarks>Reads stored completion metadata only, without decoding results.</remarks>
    private (int Index, bool Unordered) FindFirstStoredCompletion(IReadOnlyList<TaskId> candidates)
    {
        var first = -1;
        var firstSequence = long.MaxValue;
        for (var index = 0; index < candidates.Count; index++)
        {
            if (!_storage.TryGetTask(candidates[index], out var state) || !HasStoredResult(state))
            {
                continue;
            }

            var sequence = state.CompletionSequence;
            if (sequence <= 0)
            {
                return (-1, true);
            }

            if (sequence < firstSequence)
            {
                (first, firstSequence) = (index, sequence);
            }
        }

        return (first, false);
    }

    /// <summary>
    /// Returns the winner recorded under <paramref name="decisionId"/>, or records the first candidate in candidate order
    /// whose handle has completed.
    /// </summary>
    private async ValueTask<TaskId> SelectRecordedCompletionAsync(
        GrainDurableTaskContext attempt,
        TaskId decisionId,
        IDurableTaskState? decision,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        var fingerprint = GetCompletionDecisionFingerprint(candidates);
        if (decision is not null)
        {
            if (decision.Request is not null
                || !decision.RemoteTarget.IsDefault
                || decision.RemoteRequestFingerprint is not null
                || !decision.CallerId.IsDefault
                || decision.DueTime is not null
                || (decision.RequestFingerprint is { } existingFingerprint
                    && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
                || (decision.RequestFingerprint is null
                    && TryGetScheduledTaskHandle(decisionId, out _)))
            {
                throw CreateDivergenceException(
                    $"Durable completion decision '{decisionId}' is already associated with another operation.",
                    decision,
                    DescribeSelection(candidates.Count));
            }

            if (decision.RequestFingerprint is null)
            {
                _storage.SetRequestFingerprint(decisionId, decision, fingerprint);
            }

            if (ReadRecordedResult(decisionId, decision) is { IsCompleted: true } recorded)
            {
                var winner = recorded.GetResult<TaskId>();
                if (!candidates.Contains(winner))
                {
                    throw new DurableTaskDivergenceException(
                        $"Durable completion decision '{decisionId}' was recorded for candidate '{winner}', which is not in the supplied candidate set.");
                }

                return winner;
            }
        }
        else
        {
            decision = GetOrCreateTask(decisionId, request: null);
            _storage.SetRequestFingerprint(decisionId, decision, fingerprint);
        }

        while (true)
        {
            // When several candidates have completed, the first in candidate order wins.
            foreach (var candidate in candidates)
            {
                var response = await GetCompletionCandidateHandle(candidate).PollAsync(
                    new PollingOptions { PollTimeout = TimeSpan.Zero },
                    cancellationToken);
                if (!response.IsCompleted)
                {
                    continue;
                }

                decision = GetOrCreateTask(decisionId, request: null);
                StoreResult(decisionId, decision, DurableTaskResponse.FromResult(candidate));
                await WriteStateAsync(cancellationToken);
                return candidate;
            }

            await WaitForAnyAsync(attempt, candidates, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until any candidate's handle completes, parking <paramref name="attempt"/> meanwhile, then cancels and drains
    /// the other waits.
    /// </summary>
    private async Task WaitForAnyAsync(GrainDurableTaskContext attempt, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waits = new Task<DurableTaskResponse>[candidates.Count];
        for (var index = 0; index < waits.Length; index++)
        {
            waits[index] = GetCompletionCandidateHandle(candidates[index]).WaitAsync(waitCancellation.Token).AsTask();
        }

        try
        {
            // A failed wait propagates, as a failed poll does.
            await await attempt.ParkAsync(Task.WhenAny(waits), candidates);
        }
        finally
        {
            waitCancellation.Cancel();
            await ((Task)Task.WhenAll(waits)).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private IScheduledTaskHandle GetCompletionCandidateHandle(TaskId taskId)
    {
        if (TryGetScheduledTaskHandle(taskId, out var handle))
        {
            return handle;
        }

        // Selection receives scheduled inputs. An ancestor's cancellation refuses their starts without storing them.
        return IsAncestorCancellationRequested(taskId, state: null, shielded: false)
            ? new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled)
            : GetScheduledTaskHandle(taskId);
    }

    // A request fingerprint is a hexadecimal hash, so it never has this prefix.
    private const string CompletionDecisionFingerprintPrefix = "$completion-decision:";

    /// <summary>
    /// Creates the error for scheduling an operation under an identifier which records another operation, which is how a
    /// replay which diverges from its recorded execution surfaces. The attempt's context stalls the attempt with it.
    /// </summary>
    private static DurableTaskDivergenceException CreateDivergenceException(string message, IDurableTaskState recorded, string requested) =>
        new(DescribeDivergence(message, recorded, requested));

    /// <summary>
    /// Returns the outcome which an earlier attempt recorded under <paramref name="taskId"/>, decoding it on first access.
    /// </summary>
    /// <exception cref="DurableTaskDivergenceException">The recorded outcome no longer decodes.</exception>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any decoding failure is a divergence from the record.")]
    private static DurableTaskResponse? ReadRecordedResult(TaskId taskId, IDurableTaskState state)
    {
        try
        {
            return state.Result;
        }
        catch (Exception exception)
        {
            throw new DurableTaskDivergenceException(
                $"The outcome which an earlier attempt recorded for durable task '{taskId}' no longer decodes: {exception.Message} A replay reads every recorded outcome; code which decodes it replays the execution.",
                exception);
        }
    }

    /// <summary>Creates the error for scheduling a request under a task identifier which records another operation.</summary>
    private static DurableTaskConflictException CreateScheduleConflictException(
        TaskId taskId,
        string message,
        IDurableTaskState recorded,
        string requested) =>
        new(taskId, DescribeDivergence(message, recorded, requested));

    private static string DescribeDivergence(string message, IDurableTaskState recorded, string requested) =>
        $"{message} It records {DescribeOperation(recorded)}, and this attempt requested {requested}. A replay schedules the same operation, with the same arguments, under each identifier; new workflow steps use new identifiers.";

    private static string DescribeOperation(IDurableTaskState state) => state switch
    {
        { RemoteTarget.IsDefault: false } => $"a call to grain '{state.RemoteTarget}' ({AbbreviateFingerprint(state.RemoteRequestFingerprint)})",
        { DueTime: { } dueTime } => $"a delay due at {dueTime:O}",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(CompletionDecisionFingerprintPrefix, StringComparison.Ordinal) => "a selection of the first task to complete",
        _ when TryGetDetachedTask(state, out var target, out var rootId) => DescribeDetachedStart(target, rootId),
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalSendFingerprintPrefix, StringComparison.Ordinal) => "a signal's send",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalFingerprintPrefix, StringComparison.Ordinal) => "a signal's value",
        { RequestFingerprint: { } fingerprint } when TryParseIdentity(SubscriptionFingerprintPrefix, fingerprint, out var target, out var subscribed) => DescribeSubscription(target, subscribed),
        { RequestFingerprint: EffectFingerprint } => "a DurableTask.RunInline effect",
        { RequestFingerprint: VersionFingerprint } => "a version decision",
        { RequestFingerprint: { } fingerprint } => $"a request ({AbbreviateFingerprint(fingerprint)})",
        { CancellationRequestedAt: not null, CompletedAt: null } => "a cancellation which arrived before its request",
        _ => "a local child task",
    };

    private static string DescribeRequest(IDurableTaskRequest request, string fingerprint) =>
        $"{request.GetInterfaceName()}.{request.GetMethodName()} ({AbbreviateFingerprint(fingerprint)})";

    // WhenAny, Race, timeouts, and signal deadlines record their selections alike, so the description names none of them.
    private static string DescribeSelection(int candidateCount) =>
        $"a selection of the first task to complete among {candidateCount} {(candidateCount == 1 ? "candidate" : "candidates")}";

    private static string AbbreviateFingerprint(string? fingerprint) =>
        fingerprint is null ? "no fingerprint" : $"fingerprint {fingerprint[..Math.Min(12, fingerprint.Length)]}";
    private static string GetCompletionDecisionFingerprint(IReadOnlyList<TaskId> candidates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var candidate in candidates)
        {
            var value = Encoding.UTF8.GetBytes(candidate.ToString());
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }

        return $"{CompletionDecisionFingerprintPrefix}{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <returns>The new execution context.</returns>
    /// <remarks>
    /// A body which starts, or replays, after cancellation of its task was requested observes the request from the start:
    /// its <see cref="DurableTaskContext.CancellationToken"/> is canceled,
    /// <see cref="DurableTaskContext.IsTaskCancellationRequested"/> is set, and
    /// <see cref="DurableTaskContext.TaskCancellationReason"/> reports the recorded reason.
    /// </remarks>
    private GrainDurableTaskContext CreateExecutionContext(TaskId taskId)
    {
        var utcNow = GetOrCreateLogicalTime(taskId);

        if (!_executionContexts.TryGetValue(taskId, out var context))
        {
            _executionContexts[taskId] = context = new(taskId, this, GrainContext, TaskScheduler.Current, utcNow);
        }

        if (_storage.TryGetTask(taskId, out var taskState) && taskState.CancellationRequestedAt.HasValue)
        {
            // Nothing observes the token before the body starts.
            context.RequestTaskCancellation(taskState.CancellationReason);
        }

        return context;
    }

    private DateTimeOffset GetOrCreateLogicalTime(TaskId taskId)
    {
        if (_logicalTimes.TryGetValue(taskId, out var utcNow))
        {
            return utcNow;
        }

        utcNow = UtcNow;
        _logicalTimes[taskId] = utcNow;
        return utcNow;
    }

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableTaskContext? executionContext) => _executionContexts.TryGetValue(taskId, out executionContext);

    private TaskHandle GetOrCreateRunningTaskHandle(TaskId taskId, GrainId remoteTarget = default)
    {
        var handle = GetOrAddTaskHandle(taskId, remoteTarget);
        handle.IsRunning = true;
        return handle;
    }

    private TaskHandle GetOrAddTaskHandle(TaskId taskId, GrainId remoteTarget = default)
    {
        if (!_taskHandles.TryGetValue(taskId, out var handle))
        {
            _taskHandles[taskId] = handle = new TaskHandle(taskId, this, remoteTarget);
        }

        return handle;
    }

    private bool TryRegisterCompletionDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (destination.GrainId.IsDefault || destination.GrainId.IsClient() || state.CompletionDestinations.Contains(destination))
        {
            return false;
        }

        _storage.AddCompletionDestination(taskId, state, destination);
        return true;
    }

    private void ValidateCaller(TaskId taskId, IDurableTaskState state, GrainId callerId)
    {
        if (callerId.IsDefault)
        {
            return;
        }

        var existingCaller = !state.CallerId.IsDefault
            ? state.CallerId
            : state.Request?.Context?.CallerId ?? default;
        if (!existingCaller.IsDefault && existingCaller != callerId)
        {
            throw new DurableTaskConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with caller '{existingCaller}', not '{callerId}'.");
        }
    }

    private void StageCaller(TaskId taskId, IDurableTaskState state, GrainId callerId)
    {
        if (!callerId.IsDefault && state.CallerId.IsDefault)
        {
            _storage.SetCallerId(taskId, state, callerId);
        }
    }

    /// <summary>Sends a terminal task's result to one of its subscribers.</summary>
    /// <remarks>
    /// A subscriber on another grain receives a completion through the outbox, which the runtime provides from the stored
    /// result, and the task retains the result until the completion retires. A subscriber on this grain receives the
    /// result directly, in the same write, and retires at once.
    /// </remarks>
    private void SendCompletion(TaskId taskId, DurableTaskSubscriber destination)
    {
        if (destination.GrainId == GrainId)
        {
            var found = _storage.TryGetTask(taskId, out var state);
            Debug.Assert(found && state!.Result is { IsCompleted: true }, "A completion is sent for a stored result.");
            CompleteSubscription(destination.TaskId, taskId, state!.Result!);
            RetireDestination(taskId, state, destination, deadLettered: false);
            return;
        }

        var transport = MessageTransport;
        var messageId = transport.GetCompletionMessageId(GrainId, destination, taskId);
        PendingCompletions[messageId] = new(taskId, destination);
        transport.Send(messageId);
    }

    private Dictionary<MessageId, PendingCompletion> PendingCompletions
    {
        get
        {
            if (_pendingCompletions is { } pending)
            {
                return pending;
            }

            // Every completed task's destination has a completion pending in the outbox: the completion is staged with
            // the result, and the destination is removed when the outbox retires it. Destinations on this grain retire
            // in the write which stores the result.
            pending = [];
            var transport = MessageTransport;
            foreach (var (taskId, state) in _storage.Tasks)
            {
                if (state.CompletedAt.HasValue)
                {
                    foreach (var destination in state.CompletionDestinations)
                    {
                        Debug.Assert(destination.GrainId != GrainId, "This grain's subscribers retire with the result.");
                        pending[transport.GetCompletionMessageId(GrainId, destination, taskId)] = new(taskId, destination);
                    }
                }
            }

            return _pendingCompletions = pending;
        }
    }

    /// <remarks>
    /// The runtime provides the invocations which its table of pending invocations holds, and the completions of its
    /// tasks' destinations. It never withdraws either: a pending invocation stays in the table, and a pending completion's
    /// destination keeps its task and result, until <see cref="IOutboxMessageSource.OnRetired"/> removes them, and tasks
    /// with destinations are never deleted. An exception from decoding the stored result propagates, so the outbox
    /// dead-letters the completion.
    /// </remarks>
    bool IOutboxMessageSource.TryGetMessage(MessageId messageId, out OutboxMessage message)
    {
        if (_invocations.TryGetValue(messageId, out var invocation))
        {
            message = MessageTransport.CreateInvocation(messageId, invocation.Target, invocation.Payload, invocation.SentAt);
            return true;
        }

        if (!PendingCompletions.TryGetValue(messageId, out var completion))
        {
            message = default;
            return false;
        }

        var found = _storage.TryGetTask(completion.TaskId, out var state);
        Debug.Assert(found && state!.Result is { IsCompleted: true }, "A pending completion retains its task's result.");
        message = MessageTransport.CreateCompletionMessage(
            messageId,
            GrainId,
            completion.Destination,
            completion.TaskId,
            state!.Result!);
        return true;
    }

    /// <summary>
    /// Records the outcome of a retired invocation on the record which started it, or removes a retired completion's
    /// destination, deleting the task with its execution tree once its result has been delivered to every destination.
    /// </summary>
    /// <remarks>
    /// The outbox stages the message's removal in the same journal write, so the changes commit with the retirement.
    /// Retirement updates only the task's metadata, so it succeeds when the stored result no longer decodes; polling such a
    /// result surfaces the decoding error to the poller.
    /// </remarks>
    void IOutboxMessageSource.OnRetired(MessageId messageId, OutboxMessageRetirement retirement)
    {
        if (_invocations.TryGetValue(messageId, out var invocation))
        {
            _invocations.Remove(messageId);
            RetireInvocation(invocation, retirement);
            return;
        }

        var removed = PendingCompletions.Remove(messageId, out var completion);
        Debug.Assert(removed, "The outbox retires only the invocations and completions which the runtime provides.");
        if (!_storage.TryGetTask(completion.TaskId, out var state))
        {
            Debug.Fail("A pending completion's task is retained until the completion retires.");
            return;
        }

        RetireDestination(
            completion.TaskId,
            state,
            completion.Destination,
            deadLettered: retirement.Status != OutboxMessageRetirementStatus.Delivered);
    }

    /// <summary>
    /// Removes a destination which holds, or can no longer receive, a terminal task's result, deleting the task with its
    /// execution tree once no destination remains.
    /// </summary>
    /// <remarks>
    /// A delivered destination durably holds the result, so the task needs no retention for it. A task without a grain
    /// caller, such as a task which a client scheduled or a durable execution detached, keeps its result for the retention period after
    /// completion, so polls observe it. A dead-lettered completion restarts the result's retention period, so a poller,
    /// or a caller which invokes the task again, can still observe it; the task is then deleted when that period ends.
    /// </remarks>
    private void RetireDestination(TaskId taskId, IDurableTaskState state, DurableTaskSubscriber destination, bool deadLettered)
    {
        Debug.Assert(IsInvoked(state), "Only invoked tasks have completion destinations.");
        _storage.RemoveCompletionDestination(taskId, state, destination);
        if (deadLettered)
        {
            // Retention starts now: the dead letter can follow a long retry period, and the result must remain
            // available to a poller or a re-invocation for the full retention period.
            _storage.RetainResult(taskId, state);
        }

        if (state.CompletionDestinations.Count > 0)
        {
            return;
        }

        // A retention which restarted, because a completion was dead-lettered, outlasts delivery.
        if (state.RetainedSince.HasValue || state.CallerId.IsDefault)
        {
            TrackExpiry(GetRetentionExpiry(state));
        }
        else
        {
            DeleteTree(taskId, state);
        }
    }

    /// <summary>A completion pending in the outbox.</summary>
    private readonly record struct PendingCompletion(TaskId TaskId, DurableTaskSubscriber Destination);

    private void StageCancellation(TaskId taskId, GrainId target, string? reason) =>
        MessageTransport.SendCancellation(GrainId, target, taskId, reason);

    private ValueTask WriteStateAsync(CancellationToken cancellationToken) => _storage.WriteAsync(cancellationToken);

    /// <summary>Returns a task's state, staging the state of a task which doesn't exist.</summary>
    private IDurableTaskState GetOrCreateTask(TaskId taskId, IDurableTaskRequest? request)
    {
        var reportCreation = DurableTaskEvents.IsTaskCreatedEnabled && !_storage.TryGetTask(taskId, out _);
        var state = _storage.GetOrCreateTask(taskId, request);
        if (reportCreation)
        {
            DurableTaskEvents.EmitTaskCreated(GrainContext, taskId);
        }

        return state;
    }

    /// <summary>Stages a task's terminal result.</summary>
    private void SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        _storage.SetResponse(taskId, state, response);
        DurableTaskEvents.EmitTaskCompleted(GrainContext, taskId, response);
    }

    /// <summary>Stages a new terminal record of an execution, such as an effect's outcome or a version decision.</summary>
    private void AddRecord(TaskId taskId, string fingerprint, DurableTaskResponse response)
    {
        _storage.AddRecord(taskId, fingerprint, response);
        DurableTaskEvents.EmitTaskCreated(GrainContext, taskId);
        DurableTaskEvents.EmitTaskCompleted(GrainContext, taskId, response);
    }

    /// <summary>Stages a task's deletion.</summary>
    private void RemoveTask(TaskId taskId)
    {
        if (_storage.RemoveTask(taskId))
        {
            DurableTaskEvents.EmitTaskDeleted(GrainContext, taskId);
        }
    }


    /// <summary>
    /// Stages the invocation of <paramref name="request"/> on another grain as the remote child <paramref name="taskId"/>,
    /// without writing, and returns the child's recorded terminal response, or <see cref="DurableTaskResponse.Pending"/>.
    /// </summary>
    private DurableTaskResponse StageRemote(TaskId taskId, IDurableTaskRequest request, bool shielded)
    {
        var transport = MessageTransport;
        var context = request.Context ?? throw new InvalidOperationException("The durable task request has no context.");
        if (context.TargetId == GrainId)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' cannot invoke its owning grain '{GrainId}' through durable messaging.");
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (state.Request is not null
                || state.RequestFingerprint is not null
                || !state.CallerId.IsDefault
                || state.DueTime is not null
                || (!state.RemoteTarget.IsDefault && state.RemoteTarget != context.TargetId)
                || (state.RemoteRequestFingerprint is { } existing
                    && !string.Equals(existing, fingerprint, StringComparison.Ordinal)))
            {
                throw CreateDivergenceException(
                    $"Durable task '{taskId}' is already associated with a different request.",
                    state,
                    $"a call to grain '{context.TargetId}' ({AbbreviateFingerprint(fingerprint)})");
            }

            if (ReadRecordedResult(taskId, state) is { IsCompleted: true } completed)
            {
                return completed;
            }
        }

        state ??= GetOrCreateTask(taskId, request: null);
        MarkShielded(taskId, state, shielded);
        if (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)
        {
            _storage.SetRemoteRequest(taskId, state, context.TargetId, fingerprint);
        }

        context.CallerId = GrainId;
        context.SupportsDurableCompletion = true;

        // A recorded acceptance shows that the invocation retired, and the target holds the task.
        if (state.Accepted is null)
        {
            SendInvocation(transport.CreateInvocation(GrainId, context.TargetId, taskId, request), recordId: taskId);
        }

        return DurableTaskResponse.Pending;
    }

    // Fingerprints of operations in an execution tree begin with '$', so they never equal a hexadecimal request fingerprint.
    private const string EffectFingerprint = "$effect";

    private static bool IsEffectRecord(IDurableTaskState state) =>
        string.Equals(state.RequestFingerprint, EffectFingerprint, StringComparison.Ordinal);

    /// <summary>
    /// Returns the outcome which <paramref name="effectId"/> records, or runs <paramref name="effect"/> and stages a
    /// record of its outcome, without writing.
    /// </summary>
    /// <remarks>
    /// The caller runs on the activation, so the record and the state changes which the effect made are staged in the
    /// same work item and commit in the next journal write, whichever operation requests it. The record is a terminal
    /// entry of the execution's tree and is deleted with the tree. The outcome is serialized as task results are, and a
    /// result which doesn't serialize is recorded and returned as that failure, so every run observes the same failure.
    /// Another kind of record under the effect's identifier, or a named combinator's inputs beneath it, is a divergence.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A result which can't be recorded is recorded as that failure.")]
    DurableTaskResponse IDurableTaskGrainRuntime.RunEffect<TState>(
        TaskId effectId,
        TState state,
        Func<TState, DurableTaskResponse> effect)
    {
        ThrowIfStopping();
        if (_storage.TryGetTask(effectId, out var record))
        {
            if (!IsEffectRecord(record) || ReadRecordedResult(effectId, record) is not { IsCompleted: true } outcome)
            {
                throw CreateDivergenceException(
                    $"Durable child task '{effectId}' is already associated with a different operation.",
                    record,
                    "a DurableTask.RunInline effect");
            }

            return outcome;
        }

        // A named combinator records only its inputs, beneath its identifier.
        if (_storage.GetChildren(effectId).FirstOrDefault() is (var inputId, { } input))
        {
            throw CreateDivergenceException(
                $"Durable child task '{effectId}' holds '{inputId}', which an earlier run recorded.",
                input,
                "a DurableTask.RunInline effect");
        }

        var response = effect(state);
        try
        {
            AddRecord(effectId, EffectFingerprint, response);
        }
        catch (Exception exception) when (response.Exception is null)
        {
            response = DurableTaskResponse.FromException(exception);
            AddRecord(effectId, EffectFingerprint, response);
        }

        return response;
    }

    // Version records are terminal records of their execution, as effect records are, whose result is the version.
    private const string VersionFingerprint = "$version";

    /// <summary>The segment prefix of the records of version decisions beneath an execution.</summary>
    internal const string VersionSegmentPrefix = "$version:";

    /// <remarks>
    /// The search walks from the execution through its enclosing executions, such as a step's parent, and stops at the
    /// invoked task which owns the tree, so a callee's or an invoked descendant's decisions stay its own. The record is
    /// a terminal entry of the execution's tree, staged in the calling work item, and is deleted with the tree.
    /// </remarks>
    int IDurableTaskGrainRuntime.GetOrRecordVersion(TaskId executionId, string name, int version)
    {
        ThrowIfStopping();
        var segment = VersionSegmentPrefix + name;
        for (var execution = executionId; !execution.IsDefault; execution = execution.Parent())
        {
            if (_storage.TryGetTask(execution.Child(segment), out var recorded))
            {
                var decision = ReadRecordedResult(execution.Child(segment), recorded);
                Debug.Assert(
                    string.Equals(recorded.RequestFingerprint, VersionFingerprint, StringComparison.Ordinal)
                    && decision is { IsCompleted: true },
                    "Only version decisions are recorded under version segments.");
                return decision!.GetResult<int>();
            }

            if (_storage.TryGetTask(execution, out var state) && IsInvoked(state))
            {
                break;
            }
        }

        var recordId = executionId.Child(segment);
        AddRecord(recordId, VersionFingerprint, DurableTaskResponse.FromResult(version));
        DurableTaskEvents.EmitVersionRecorded(GrainContext, executionId, name, version);
        return version;
    }

    private static bool IsVersionRecord(IDurableTaskState state) =>
        string.Equals(state.RequestFingerprint, VersionFingerprint, StringComparison.Ordinal);

    void IDurableTaskGrainRuntime.ThrowIfRecordedAsEffect(TaskId operationId, TaskId effectId, string requested)
    {
        if (_storage.TryGetTask(effectId, out var recorded) && IsEffectRecord(recorded))
        {
            throw CreateDivergenceException(
                $"Durable task '{operationId}' runs beneath '{effectId}', which an earlier run recorded.",
                recorded,
                requested);
        }
    }

    public async ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, string? reason, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        if (state.RemoteTarget.IsDefault || state.RemoteTarget != target)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' is not associated with remote target '{target}'.");
        }

        // The cancellation and its message commit together; the outbox delivers the message only after that commit. The
        // message carries the recorded reason, which the first request determines.
        var recordedReason = state.CancellationRequestedAt.HasValue ? state.CancellationReason : reason;
        _storage.RequestCancellation(taskId, state, reason);
        if (!_preStagedCancellations.Remove((taskId, target)))
        {
            StageCancellation(taskId, target, recordedReason);
        }

        await WriteStateAsync(cancellationToken);
    }

    /// <summary>Requests cancellation of a local task through its handle.</summary>
    /// <remarks>
    /// A cancellation leaf, such as a delay or a task whose body hasn't started, terminalizes as canceled in the write
    /// which records the request. A started body observes the request through its durable token and decides its outcome.
    /// </remarks>
    internal async ValueTask CancelScheduledTaskAsync(TaskId taskId, string? reason, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        if (!IsCancellationLeaf(taskId, state))
        {
            await SignalCancellationAsync(taskId, callerId: default, reason, cancellationToken);
            return;
        }

        var propagation = StageCancellationTree(taskId, callerId: default, reason);
        var propagated = StagePropagation(propagation.Contexts, propagation.Handles, reason);
        await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
        await propagated.WaitAsync(cancellationToken);
    }

    /// <summary>Returns whether a task's body has started, which records the execution's logical time.</summary>
    private bool HasStarted(TaskId taskId) => _logicalTimes.ContainsKey(taskId);

    /// <summary>
    /// Returns whether a requested cancellation terminalizes a task as canceled without running a body which decides its
    /// outcome.
    /// </summary>
    /// <remarks>
    /// Delays and tasks whose body hasn't started are leaves. A remote child's callee decides that child's outcome, and a
    /// started body runs, or replays, with its durable token canceled.
    /// </remarks>
    private bool IsCancellationLeaf(TaskId taskId, IDurableTaskState state) =>
        state.RemoteTarget.IsDefault && (state.DueTime is not null || !HasStarted(taskId));

    public async ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var transport = MessageTransport;
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (ReadRecordedResult(taskId, state) is { IsCompleted: true } completed)
            {
                return completed;
            }

            if (state.DueTime is { } existingDueTime)
            {
                // A durable delay is a commitment: replay keeps the recorded due time, even when the workflow now
                // requests another one, for example after a configured duration changed.
                if (existingDueTime != dueTime)
                {
                    _shared.Logger.LogWarning(
                        "{Id} durable delay '{TaskId}' is due at {RecordedDueTime:O}; its replay requested {RequestedDueTime:O}. The recorded due time is kept.",
                        GrainId,
                        taskId,
                        existingDueTime,
                        dueTime);
                }

                // A replay can observe a delay staged by an earlier attempt whose write did not complete.
                await WriteStateAsync(cancellationToken);
                return DurableTaskResponse.Pending;
            }
        }

        state ??= GetOrCreateTask(taskId, request: null);
        var generation = checked(state.ResumeGeneration + 1);
        _storage.SetDelay(taskId, state, dueTime, generation);

        // The grain's own inbox accepts the resume in the same journal write as the delay, so every durable delay has a
        // durable resume.
        transport.SendResume(GrainId, taskId, generation, dueTime);
        await WriteStateAsync(cancellationToken);
        return DurableTaskResponse.Pending;
    }

    internal ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        TimeSpan duration,
        CancellationToken cancellationToken) =>
        ScheduleDelayAsync(
            taskId,
            GetOrCreateLogicalTime(taskId) + duration,
            cancellationToken);

    /// <summary>
    /// Stages the outcome of a delay whose scheduled resume message became due, completing its handle after commit.
    /// </summary>
    /// <remarks>
    /// A delay whose cancellation was requested terminalizes as canceled. A pending delay of the same generation
    /// completes. Resumes for an earlier generation, a terminal delay, or a missing task change nothing.
    /// </remarks>
    internal void ResumeDelayFromInbox(TaskId taskId, long generation, GrainId sender)
    {
        ThrowIfStopping();
        if (sender != GrainId)
        {
            throw new InvalidOperationException(
                $"Durable delay '{taskId}' does not accept resumes from grain '{sender}'.");
        }

        if (!_storage.TryGetTask(taskId, out var state)
            || state.Result is { IsCompleted: true })
        {
            return;
        }

        if (state.CancellationRequestedAt.HasValue)
        {
            _pendingHandleResponses[taskId] = StageResponse(taskId, DurableTaskResponse.Canceled);
            return;
        }

        if (state.DueTime is null || state.ResumeGeneration != generation)
        {
            return;
        }

        _pendingHandleResponses[taskId] = StageResponse(taskId, DurableTaskResponse.Completed);
    }

    private DurableTaskResponse StageAcceptedResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can be accepted.");
        }

        if (state.Result is { IsCompleted: true } terminal)
        {
            return terminal;
        }

        StoreResult(taskId, state, response);
        return response;
    }

    /// <summary>
    /// Stages a remote child's terminal response, which its callee sent for the task which it stores under this grain's
    /// scope.
    /// </summary>
    internal void AcceptResponseFromInbox(TaskId taskId, DurableTaskResponse response, GrainId sender) =>
        AcceptResponseFromInbox(taskId, CallerScope.GetTaskId(GrainId, taskId), response, sender);

    /// <summary>
    /// Stages the terminal response of a task of <paramref name="sender"/> which <paramref name="taskId"/> awaits: a remote
    /// child's own result, or the result of the existing task <paramref name="sourceTaskId"/> to which a subscription
    /// child subscribed. The local handle completes after commit.
    /// </summary>
    /// <remarks>
    /// The inbox accepts the completion directly into task state, so this stored response is its only journaled copy.
    /// The sender retains its result until the inbox reports this acceptance to the sender's outbox. A completion for a
    /// task which no longer exists is accepted without changing state: the task was deleted with its execution tree,
    /// which canceled it, and nothing awaits its result. A subscription which already ended, for example because it was
    /// canceled, also accepts the completion without changes.
    /// </remarks>
    internal void AcceptResponseFromInbox(TaskId taskId, TaskId sourceTaskId, DurableTaskResponse response, GrainId sender)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state))
        {
            return;
        }

        ThrowIfNotStartedBy(taskId, sourceTaskId, state, sender);

        // The task's result shows that its grain holds the task, so it records the acceptance which the invocation's
        // retirement would otherwise record.
        if (state.Accepted is null && AwaitsAcceptance(state))
        {
            _storage.SetAccepted(taskId, state, accepted: true);
            SignalAcceptance(taskId);
        }

        if (TryGetSubscription(state, out _, out _))
        {
            CompleteSubscription(taskId, sourceTaskId, response);
            return;
        }

        _pendingHandleResponses[taskId] = StageAcceptedResponse(taskId, state, response);
    }

    /// <summary>Recovers committed task state and starts the recovered executions.</summary>
    internal async Task ResumePendingTasksAsync(CancellationToken cancellationToken)
    {
        await RecoverAsync(cancellationToken);
        StartRecoveredExecutions();
    }

    /// <summary>Returns whether the task stores its terminal result, without decoding the result.</summary>
    /// <remarks>
    /// Storage stamps <see cref="IDurableTaskState.CompletedAt"/> with every stored result, which is always terminal.
    /// Activation checks completion this way, so a stored result which no longer decodes fails only the operations which
    /// read it, such as materializing its completion or polling it.
    /// </remarks>
    private static bool HasStoredResult(IDurableTaskState state) => state.CompletedAt.HasValue;

    private async Task ResumePendingTasksCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        List<TaskId>? starts = null;
        var tasks = _storage.Tasks.ToList();
        foreach (var (taskId, state) in tasks)
        {
            if (!HasStoredResult(state)
                && state.Request is not null
                && state.DueTime is null
                && (!state.CancellationRequestedAt.HasValue || !IsCancellationLeaf(taskId, state)))
            {
                GetOrAddTaskHandle(taskId);
            }
        }

        foreach (var (taskId, state) in tasks)
        {
            if (HasStoredResult(state))
            {
                continue;
            }

            // A canceled leaf terminalizes. A remote child awaits its callee's outcome, and a started body replays below,
            // or with its parent, with its durable token canceled.
            if (state.CancellationRequestedAt.HasValue && IsCancellationLeaf(taskId, state))
            {
                await SetResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
                continue;
            }

            // A pending delay's resume message is durable with the delay, and the inbox processes it when due.
            if (state.DueTime is not null)
            {
                continue;
            }

            if (state.Request is null
                || IsRequestRunning(taskId))
            {
                continue;
            }

            (starts ??= []).Add(taskId);
        }

        if (starts is not null)
        {
            _recoveredStarts = starts;
        }

        if (DeleteExpiredTasks())
        {
            await WriteStateAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="DurableTaskResponse"/> indicating the status of the request. A response of type <see cref="PendingDurableTaskResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    async ValueTask<DurableTaskResponse> IDurableTaskServer.ScheduleAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        // Retention ends at a deadline, which can pass while the grain is otherwise idle, and this write commits the
        // deletion with the request.
        ThrowIfStopping();
        DeleteExpiredTasks();
        var scheduled = StageSchedule(taskId, request, fromInbox: false);

        // Commit the request identity and caller subscription before execution begins.
        await WriteStateAsync(cancellationToken);
        if (scheduled.CompletedResponse is { } completed)
        {
            CompleteTaskHandle(taskId, completed);
            if (DeleteExpiredTasks())
            {
                await WriteStateAsync(cancellationToken);
            }
        }

        if (scheduled.Start)
        {
            StartRequest(taskId);
        }

        return scheduled.Response;
    }

    /// <summary>
    /// Stages a request received through the inbox, starting it after the inbox batch commits.
    /// </summary>
    internal DurableTaskResponse ScheduleFromInbox(TaskId taskId, IDurableTaskRequest request) =>
        StageScheduleWithCommitEffects(taskId, request, fromInbox: true);

    /// <summary>
    /// Stages a request, starting it or completing its handle after the write which commits the staged changes.
    /// </summary>
    private DurableTaskResponse StageScheduleWithCommitEffects(TaskId taskId, IDurableTaskRequest request, bool fromInbox)
    {
        var scheduled = StageSchedule(taskId, request, fromInbox);
        if (scheduled.CompletedResponse is { } completed)
        {
            _pendingHandleResponses[taskId] = completed;
        }

        if (scheduled.Start)
        {
            _pendingStarts.Add(taskId);
        }

        return scheduled.Response;
    }

    private ScheduledRequest StageSchedule(TaskId taskId, IDurableTaskRequest request, bool fromInbox)
    {
        ThrowIfStopping();
        CallerScope.ThrowIfReserved(taskId);
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        if (requestContext.TargetId != GrainId)
        {
            throw new InvalidOperationException(
                $"The durable task request targets grain '{requestContext.TargetId}', not receiver '{GrainId}'.");
        }

        if (!fromInbox)
        {
            requestContext.CallerId = default;
            requestContext.SupportsDurableCompletion = false;
        }
        else if (requestContext.CallerId == GrainId)
        {
            // A completion is never addressed to its own grain: StageRemote rejects self-invocation.
            throw new InvalidOperationException(
                $"Durable task '{taskId}' cannot be invoked by its owning grain '{GrainId}' through durable messaging.");
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = GetOrCreateTask(taskId, request);
            StageCaller(taskId, state, requestContext.CallerId);
            _storage.SetRequestFingerprint(taskId, state, fingerprint);
            var subscribedNew = requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, GetInvoker(requestContext.CallerId, taskId));
            return new(subscribedNew ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending, Start: true, CompletedResponse: null);
        }

        if (!state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is not null)
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a remote child request.",
                state,
                DescribeRequest(request, fingerprint));
        }

        ValidateCaller(taskId, state, requestContext.CallerId);
        if (state.Request is null
            && state.RequestFingerprint is null
            && state.CancellationRequestedAt is null)
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a non-request operation.",
                state,
                DescribeRequest(request, fingerprint));
        }

        if (state.RequestFingerprint is { } existingFingerprint
            && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a different request.",
                state,
                DescribeRequest(request, fingerprint));
        }

        if (state.RequestFingerprint is null
            && state.Request is { } existingRequest
            && !string.Equals(
                IDurableTaskRequest.GetFingerprint(existingRequest, _shared.Serializer),
                fingerprint,
                StringComparison.Ordinal))
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a different request.",
                state,
                DescribeRequest(request, fingerprint));
        }

        StageCaller(taskId, state, requestContext.CallerId);
        if (state.RequestFingerprint is null)
        {
            _storage.SetRequestFingerprint(taskId, state, fingerprint);
        }

        // An invocation whose cancellation preceded its start terminalizes as canceled. A started body, which a duplicate
        // invocation reaches, decides its own outcome.
        if (state.CancellationRequestedAt.HasValue
            && state.Result is not { IsCompleted: true }
            && IsCancellationLeaf(taskId, state))
        {
            // A cancellation which preceded the invocation recorded no request.
            if (state.Request is null)
            {
                _storage.SetRequest(taskId, state, request);
            }

            if (requestContext.SupportsDurableCompletion)
            {
                TryRegisterCompletionDestination(taskId, state, GetInvoker(requestContext.CallerId, taskId));
            }

            var canceled = StageResponse(taskId, DurableTaskResponse.FromException(new OperationCanceledException()));
            return new(canceled, Start: false, CompletedResponse: canceled);
        }

        if (state.Result is { IsCompleted: true } completed)
        {
            if (ShouldSendCompletion(taskId, state, requestContext))
            {
                SendCompletion(taskId, GetInvoker(requestContext.CallerId, taskId));
            }

            return new(completed, Start: false, CompletedResponse: null);
        }

        var subscribed = requestContext.SupportsDurableCompletion
            && TryRegisterCompletionDestination(taskId, state, GetInvoker(requestContext.CallerId, taskId));

        // A request staged by an attempt whose write did not complete has not started yet.
        return new(subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending, Start: true, CompletedResponse: null);
    }

    private readonly record struct ScheduledRequest(DurableTaskResponse Response, bool Start, DurableTaskResponse? CompletedResponse);

    /// <summary>
    /// Returns the destination of the completion of <paramref name="taskId"/> to the grain which invoked it, which names the
    /// invoker's own child.
    /// </summary>
    private static DurableTaskSubscriber GetInvoker(GrainId callerId, TaskId taskId) =>
        new(callerId, CallerScope.GetCallerTaskId(callerId, taskId));

    private bool ShouldSendCompletion(
        TaskId taskId,
        IDurableTaskState state,
        DurableTaskRequestContext requestContext)
    {
        if (!requestContext.SupportsDurableCompletion
            || requestContext.CallerId.IsDefault
            || requestContext.CallerId.IsClient())
        {
            return false;
        }

        if (state.CompletionDestinations.Contains(GetInvoker(requestContext.CallerId, taskId)))
        {
            // The completion to this destination is still pending in the outbox.
            return false;
        }

        return TryRegisterCompletionDestination(taskId, state, GetInvoker(requestContext.CallerId, taskId));
    }

    private void StartRequest(TaskId taskId)
    {
        if (_stopping || IsRequestRunning(taskId))
        {
            return;
        }

        if (!_storage.TryGetTask(taskId, out var state)
            || state.Result is { IsCompleted: true })
        {
            return;
        }

        // A request whose cancellation precedes its start has no body which observes it.
        if (state.CancellationRequestedAt.HasValue && IsCancellationLeaf(taskId, state))
        {
            TrackBackgroundTask(
                SetResponseAsync(taskId, DurableTaskResponse.Canceled, CancellationToken.None),
                "terminalizing a canceled deferred durable task");
            return;
        }

        if (state.Request is not { } committedRequest)
        {
            return;
        }

        var executionContext = CreateExecutionContext(taskId);
        _ = GetOrCreateRunningTaskHandle(taskId);
        committedRequest.SetTarget(GrainContext);
        StartInvocation(taskId, static value => value.CreateTask(), committedRequest, executionContext);
    }

    /// <summary>
    /// Reports work while the grain holds a runnable execution, so the inbox's durable wake-up reactivates the grain, and
    /// recovery resumes the execution, after a deactivation or a crash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An attempt is runnable while one of its segments runs or is queued. A parked attempt waits for an event which a
    /// message delivers: a child's result, a signal, a delay's resume, or the first of several completions. That message
    /// reactivates the grain. A journal write also makes attempts runnable once it commits: it starts the requests it
    /// accepts, completes the handles of the results and signals it stores, and propagates the cancellations it records.
    /// The inbox consults this source while it prepares each write, before the runtime captures those effects, and ensures
    /// the wake-up before the write can become durable. Committed cancellations propagate in background work, which counts
    /// as work until it ends.
    /// </para>
    /// <para>
    /// Messaging consults its work sources when the activation starts, before this runtime recovers its executions, so
    /// until recovery has run, the pending executions which storage holds count as runnable. This re-arms the wake-up from
    /// recovered state, so committed work is never stranded.
    /// </para>
    /// <para>
    /// The delay, <see cref="Orleans.Configuration.DurableTaskOptions.RunnableExecutionWakeupDelay"/>, bounds how late a
    /// grain which crashed resumes a runnable attempt. The wake-up only matters after a crash or a deactivation: while the
    /// grain is active, a wake-up only reschedules itself, so a long delay keeps that cheap rather than polling.
    /// </para>
    /// <para>
    /// A stalled execution waits for code which can replay it, which only a later activation runs, so it keeps a slow
    /// wake-up, <see cref="Orleans.Configuration.DurableTaskOptions.StalledExecutionRetryInterval"/>: once the activation ends, such as when a
    /// rolling deployment stops its silo, the wake-up reactivates the grain and recovery replays the execution.
    /// </para>
    /// </remarks>
    TimeSpan? IInboxWorkSource.GetWakeupDelay() =>
        HasRunnableAttempts()
        || _pendingStarts.Count > 0
        || _pendingHandleResponses.Count > 0
        || _pendingCancellationPropagations.Count > 0
        || _backgroundTasks.Count > 0
        || _recoveredStarts is not null
        || (!_recovered && HasPendingExecutions())
            ? _shared.Options.RunnableExecutionWakeupDelay
            : HasStalledExecutions() ? _shared.Options.StalledExecutionRetryInterval : null;

    private bool HasRunnableAttempts()
    {
        foreach (var context in _executionContexts.Values)
        {
            if (context.IsRunnable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether storage holds an execution which recovery would run.</summary>
    private bool HasPendingExecutions()
    {
        foreach (var (_, state) in _storage.Tasks)
        {
            if (!HasStoredResult(state) && state.Request is not null && state.DueTime is null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a stalled execution is still pending, forgetting those which ended, such as by an abort.</summary>
    private bool HasStalledExecutions()
    {
        _stalledExecutions.RemoveWhere(taskId => !_storage.TryGetTask(taskId, out var state) || HasStoredResult(state));
        return _stalledExecutions.Count > 0;
    }

    /// <summary>
    /// Cancels the attempt of an execution which the running code can't replay, keeps its task pending, and reports the
    /// stall.
    /// </summary>
    /// <remarks>
    /// The attempt is canceled as deactivation, an abort, or a deletion cancels it, so its later durable operations throw
    /// and its result is discarded. The task keeps no outcome, and the grain's slow wake-up lets a later
    /// activation replay it.
    /// </remarks>
    void IDurableTaskGrainRuntime.StallAttempt(GrainDurableTaskContext context, DurableTaskAttemptCancellationReason reason, Exception exception)
    {
        Debug.Assert(
            reason is DurableTaskAttemptCancellationReason.UnsupportedVersion or DurableTaskAttemptCancellationReason.Diverged,
            "An attempt stalls when the running code can't replay its execution.");
        if (!CancelAttempt(context, reason))
        {
            return;
        }

        _stalledExecutions.Add(context.Id);
        _shared.Logger.LogWarning(exception, "{Id} durable task '{TaskId}' stalled: {Reason}.", GrainId, context.Id, reason);
        DurableTaskEvents.EmitExecutionStalled(GrainContext, context.Id, reason, exception);
    }


    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        throw new InvalidOperationException($"Journal state '{StateMachineName}' does not write journal entries.");

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _pendingStarts.Clear();
        _pendingHandleResponses.Clear();
        foreach (var propagation in _pendingCancellationPropagations.Values)
        {
            // The cancellation which the propagation follows didn't commit.
            propagation.Completion?.TrySetCanceled();
        }

        _pendingCancellationPropagations.Clear();
        _preStagedCancellations.Clear();
        _pendingCompletions = null;
        _nextExpiry = DateTimeOffset.MinValue;
        _committingStarts = [];
        _committingHandleResponses = [];
        _committingCancellationPropagations = [];
    }

    ValueTask IStateMachine.PrepareWriteAsync(CancellationToken cancellationToken) => default;

    void IStateMachine.WritePendingEntries(JournalStreamWriter writer) => CaptureCommitEffects();

    void IStateMachine.WriteSnapshot(JournalStreamWriter writer) => CaptureCommitEffects();

    void IStateMachine.OnWriteCompleted() => RunCommittedEffects();

    private void CaptureCommitEffects()
    {
        // Effects stay pending until a write which captured them completes, so a capture which writes no bytes
        // leaves them for the next write.
        _committingStarts = (_pendingStarts.Count == 0) ? [] : [.. _pendingStarts];
        _committingHandleResponses = (_pendingHandleResponses.Count == 0) ? [] : [.. _pendingHandleResponses];
        _committingCancellationPropagations = (_pendingCancellationPropagations.Count == 0) ? [] : [.. _pendingCancellationPropagations.Keys];
    }

    private void RunCommittedEffects()
    {
        var starts = _committingStarts;
        var handleResponses = _committingHandleResponses;
        var propagations = _committingCancellationPropagations;
        _committingStarts = [];
        _committingHandleResponses = [];
        _committingCancellationPropagations = [];

        foreach (var taskId in starts)
        {
            if (_pendingStarts.Remove(taskId))
            {
                StartRequest(taskId);
            }
        }

        foreach (var (taskId, response) in handleResponses)
        {
            // A later stage of the same task's response, which a later write commits, stays pending.
            if (_pendingHandleResponses.TryGetValue(taskId, out var pending) && ReferenceEquals(pending, response))
            {
                _pendingHandleResponses.Remove(taskId);
            }
        }

        List<CancellationPropagation>? committedPropagations = null;
        foreach (var id in propagations)
        {
            if (_pendingCancellationPropagations.Remove(id, out var propagation))
            {
                (committedPropagations ??= []).Add(propagation);
            }
        }

        if (committedPropagations is null && _orderedCommittedEffects is null)
        {
            foreach (var entry in handleResponses)
            {
                CompleteTaskHandle(entry.Key, entry.Value);
            }
        }
        else
        {
            // An attempt observes its task's cancellation before any outcome which this or a later write commits, such as a
            // child which the cancellation ended. The attempts' task cancellation runs their tokens' registrations, so it
            // runs after this write's acknowledgement, and the handles which this write completes wait for it.
            if (_orderedCommittedEffects is null)
            {
                _orderedCommittedEffects = [];
                TrackBackgroundTask(RunOrderedCommittedEffectsAsync(), "propagating committed durable task cancellation");
            }

            _orderedCommittedEffects.Add(new(committedPropagations ?? [], handleResponses));
        }

        if (!_stopping)
        {
            // Checking for expired tasks is cheap until the earliest expiry passes.
            if (!(_pendingHandleResponses.Count == 0) || DeleteExpiredTasks())
            {
                QueueBackgroundWrite();
            }
        }
    }

    /// <summary>The cancellation propagations and handle completions which one write committed.</summary>
    private readonly record struct CommittedEffects(
        List<CancellationPropagation> Propagations,
        KeyValuePair<TaskId, DurableTaskResponse>[] HandleResponses);

    /// <summary>
    /// Runs the committed effects which wait for task cancellation, in commit order: each write's cancellation of
    /// attempts, then its handle completions, then its cancellation of the leaves which it reaches, such as delays.
    /// </summary>
    /// <remarks>Effects which writes commit while this runs join it, so their handles complete after the earlier cancellations.</remarks>
    private async Task RunOrderedCommittedEffectsAsync()
    {
        await Task.Yield();
        while (_orderedCommittedEffects is { Count: > 0 } batches)
        {
            _orderedCommittedEffects = [];
            foreach (var (committedPropagations, handleResponses) in batches)
            {
                var failures = new List<Exception>?[committedPropagations.Count];
                for (var index = 0; index < committedPropagations.Count; index++)
                {
                    failures[index] = RequestTaskCancellation(committedPropagations[index]);
                }

                foreach (var entry in handleResponses)
                {
                    CompleteTaskHandle(entry.Key, entry.Value);
                }

                for (var index = 0; index < committedPropagations.Count; index++)
                {
                    TrackBackgroundTask(
                        PropagateToLeavesAsync(committedPropagations[index], failures[index]),
                        "propagating committed durable task cancellation to leaves");
                }
            }
        }

        _orderedCommittedEffects = null;
    }

    /// <summary>Requests the task cancellation of a committed propagation's attempts, and returns their registrations' failures.</summary>
    private static List<Exception>? RequestTaskCancellation(CancellationPropagation propagation)
    {
        List<Exception>? failures = null;
        foreach (var context in propagation.Contexts)
        {
            try
            {
                context.RequestTaskCancellation(propagation.CancellationReason);
            }
            catch (AggregateException exception)
            {
                // A registration's failure leaves the other attempts' cancellation unaffected.
                (failures ??= []).Add(exception);
            }
        }

        return failures;
    }

    /// <summary>
    /// Cancels the leaves of a committed propagation, such as delays and children whose bodies haven't started, then
    /// completes the propagation with every failure.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every failure is reported through the propagation's completion.")]
    private static async Task PropagateToLeavesAsync(CancellationPropagation propagation, List<Exception>? failures)
    {
        try
        {
            await Task.WhenAll(propagation.Handles.Select(
                handle => handle.CancelAsync(propagation.CancellationReason, CancellationToken.None).AsTask()));
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            var exception = new AggregateException(failures);
            if (propagation.Completion is not { } completion)
            {
                throw exception;
            }

            completion.TrySetException(exception);
            return;
        }

        propagation.Completion?.TrySetResult();
    }

    /// <summary>Starts a child which its ancestors' durable cancellation reaches.</summary>
    public ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask durableTask,
        CancellationToken cancellationToken) =>
        ScheduleChildAsync(taskId, durableTask, shielded: false, waitForAcceptance: false, cancellationToken);

    /// <summary>Starts or reattaches to a child, and waits for the write which commits it.</summary>
    /// <param name="taskId">The child's identifier.</param>
    /// <param name="durableTask">The child's definition.</param>
    /// <param name="shielded">
    /// Whether the parent starts the child shielded. Such a child is created and runs even when an ancestor's cancellation
    /// was requested, and records that its ancestors' cancellation doesn't reach it.
    /// </param>
    /// <param name="waitForAcceptance">
    /// Whether the start completes once the child's host has accepted and durably stored it: a call to another grain
    /// awaits its target's acceptance, and a rejected call fails the start.
    /// </param>
    /// <param name="cancellationToken">A token which abandons the start.</param>
    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask durableTask,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (durableTask is IDurableTaskSubscription subscription)
        {
            var (subscriptionHandle, requiresWrite) = StageSubscription(taskId, subscription, shielded);
            if (requiresWrite)
            {
                await WriteStateAsync(cancellationToken);
            }

            return subscriptionHandle;
        }

        ThrowIfCustomScheduled(durableTask);
        var staged = StageChild(taskId, durableTask, shielded);
        if (staged.RequiresWrite)
        {
            await WriteStateAsync(cancellationToken);
        }

        if (waitForAcceptance)
        {
            await WaitForAcceptanceAsync(taskId, cancellationToken);
        }

        return staged.Handle;
    }

    /// <summary>Stages the start of a child on the activation, without writing, and returns its handle.</summary>
    /// <remarks>
    /// The start commits with the next journal write, together with the state changes which the calling work item made.
    /// </remarks>
    /// <exception cref="NotSupportedException">A custom host schedules the definition.</exception>
    public IScheduledTaskHandle PostChild(TaskId taskId, DurableTask durableTask, bool shielded)
    {
        ThrowIfStopping();
        if (durableTask is IDurableTaskSubscription subscription)
        {
            return StageSubscription(taskId, subscription, shielded).Handle;
        }

        ThrowIfCustomScheduled(durableTask);
        return StageChild(taskId, durableTask, shielded).Handle;
    }

    /// <summary>
    /// Throws for a child definition which a host other than this runtime schedules: an execution's children are its
    /// grain's local children, calls to other grains, and subscriptions, all of which this runtime records and replays.
    /// </summary>
    private static void ThrowIfCustomScheduled(DurableTask durableTask)
    {
        if (durableTask is ISchedulableTask and not IDurableTaskRequest)
        {
            throw new NotSupportedException(
                $"A durable execution runs its grain's local children, durable grain calls, and subscriptions. '{durableTask.GetType()}' is scheduled by another host.");
        }
    }

    /// <summary>Stages the start of a local child or of a call to another grain, without writing.</summary>
    /// <returns>The child's handle, and whether its start staged changes which a write commits.</returns>
    private StagedChild StageChild(TaskId taskId, DurableTask durableTask, bool shielded)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        var stateExisted = _storage.TryGetTask(taskId, out var state);
        if (stateExisted)
        {
            // Decodes the recorded outcome once, so the handle which the replay receives holds it.
            ReadRecordedResult(taskId, state!);
        }

        IScheduledTaskHandle? handle = null;
        IScheduledTaskHandle? existingScheduledHandle = null;
        if (stateExisted && TryGetScheduledTaskHandle(taskId, out var existingHandle))
        {
            existingScheduledHandle = existingHandle;
            if (existingHandle is not TaskHandle localHandle || localHandle.IsRunning)
            {
                handle = existingHandle;
            }
        }

        var remoteRequest = durableTask as IDurableTaskRequest;
        if (remoteRequest is not null)
        {
            var target = remoteRequest.Context?.TargetId
                ?? throw new InvalidOperationException("The durable task request has no target.");
            var fingerprint = IDurableTaskRequest.GetFingerprint(remoteRequest, _shared.Serializer);
            if (state is not null)
            {
                if (state.Request is not null
                    || state.RequestFingerprint is not null
                    || !state.CallerId.IsDefault
                    || state.DueTime is not null
                    || (existingScheduledHandle is TaskHandle existingTaskHandle
                        && existingTaskHandle.RemoteTarget.IsDefault)
                    || (!state.RemoteTarget.IsDefault && state.RemoteTarget != target)
                    || (state.RemoteRequestFingerprint is { } existing
                        && !string.Equals(existing, fingerprint, StringComparison.Ordinal))
                    || (handle is not null
                        && (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)))
                {
                    throw CreateDivergenceException(
                        $"Durable child task '{taskId}' is already associated with a different request.",
                        state,
                        $"a call to grain '{target}' ({AbbreviateFingerprint(fingerprint)})");
                }

                if (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)
                {
                    _storage.SetRemoteRequest(taskId, state, target, fingerprint);
                }
            }
        }
        else if (state is not null
            && (state.Request is not null
                || state.RequestFingerprint is not null
                || !state.CallerId.IsDefault
                || !state.RemoteTarget.IsDefault
                || state.RemoteRequestFingerprint is not null))
        {
            throw CreateDivergenceException(
                $"Durable child task '{taskId}' is already associated with a different request.",
                state,
                "a local child task");
        }

        // A requested cancellation terminalizes a leaf, such as a delay or a child whose body hasn't started. A remote
        // child's callee decides its outcome, and a started body replays below with its durable token canceled.
        if (state is { CancellationRequestedAt: not null, Result: not { IsCompleted: true } }
            && IsCancellationLeaf(taskId, state))
        {
            var canceled = StageResponse(taskId, DurableTaskResponse.Canceled);
            _pendingHandleResponses[taskId] = canceled;
            return new(new CompletedTaskHandle(taskId, canceled), RequiresWrite: true);
        }

        if (handle is CompletedTaskHandle)
        {
            return new(handle, RequiresWrite: false);
        }

        if (handle is TaskHandle cachedHandle
            && cachedHandle.ResponseTask.IsCompletedSuccessfully
            && cachedHandle.ResponseTask.Result.IsCompleted)
        {
            return new(handle, RequiresWrite: false);
        }

        // A child of a canceled ancestor is not created, unless the parent started it shielded. A child which existed when
        // the cancellation was requested records the request itself.
        if (state is not { CancellationRequestedAt: not null } && IsStartRefusedByAncestorCancellation(taskId, state, shielded))
        {
            return new(new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled), RequiresWrite: false);
        }

        if (handle is not null)
        {
            return new(handle, RequiresWrite: false);
        }

        if (remoteRequest is not null)
        {
            return StageRemoteChild(taskId, remoteRequest, shielded);
        }

        // Otherwise, the task must be a local method invocation. Its state is staged and commits with the next write,
        // such as the first durable operation of its body or its result, so a synchronous step costs one write. A step's
        // identity and inputs follow from its parent's inputs and earlier results, so a replay which finds no record
        // runs the step again.
        state ??= GetOrCreateTask(taskId, null);
        MarkShielded(taskId, state, shielded);

        var executionContext = CreateExecutionContext(taskId);
        handle = GetOrCreateRunningTaskHandle(taskId);
        StartInvocation(taskId, static task => task, durableTask, executionContext);
        return new(handle, RequiresWrite: false);
    }

    /// <summary>A staged start's handle, and whether the start staged changes which a write commits.</summary>
    private readonly record struct StagedChild(IScheduledTaskHandle Handle, bool RequiresWrite);

    private StagedChild StageRemoteChild(TaskId taskId, IDurableTaskRequest request, bool shielded)
    {
        var response = StageRemote(taskId, request, shielded);
        if (response.IsCompleted)
        {
            return new(new CompletedTaskHandle(taskId, response), RequiresWrite: false);
        }

        return new(GetOrCreateRunningTaskHandle(taskId, request.Context!.TargetId), RequiresWrite: true);
    }

    /// <summary>Returns whether the durable cancellation of one of a task's ancestors reaches the task.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="state">The task's state, if it exists.</param>
    /// <param name="shielded">Whether the task's parent starts it shielded.</param>
    /// <remarks>
    /// Cancellation reaches a task through its ancestors up to the nearest one which is shielded: the cancellation of
    /// that task's own ancestors doesn't reach it, so it doesn't reach its descendants either.
    /// </remarks>
    private bool IsAncestorCancellationRequested(TaskId taskId, IDurableTaskState? state, bool shielded)
    {
        if (shielded || state is { Shielded: true })
        {
            return false;
        }

        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (_storage.TryGetTask(ancestorId, out var ancestorState))
            {
                if (ancestorState.CancellationRequestedAt.HasValue)
                {
                    return true;
                }

                if (ancestorState.Shielded)
                {
                    return false;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether the durable cancellation of one of a task's ancestors refuses the task's start, after each running
    /// ancestor which the cancellation reaches observes it.
    /// </summary>
    /// <param name="taskId">The task to start.</param>
    /// <param name="state">The task's state, if it exists.</param>
    /// <param name="shielded">Whether the task's parent starts it shielded.</param>
    /// <remarks>
    /// A cancellation is staged when it arrives and reaches the running executions once the write which commits it
    /// completes. A body which starts work in between has that work refused, so it observes its own cancellation first:
    /// the refused start throws while <see cref="DurableTaskContext.IsTaskCancellationRequested"/> reports the
    /// cancellation, and the body runs its cancellation handling, such as shielded compensation. The propagation which
    /// follows the write finds the cancellation already requested.
    /// </remarks>
    private bool IsStartRefusedByAncestorCancellation(TaskId taskId, IDurableTaskState? state, bool shielded)
    {
        if (!IsAncestorCancellationRequested(taskId, state, shielded))
        {
            return false;
        }

        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (!_storage.TryGetTask(ancestorId, out var ancestorState))
            {
                continue;
            }

            if (ancestorState.CancellationRequestedAt.HasValue && TryGetExecutionContext(ancestorId, out var context))
            {
                try
                {
                    context.RequestTaskCancellation(ancestorState.CancellationReason);
                }
                catch (AggregateException exception)
                {
                    _shared.Logger.LogWarning(exception, "{Id} error canceling durable task '{TaskId}'.", GrainId, ancestorId);
                }
            }

            if (ancestorState.Shielded)
            {
                break;
            }
        }

        return true;
    }

    /// <summary>Records that a child which its parent started shielded is unreachable by its ancestors' cancellation.</summary>
    private void MarkShielded(TaskId taskId, IDurableTaskState state, bool shielded)
    {
        if (shielded && !state.Shielded)
        {
            _storage.Shield(taskId, state);
        }
    }

    private void StartInvocation<TState>(
        TaskId taskId,
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableTaskContext context)
    {
        Debug.Assert(!_runningRequests.ContainsKey(taskId), "Callers start a task's attempt only while none runs.");

        // The body starts in the attempt's first segment, a request which the activation admits as it admits grain calls.
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Invoke(createTask, state, context, start.Task);
        _runningRequests.Add(taskId, new RunningRequest(invocation, context));
        DurableTaskEvents.EmitExecutionStarted(GrainContext, taskId);
        context.Start(start);
    }

    private async Task Invoke<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableTaskContext context,
        Task start)
    {
        await start;
        DurableTaskResponse? response;
        try
        {
            response = await DurableTaskRuntimeHelper.RunAsync(createTask(state), context);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException && !IsUnregistered(context))
            {
                _shared.Logger.LogWarning(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            }

            response = DurableTaskResponse.FromException(exception);
        }

        // A cancellation which arrives while this attempt still runs, for example while a delay's journal write is in
        // flight, cancels only the attempt's token. The attempt then terminalizes the task, so a pending operation
        // which it returns, such as a delay, does not outlive the cancellation.
        if (!response.IsCompleted
            && _storage.TryGetTask(context.Id, out var taskState)
            && taskState.CancellationRequestedAt.HasValue)
        {
            response = DurableTaskResponse.Canceled;
        }

        try
        {
            // A canceled attempt's response is discarded: its task continues in the next activation, or has its outcome.
            if (response.IsCompleted && !context.IsAttemptCanceled)
            {
                Debug.Assert(!_stopping && !IsUnregistered(context), "The runtime cancels every attempt which it stops or discards.");
                await SetResponseAsync(context.Id, response, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _shared.Logger.LogWarning(exception, "{Id} error committing the response of durable task '{TaskId}'.", GrainId, context.Id);

            // A preparation failure retains the staged response, so a later write commits it and completes the handle.
            if (exception is JournaledStatePreCommitException && !_stopping)
            {
                QueueBackgroundWrite();
            }
        }
        finally
        {
            context.OnAttemptCompleted();
            if (_runningRequests.TryGetValue(context.Id, out var running)
                && ReferenceEquals(running.Context, context))
            {
                _runningRequests.Remove(context.Id);
                _executionContexts.Remove(context.Id);
                DurableTaskEvents.EmitExecutionStopped(GrainContext, context.Id);
            }
        }
    }

    private sealed record RunningRequest(
        Task Task,
        GrainDurableTaskContext Context);

    /// <summary>
    /// Returns whether an attempt's task was deleted with its execution tree, which unregisters the attempt's context,
    /// so the attempt's result is discarded.
    /// </summary>
    private bool IsUnregistered(GrainDurableTaskContext context) =>
        !_executionContexts.TryGetValue(context.Id, out var registered) || !ReferenceEquals(registered, context);

    /// <remarks>
    /// Journaling commits every staged change in the next write, whichever operation requests it, so a body's state
    /// changes and its result commit together only when both are staged in one work item. The attempt commits the staged
    /// response once the body's run returns it.
    /// </remarks>
    void IDurableTaskGrainRuntime.StageBodyResponse(GrainDurableTaskContext context, DurableTaskResponse response)
    {
        Debug.Assert(!context.IsAttemptCanceled && !_stopping && !IsUnregistered(context), "Only a running attempt stages its response.");
        try
        {
            // The handle completes when a write commits the response, as it does for a response which the attempt commits.
            _pendingHandleResponses[context.Id] = StageResponse(context.Id, response);
        }
        catch (Exception exception)
        {
            // This runs from the body's continuation, which has no caller to observe a failure. The attempt stages the
            // response again once the body's run returns it.
            _shared.Logger.LogWarning(exception, "{Id} error staging the response of durable task '{TaskId}'.", GrainId, context.Id);
        }
    }

    /// <summary>
    /// The cancellation which a committed write propagates: task cancellation, carrying <see cref="CancellationReason"/>,
    /// of running attempts, and the cancellation of leaves such as delays. <see cref="Completion"/>, when supplied,
    /// completes once the propagation has run.
    /// </summary>
    private sealed record CancellationPropagation(
        List<GrainDurableTaskContext> Contexts,
        List<IScheduledTaskHandle> Handles,
        string? CancellationReason = null,
        TaskCompletionSource? Completion = null);

    private async Task SetResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            return;
        }

        await CommitResponseAsync(taskId, response, cancellationToken);
    }

    /// <summary>
    /// Stages a terminal response with its completion notifications, commits them, and completes the local handle.
    /// </summary>
    /// <returns>The first terminal response, which wins over a later one.</returns>
    private async ValueTask<DurableTaskResponse> CommitResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken)
    {
        var stored = _storage.TryGetTask(taskId, out var state) && state.Result is { IsCompleted: true };
        var winningResponse = StageResponse(taskId, response);
        if (!stored)
        {
            // The handle completes when a write commits the response: this write, or a later one when this write fails
            // before it commits, because a failed preparation retains staged changes.
            _pendingHandleResponses[taskId] = winningResponse;
        }
        else if (!_pendingHandleResponses.ContainsKey(taskId))
        {
            // A stored result without a pending handle completion is committed: a write which carried it, such as one
            // which committed the result that the body's final code staged, completed the handle already. A pending one
            // completes with the write which captured it, so the entry stays as that write captured it.
            CompleteTaskHandle(taskId, winningResponse);
        }

        await WriteStateAsync(cancellationToken);
        if (DeleteExpiredTasks())
        {
            await WriteStateAsync(cancellationToken);
        }

        return winningResponse;
    }

    /// <summary>
    /// Stages a terminal response and its completion notifications, unless a terminal response is already staged.
    /// </summary>
    /// <returns>The first terminal response, which wins over a later one.</returns>
    private DurableTaskResponse StageResponse(TaskId taskId, DurableTaskResponse response)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can complete a task.");
        }

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} task {TaskId} completed with result '{Result}'.", GrainId, taskId, response);
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
        }

        // Never overwrite a terminal result: a staged result commits, and changing it could change an observed result.
        if (state.Result is { IsCompleted: true } terminal)
        {
            return terminal;
        }

        // The outbox reads each completion from the stored result, so the result is staged first. A subscriber on this
        // grain retires as it receives the result, so the destinations are copied first.
        StoreResult(taskId, state, response);
        foreach (var destination in state.CompletionDestinations.ToArray())
        {
            SendCompletion(taskId, destination);
        }

        return response;
    }

    /// <summary>
    /// Stores a task's terminal result. Without completion destinations, the result's retention period starts now.
    /// </summary>
    /// <param name="subscribed">
    /// Whether the result of a subscription child is the subscribed task's outcome. A subscription child which ends
    /// otherwise, such as a canceled or aborted one, ends its subscription.
    /// </param>
    private void StoreResult(TaskId taskId, IDurableTaskState state, DurableTaskResponse response, bool subscribed = false)
    {
        SetResponse(taskId, state, response);
        if (!subscribed && TryGetSubscription(state, out var target, out var subscribedTaskId))
        {
            Unsubscribe(target, subscribedTaskId, taskId);
        }

        if (state.CompletionDestinations.Count == 0)
        {
            TrackExpiry(GetRetentionExpiry(state));
        }
    }

    private void QueueBackgroundWrite()
    {
        TrackBackgroundTask(
            PersistBackgroundChangesAsync(),
            "persisting deferred durable task cleanup");
    }

    /// <summary>Tracks background work on the activation, which deactivation drains, and logs its failure.</summary>
    private void TrackBackgroundTask(Task task, string operation)
    {
        var id = ++_backgroundWriteId;
        var observed = ObserveBackgroundTaskAsync(id, task, operation);
        if (!observed.IsCompleted)
        {
            _backgroundTasks[id] = observed;
        }
    }

    private async Task ObserveBackgroundTaskAsync(long id, Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _shared.Logger.LogWarning(
                exception,
                "{Id} error {Operation}.",
                GrainId,
                operation);
        }
        finally
        {
            _backgroundTasks.Remove(id);
        }
    }

    private async Task PersistBackgroundChangesAsync()
    {
        await Task.Yield();
        await WriteStateAsync(CancellationToken.None);
    }


    /// <summary>Marks the attempts whose parked waits <paramref name="awaited"/> releases as runnable.</summary>
    private void NotifyParkedAttempts(TaskId awaited)
    {
        foreach (var context in _executionContexts.Values)
        {
            context.OnAwaitedCompleted(awaited);
        }
    }

    private void CompleteTaskHandle(TaskId taskId, DurableTaskResponse response)
    {
        NotifyParkedAttempts(taskId);
        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is TaskHandle localHandle)
        {
            localHandle.TrySetResponse(response);
        }

        CompleteSignalWaits(taskId);
    }

    /// <summary>
    /// Deletes the execution trees whose retention has ended and the cancellation records which can no longer match an
    /// invocation, once the earliest expiry has passed.
    /// </summary>
    /// <returns><see langword="true"/> if a deletion was staged.</returns>
    /// <remarks>
    /// The check costs nothing until the earliest expiry passes; the scan which follows recomputes it. A task with a
    /// pending completion has no expiry: the completion's retirement deletes the task or starts its retention.
    /// </remarks>
    private bool DeleteExpiredTasks()
    {
        var now = UtcNow;
        if (now < _nextExpiry)
        {
            return false;
        }

        var nextExpiry = DateTimeOffset.MaxValue;
        List<(TaskId Id, IDurableTaskState State)>? expired = null;
        foreach (var (taskId, state) in _storage.Tasks)
        {
            if (state.CompletionDestinations.Count > 0)
            {
                continue;
            }

            var isCancellationRecord = IsCancellationRecord(state);
            if ((!state.CompletedAt.HasValue && !isCancellationRecord)
                || (!IsInvoked(state) && HasStoredAncestor(taskId)))
            {
                // A pending task has no expiry, and a task in another task's execution tree is deleted with it.
                continue;
            }

            var expiry = state.CompletedAt.HasValue ? GetRetentionExpiry(state) : DateTimeOffset.MinValue;
            if (isCancellationRecord)
            {
                // The invocation which the cancellation precedes is admitted within the admission window, or never.
                var recordExpiry = AddSaturating(state.CreatedAt, _shared.InvocationAdmissionWindow);
                expiry = recordExpiry > expiry ? recordExpiry : expiry;
            }
            else if (IsSignalRecord(state))
            {
                // A signal record without its task preceded the invocation, which is admitted within the admission window,
                // or never.
                expiry = GetEarlySignalExpiry(state);
            }

            if (expiry <= now)
            {
                if (IsSignalRecord(state))
                {
                    _shared.Logger.LogWarning(
                        "{Id} discarding a signal for durable task '{TaskId}', whose invocation wasn't admitted within the invocation admission window.",
                        GrainId,
                        taskId.Parent());
                }

                (expired ??= []).Add((taskId, state));
            }
            else if (expiry < nextExpiry)
            {
                nextExpiry = expiry;
            }
        }

        _nextExpiry = nextExpiry;
        if (expired is null)
        {
            return false;
        }

        // An expired record can lie in another expired tree, whose deletion removed it already.
        foreach (var (taskId, state) in expired)
        {
            if (_storage.TryGetTask(taskId, out _))
            {
                DeleteTree(taskId, state);
            }
        }

        return true;
    }

    /// <summary>Returns when a terminal result's retention ends.</summary>
    /// <remarks>Retention starts at completion, or later when a dead-lettered completion restarted it.</remarks>
    private DateTimeOffset GetRetentionExpiry(IDurableTaskState state)
    {
        var completedAt = state.CompletedAt!.Value;
        var retainedSince = state.RetainedSince is { } restarted && restarted > completedAt ? restarted : completedAt;
        return AddSaturating(retainedSince, _shared.Options.ResultRetentionPeriod);
    }

    private static DateTimeOffset AddSaturating(DateTimeOffset time, TimeSpan duration) =>
        duration >= DateTimeOffset.MaxValue - time ? DateTimeOffset.MaxValue : time + duration;

    private void TrackExpiry(DateTimeOffset expiry)
    {
        if (expiry < _nextExpiry)
        {
            _nextExpiry = expiry;
        }
    }

    /// <summary>Returns whether a task was scheduled as a request, so it owns its execution tree.</summary>
    /// <remarks>
    /// Completion decisions, records of detached tasks, signal records, signal send markers, and effect records have
    /// fingerprints which begin with <c>$</c>, but they are part of their parent's tree; request fingerprints are
    /// hexadecimal.
    /// </remarks>
    private static bool IsInvoked(IDurableTaskState state) =>
        state.RequestFingerprint is { } fingerprint
        && !fingerprint.StartsWith('$');

    /// <summary>Returns whether a task records only a cancellation which arrived before its invocation.</summary>
    private static bool IsCancellationRecord(IDurableTaskState state) =>
        state.CancellationRequestedAt.HasValue
        && state.RequestFingerprint is null
        && state.RemoteRequestFingerprint is null
        && state.DueTime is null
        && state.Request is null;

    private bool HasStoredAncestor(TaskId taskId)
    {
        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (_storage.TryGetTask(ancestorId, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes a task which owns its lifecycle, together with its execution tree: its descendants which are not invoked
    /// tasks themselves.
    /// </summary>
    /// <remarks>
    /// Costs time proportional to the size of the tree. Each non-terminal task in the tree is canceled: a pending remote
    /// child's cancellation is staged with the deletion, and a running attempt is canceled and its result discarded.
    /// </remarks>
    private void DeleteTree(TaskId taskId, IDurableTaskState state)
    {
        Debug.Assert(state.CompletionDestinations.Count == 0, "A pending completion retains its task's result.");
        DeleteTreeCore(taskId, state);
    }

    private void DeleteTreeCore(TaskId taskId, IDurableTaskState state)
    {
        foreach (var (childId, child) in _storage.GetChildren(taskId).ToList())
        {
            if (!IsInvoked(child))
            {
                DeleteTreeCore(childId, child);
            }
        }

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} deleting task {TaskId}", GrainId, taskId);
        }

        var terminal = state.CompletedAt.HasValue;
        if (!terminal && !state.RemoteTarget.IsDefault)
        {
            StageCancellation(taskId, state.RemoteTarget, state.CancellationReason);
            _preStagedCancellations.Remove((taskId, state.RemoteTarget));
        }
        else if (!terminal && TryGetSubscription(state, out var target, out var subscribedTaskId))
        {
            Unsubscribe(target, subscribedTaskId, taskId);
        }

        if (_executionContexts.Remove(taskId, out var context))
        {
            CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskDeleted);
        }

        if (_runningRequests.Remove(taskId, out var running))
        {
            // Deactivation waits for the canceled attempt, which drains.
            TrackBackgroundTask(running.Context.Settled, "running a deleted durable task");
            DurableTaskEvents.EmitExecutionStopped(GrainContext, taskId);
        }

        if (_taskHandles.Remove(taskId, out var handle))
        {
            if (handle is TaskHandle { ResponseTask.IsCompleted: false } localHandle)
            {
                localHandle.TrySetResponse(terminal ? state.Result! : DurableTaskResponse.Canceled);
                NotifyParkedAttempts(taskId);
            }
        }

        _pendingStarts.Remove(taskId);
        _pendingHandleResponses.Remove(taskId);
        _logicalTimes.Remove(taskId);
        RemoveTask(taskId);
        SignalAcceptance(taskId);
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        // Retention ends at a deadline, which can pass while the grain is otherwise idle.
        if (DeleteExpiredTasks())
        {
            await WriteStateAsync(cancellationToken);
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new DurableTaskNotFoundException(taskId);
        }

        var handle = GetScheduledTaskHandle(taskId);
        var response = await handle.PollAsync(new PollingOptions { PollTimeout = options.PollTimeout }, cancellationToken);
        if (response.IsCompleted)
        {
            return response;
        }

        return DurableTaskResponse.Pending;
    }

    async IAsyncEnumerable<(TaskId TaskId, DurableTaskDiagnosticState State)> IDurableTaskGrainExtension.GetTasksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        await Task.CompletedTask;

        foreach (var (taskId, taskState) in _storage.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetDiagnosticState(taskId, taskState);

            yield return (taskId, state);
        }

        static DurableTaskDiagnosticState GetDiagnosticState(TaskId taskId, IDurableTaskState taskState)
        {
            var (status, response) = DescribeResult(taskState);
            return new DurableTaskDiagnosticState
            {
                CompletedAt = taskState.CompletedAt,
                CreatedAt = taskState.CreatedAt,
                DueTime = taskState.DueTime,
                RemoteTarget = taskState.RemoteTarget.IsDefault ? null : taskState.RemoteTarget.ToString(),
                Response = response,
                Request = DescribeRequest(taskState),
                Status = status,
                Waiters = [.. taskState.CompletionDestinations.Select(destination => DescribeSubscriber(taskId, destination))],
            };
        }

        // Diagnostics decode stored payloads, so a payload which no longer decodes is reported instead of thrown. A
        // canceling or canceled task reports its cancellation reason.
        static (string Status, string? Response) DescribeResult(IDurableTaskState taskState)
        {
            if (!HasStoredResult(taskState))
            {
                return taskState switch
                {
                    { CancellationRequestedAt: not null } => ("Canceling", taskState.CancellationReason),
                    { DueTime: not null } => ("Delayed", null),
                    _ => ("Pending", null),
                };
            }

            try
            {
                var result = taskState.Result!;
                return result.Status switch
                {
                    DurableTaskStatus.CompletedSuccessfully => ("Completed", result.Result?.ToString()),
                    DurableTaskStatus.Canceled => ("Canceled", taskState.CancellationReason),
                    _ => ("Faulted", DescribeException(result.Exception)),
                };
            }
            catch (Exception exception)
            {
                return ("Unreadable", DescribeException(exception));
            }
        }

        static string? DescribeRequest(IDurableTaskState taskState)
        {
            if (TryGetSubscription(taskState, out var target, out var subscribedTaskId))
            {
                return DescribeSubscription(target, subscribedTaskId);
            }

            try
            {
                return taskState.Request?.ToMethodCallString();
            }
            catch (Exception exception)
            {
                return $"Unreadable request: {DescribeException(exception)}";
            }
        }

        static string? DescribeException(Exception? exception) =>
            exception is null ? null : $"{exception.GetType().Name}: {exception.Message}";
    }

    async IAsyncEnumerable<TaskId> IDurableTaskGrainExtension.GetRunningTasksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        await Task.CompletedTask;
        var running = _runningRequests.Keys.ToList();

        foreach (var taskId in running)
        {
            yield return taskId;
        }
    }

    public ValueTask SignalCancellationAsync(TaskId taskId, CancellationToken cancellationToken) =>
        SignalCancellationAsync(taskId, callerId: default, reason: null, cancellationToken);

    /// <summary>
    /// Stages a cancellation received through the inbox, propagating it after the inbox batch commits.
    /// </summary>
    internal void SignalCancellationFromInbox(TaskId taskId, GrainId callerId, string? reason)
    {
        var propagation = StageCancellationTree(taskId, callerId, reason);
        if (propagation.Contexts.Count > 0 || propagation.Handles.Count > 0)
        {
            _pendingCancellationPropagations[++_cancellationPropagationId] =
                new(propagation.Contexts, propagation.Handles, CancellationReason: reason);
        }
    }

    private async ValueTask SignalCancellationAsync(
        TaskId taskId,
        GrainId callerId,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Cancellation is persisted before it propagates.
        var propagation = StageCancellationTree(taskId, callerId, reason);
        if (!propagation.Changed)
        {
            return;
        }

        var propagated = StagePropagation(propagation.Contexts, propagation.Handles, reason);
        await WriteStateAsync(cancellationToken);
        await propagated.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stages the propagation of a cancellation to the attempts and leaves which it reaches, which the
    /// write that commits the cancellation runs, and returns its completion.
    /// </summary>
    private Task StagePropagation(List<GrainDurableTaskContext> contexts, List<IScheduledTaskHandle> handles, string? reason)
    {
        if (contexts.Count == 0 && handles.Count == 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCancellationPropagations[++_cancellationPropagationId] =
            new(contexts, handles, CancellationReason: reason, Completion: completion);
        return completion.Task;
    }

    /// <remarks>
    /// Each task which the request newly reaches records <paramref name="reason"/>, so every execution and handle which
    /// the request collects is canceled carrying that reason.
    /// </remarks>
    private (bool Changed, List<GrainDurableTaskContext> Contexts, List<IScheduledTaskHandle> Handles) StageCancellationTree(
        TaskId taskId,
        GrainId callerId,
        string? reason)
    {
        ThrowIfStopping();
        if (taskId.IsDefault)
        {
            throw new ArgumentException("Invalid TaskId.", nameof(taskId));
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            // The cancellation can precede its invocation, which this record cancels on arrival.
            taskState = GetOrCreateTask(taskId, request: null);
            StageCaller(taskId, taskState, callerId);
            _storage.RequestCancellation(taskId, taskState, reason);
            TrackExpiry(AddSaturating(taskState.CreatedAt, _shared.InvocationAdmissionWindow));
            return (true, [], []);
        }

        ValidateCaller(taskId, taskState, callerId);
        StageCaller(taskId, taskState, callerId);
        List<GrainDurableTaskContext> canceledContexts = [];
        List<IScheduledTaskHandle> canceledHandles = [];
        var changed = RequestCancellationCore(taskId, taskState, canceledContexts, canceledHandles);
        return (changed, canceledContexts, canceledHandles);

        bool RequestCancellationCore(
            TaskId currentTaskId,
            IDurableTaskState currentTaskState,
            List<GrainDurableTaskContext> contexts,
            List<IScheduledTaskHandle> handles)
        {
            if (currentTaskState.CompletedAt.HasValue)
            {
                return false;
            }

            if (currentTaskState.CancellationRequestedAt.HasValue)
            {
                return false;
            }

            foreach (var (childTaskId, childTaskState) in _storage.GetChildren(currentTaskId).ToList())
            {
                // A named combinator's inputs are this task's nearest stored descendants beneath the combinator's ID.
                Debug.Assert(currentTaskId.IsAncestorOf(childTaskId));

                // A shielded child isn't reached by its ancestors' cancellation;
                // its own cancellation, an abort of the tree, and deletion of the tree still reach it.
                if (!childTaskState.Shielded)
                {
                    _ = RequestCancellationCore(childTaskId, childTaskState, contexts, handles);
                }
            }

            _storage.RequestCancellation(currentTaskId, currentTaskState, reason);
            if (TryGetExecutionContext(currentTaskId, out var context))
            {
                if (currentTaskState.Request is IDurableTaskRequest request
                    && request.Context is { } requestContext
                    && requestContext.TargetId != GrainId)
                {
                    StageCancellation(currentTaskId, requestContext.TargetId, reason);
                    _preStagedCancellations.Add((currentTaskId, requestContext.TargetId));
                }

                contexts.Add(context);
            }
            else if (!currentTaskState.RemoteTarget.IsDefault)
            {
                StageCancellation(currentTaskId, currentTaskState.RemoteTarget, reason);
            }
            else if (TryGetScheduledTaskHandle(currentTaskId, out var handle))
            {
                handles.Add(handle);
            }

            return true;
        }
    }

    ValueTask IDurableTaskServer.CancelAsync(TaskId taskId, CancellationToken cancellationToken) =>
        SignalCancellationAsync(taskId, callerId: default, reason: null, cancellationToken);

    ValueTask IDurableTaskServer.CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken) =>
        SignalCancellationAsync(taskId, callerId: default, reason, cancellationToken);

    ValueTask IDurableTaskServer.AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken) =>
        AbortTaskAsync(taskId, reason, cancellationToken);

    ValueTask IDurableTaskManager.AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken) =>
        AbortTaskAsync(taskId, reason, cancellationToken);

    /// <summary>
    /// Aborts a task and its execution tree: the write which commits each unfinished task in the tree as failed with
    /// <see cref="DurableTaskAbortedException"/> also stages an abort to each pending call to another grain, and each
    /// running attempt in the tree is canceled and its result discarded.
    /// </summary>
    /// <remarks>A terminal task, or a task which this grain doesn't retain, is unchanged.</remarks>
    internal async ValueTask AbortTaskAsync(TaskId taskId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (StageAbort(taskId, reason, callerId: default))
        {
            await WriteStateAsync(cancellationToken);
        }
    }

    /// <summary>Stages an abort received through the inbox, canceling the aborted attempts.</summary>
    /// <remarks>
    /// An abort can precede its invocation, like a cancellation; it is then recorded as a cancellation, so the invocation
    /// terminalizes when it arrives. The caller has already terminalized its own record of the task.
    /// </remarks>
    internal void AbortFromInbox(TaskId taskId, string reason, GrainId callerId)
    {
        if (!_storage.TryGetTask(taskId, out _))
        {
            SignalCancellationFromInbox(taskId, callerId, reason: null);
            return;
        }

        StageAbort(taskId, reason, callerId);
    }

    /// <returns><see langword="true"/> if the abort staged changes.</returns>
    private bool StageAbort(TaskId taskId, string reason, GrainId callerId)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state))
        {
            return false;
        }

        ValidateCaller(taskId, state, callerId);
        if (HasStoredResult(state))
        {
            return false;
        }

        // Every task in the tree records the same failure, so one response serves them all.
        var response = DurableTaskResponse.FromException(new DurableTaskAbortedException(reason));
        AbortTreeCore(taskId, state, reason, response);
        return true;
    }

    private void AbortTreeCore(
        TaskId taskId,
        IDurableTaskState state,
        string reason,
        DurableTaskResponse response)
    {
        foreach (var (childId, child) in _storage.GetChildren(taskId).ToList())
        {
            if (!IsInvoked(child))
            {
                AbortTreeCore(childId, child, reason, response);
            }
        }

        if (HasStoredResult(state))
        {
            return;
        }

        if (!state.RemoteTarget.IsDefault)
        {
            MessageTransport.SendAbort(GrainId, state.RemoteTarget, taskId, reason);
        }

        // Unregistering the attempt discards its result; deactivation waits for it to drain.
        if (_executionContexts.Remove(taskId, out var context))
        {
            CancelAttempt(context, DurableTaskAttemptCancellationReason.TaskAborted);
        }

        if (_runningRequests.Remove(taskId, out var running))
        {
            TrackBackgroundTask(running.Context.Settled, "running an aborted durable task");
            DurableTaskEvents.EmitExecutionStopped(GrainContext, taskId);
        }

        _pendingStarts.Remove(taskId);
        _pendingHandleResponses[taskId] = StageResponse(taskId, response);
    }

    // The base constructor of ScheduledTask is internal to the abstractions, so an untyped handle uses an object result.
    ScheduledTask IDurableTaskManager.GetTask(TaskId taskId) => ((IDurableTaskManager)this).GetTask<object?>(taskId);

    // The runtime serves the handle's polls and cancellation directly, as it serves them for a grain call.
    ScheduledTask<TResult> IDurableTaskManager.GetTask<TResult>(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return new AttachedScheduledTask<TResult>(taskId, GrainId, this);
    }

    ValueTask IDurableTaskManager.CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken) =>
        SignalCancellationAsync(taskId, callerId: default, reason, cancellationToken);

    private bool TryGetScheduledTaskHandle(TaskId taskId, [NotNullWhen(true)] out IScheduledTaskHandle? handle)
    {
        if (_taskHandles.TryGetValue(taskId, out var taskHandle))
        {
            handle = taskHandle;
            return true;
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            handle = null;
            return false;
        }

        // A committed result rehydrates as a completed handle; a staged one completes its handle once it commits.
        if (!_pendingHandleResponses.ContainsKey(taskId) && taskState.Result is { } response)
        {
            Debug.Assert(response.IsCompleted);
            handle = new CompletedTaskHandle(taskId, response);
            return true;
        }

        handle = GetOrAddTaskHandle(taskId, taskState.RemoteTarget);
        return true;
    }

    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId)
    {
        ThrowIfStopping();
        if (!TryGetScheduledTaskHandle(taskId, out var handle))
        {
            throw new KeyNotFoundException($"A task with the identifier '{taskId}' was not found.");
        }

        return handle;
    }

    /// <remarks>
    /// The record of a subscription or a detached start names the task which it observes and that task's grain, which
    /// other parties address. A call to a grain reaches the task which the target stores under this grain's scope, and
    /// retains until this grain accepts its completion. The identifier of any other operation, or of one which recorded
    /// nothing, such as a start which an earlier cancellation prevented, is its own.
    /// </remarks>
    public TaskId GetTargetTaskId(TaskId taskId, out GrainId addressedHost)
    {
        addressedHost = default;
        if (!_storage.TryGetTask(taskId, out var state))
        {
            return taskId;
        }

        if (TryGetSubscription(state, out var target, out var observedId))
        {
            addressedHost = target;
            return observedId;
        }

        return state.RemoteTarget.IsDefault ? taskId : CallerScope.GetTaskId(GrainId, taskId);
    }

    public DateTimeOffset? GetCompletedAt(TaskId taskId) =>
        _storage.TryGetTask(taskId, out var state) ? state.CompletedAt : null;

    public void OnReplayEnded(TaskId executionId) => DurableTaskEvents.EmitReplayEnded(GrainContext, executionId);

    /// <remarks>
    /// An execution's recorded operations are its nearest stored descendants: descendants which are themselves executions,
    /// such as local steps, hold their own operations. Signal values belong to the invoked task, which senders deliver
    /// independently of its attempts; invoked descendants keep their own lifecycle; a cancellation which arrived
    /// before its request records no operation of this execution; and version decisions record which code the execution
    /// follows rather than an operation which it reaches, so a deployment which removes a version check leaves no
    /// operation which its replays never reach.
    /// </remarks>
    public HashSet<TaskId>? GetRecordedOperations(TaskId executionId)
    {
        HashSet<TaskId>? operations = null;
        foreach (var (id, state) in _storage.GetChildren(executionId))
        {
            if (!IsInvoked(state) && !IsSignalRecord(state) && !IsCancellationRecord(state) && !IsVersionRecord(state))
            {
                (operations ??= []).Add(id);
            }
        }

        return operations;
    }

    internal Task StopAsync(CancellationToken _) => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        // Every attempt is canceled as the runtime stops, so none records anything once the activation stops.
        _stopping = true;
        foreach (var context in _executionContexts.Values)
        {
            CancelAttempt(context, DurableTaskAttemptCancellationReason.ActivationStopping);
        }

        await _deactivationCts.CancelAsync();

        // Pending tasks continue in the next activation, so a waiter on this activation's handle fails, as a call which
        // arrives while the runtime stops does, instead of observing the stop as the task's outcome.
        var stopping = new DurableTaskAttemptCanceledException(DurableTaskAttemptCancellationReason.ActivationStopping, _deactivationCts.Token);
        foreach (var handle in _taskHandles.Values)
        {
            handle.SetStopping(stopping);
        }

        while (true)
        {
            // A parked attempt is discarded and never resumes, so it settles at once; an attempt whose segment runs settles
            // when the segment ends. Abandoning the drain would allow a replacement activation to overlap this one.
            // Adapter-controlled waits observe the lifecycle token. Arbitrary user code can ignore it, so deactivation
            // deliberately waits for every running segment before replacement replay can begin.
            var running = _runningRequests.Values
                .Select(static request => request.Context.Settled)
                .Where(static settled => !settled.IsCompleted)
                .Concat(_backgroundTasks.Values)
                .ToArray();
            if (running.Length == 0)
            {
                return;
            }

            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private bool IsRequestRunning(TaskId taskId) => _runningRequests.ContainsKey(taskId);

    private static InvalidOperationException CreateUnboundException() => new(
        $"Durable Tasks requires grain activations to derive from Orleans.Journaling.DurableGrain or implement {typeof(Orleans.Messaging.IMessagingGrain).FullName} so the runtime binds to the activation's journal and inbox.");

    private void ThrowIfStopping()
    {
        if (_grainContext is null)
        {
            throw CreateUnboundException();
        }

        if (_stopping)
        {
            throw new DurableTaskAttemptCanceledException(DurableTaskAttemptCancellationReason.ActivationStopping, _deactivationCts.Token);
        }
    }

    /// <summary>
    /// Cancels <paramref name="context"/>'s attempt for <paramref name="reason"/>, and signals the attempt's tokens in a
    /// later work item, which deactivation drains.
    /// </summary>
    /// <remarks>
    /// Deactivation, an abort, and the deletion of a task's tree cancel attempts this way, on the activation's scheduler.
    /// From the cancellation on, the attempt's durable operations throw <see cref="DurableTaskAttemptCanceledException"/>,
    /// so the runtime records none of them, and the attempt's result is discarded. Journaled state changes which its code
    /// makes, such as cleanup, commit with the grain's next journal write. Its code observes the cancellation through its
    /// tokens once they are signaled, outside the runtime's current operation.
    /// </remarks>
    private bool CancelAttempt(GrainDurableTaskContext context, DurableTaskAttemptCancellationReason reason)
    {
        if (!context.CancelAttempt(reason))
        {
            return false;
        }

        TrackBackgroundTask(SignalAttemptCanceledAsync(context), "signaling a canceled durable task attempt");
        return true;

        static async Task SignalAttemptCanceledAsync(GrainDurableTaskContext context)
        {
            await Task.Yield();
            context.SignalAttemptCanceled();
        }
    }
}
