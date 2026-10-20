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
using Orleans.Serialization;

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
/// Completions and resumes which arrive for a deleted task, or for an earlier run of a task's tree, are ignored: each run
/// of a root task ID has its own incarnation, which the identifiers of the messages which name its nodes cover, as
/// <see cref="CallerScope"/> describes. A cancellation or a signal which arrives before its
/// invocation is recorded, and the invocation finds it in its tree. The grain admits an invocation until
/// <see cref="Orleans.Messaging.Configuration.DurableMessagingOptions.DeduplicationWindow"/> has elapsed since its sender
/// first staged it, and dead-letters a later one. A record whose invocation doesn't arrive is deleted after
/// <see cref="DurableTaskGrainRuntimeShared.EarlyRecordRetention"/>, which covers the skew between the sender's clock and
/// the grain's.
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
    IEnumerable<IDurableTaskMessageTransport> messageTransports,
    IJournaledStateManager stateManager) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IDurableTaskManager,
    IStateMachine,
    IReportsStagedChanges,
    IOutboxMessageSource,
    IInboxWorkSource
{
    /// <summary>The journal state machine name of the runtime's commit hook.</summary>
    /// <remarks>
    /// The hook journals no entries of its own: the runtime stages every change in the built-in durable states which hold
    /// its task state and in its tasks' access logs, which report each change, and the effects it runs after a write follow
    /// those changes. It therefore reports its changes (<see cref="IReportsStagedChanges"/>), so Messaging can tell whether a
    /// failed handler attempt staged anything.
    /// </remarks>
    internal const string StateMachineName = "orleans.durable-tasks.runtime";

    // The runtime's state is confined to the activation's scheduler, as grain state is.
    private readonly Dictionary<TaskId, GrainDurableTaskContext> _executionContexts = [];
    private readonly Dictionary<TaskId, RunningRequest> _runningRequests = [];
    private readonly Dictionary<TaskId, TaskHandle> _taskHandles = [];
    private readonly HashSet<TaskId> _pendingStarts = [];
    private readonly Dictionary<TaskId, DurableTaskResponse> _pendingHandleResponses = [];
    private readonly Dictionary<long, CancellationPropagation> _pendingCancellationPropagations = [];
    private readonly Dictionary<long, Task> _backgroundTasks = [];

    // The activation's journaled state manager, whose context provider records the accesses of durable task bodies.
    private readonly IJournaledStateManager _stateManager = stateManager;

    // The storage of the access logs which record the accesses of durable task bodies, bound by InitializeForActivation.
    private IDurableTaskAccessLogStorage? _accessLogStorage;
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

    /// <summary>
    /// Binds the runtime to <paramref name="grainContext"/>, the activation whose executions it runs: it records the
    /// accesses of durable task bodies to the activation's journaled state from now on, which every body's replay depends
    /// on.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The task storage doesn't store access logs, the journaled state manager doesn't route its states' accesses through
    /// a host's context, or another host's context already routes them.
    /// </exception>
    internal void InitializeForActivation(IGrainContext grainContext)
    {
        if (_storage is not IDurableTaskAccessLogStorage accessLogStorage)
        {
            throw new InvalidOperationException(
                $"Durable task storage '{_storage.GetType()}' doesn't store the access logs which record durable task bodies' accesses to journaled state, so a replay would apply them again. Configure the journaled durable task storage which AddDurableTasks registers.");
        }

        if (_stateManager is not IJournaledStateContextHost host)
        {
            throw new InvalidOperationException(
                $"Journaled state manager '{_stateManager.GetType()}' doesn't route its states' accesses through a host's context, so durable task bodies' accesses to journaled state can't be recorded. Use the journaled state manager which Orleans.Journaling registers.");
        }

        if (host.ContextProvider is { } installed and not DurableTaskAccessContextProvider)
        {
            throw new InvalidOperationException(
                $"The accesses to the activation's journaled state already run through '{installed.GetType()}', so durable task bodies' accesses can't be recorded.");
        }

        host.ContextProvider = new DurableTaskAccessContextProvider(this);
        _accessLogStorage = accessLogStorage;
        _grainContext = grainContext;
    }

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
            if (decision.HasRequest
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
            decision = GetOrCreateTask(decisionId, DurableTaskRecordKind.Selection, request: null);
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

                decision = GetOrCreateTask(decisionId, DurableTaskRecordKind.Selection, request: null);
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
        $"{message} It records {DescribeOperation(recorded)}, and this attempt requested {requested}. A replay schedules the same operation under each identifier: a call or request invokes the same method on the same target, whatever its arguments; new workflow steps use new identifiers.";

    private static string DescribeOperation(IDurableTaskState state) => state switch
    {
        { RemoteTarget.IsDefault: false } => DescribeCall(state.RemoteTarget, state.Method, state.RemoteRequestFingerprint),
        { DueTime: { } dueTime } => $"a delay due at {dueTime:O}",
        { Kind: DurableTaskRecordKind.Delay } => "a delay",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(CompletionDecisionFingerprintPrefix, StringComparison.Ordinal) => "a selection of the first task to complete",
        _ when TryGetDetachedTask(state, out var target, out var rootId) => DescribeDetachedStart(target, rootId, state.Method),
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalSendFingerprintPrefix, StringComparison.Ordinal) => "a signal's send",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalFingerprintPrefix, StringComparison.Ordinal) => "a signal's value",
        { RequestFingerprint: { } fingerprint } when TryParseIdentity(SubscriptionFingerprintPrefix, fingerprint, out var target, out var subscribed) => DescribeSubscription(target, subscribed),
        { RequestFingerprint: EffectFingerprint } => "a DurableTask.RunInline effect",
        { RequestFingerprint: VersionFingerprint } => "a version decision",
        { Kind: DurableTaskRecordKind.CancellationObserved } => "the point at which the execution observed its cancellation",
        { Method: { } method } => $"a request for {DescribeMethod(method)} ({AbbreviateFingerprint(state.RequestFingerprint)})",
        { RequestFingerprint: { } fingerprint } => $"a request ({AbbreviateFingerprint(fingerprint)})",
        { Kind: DurableTaskRecordKind.EarlyCancellation } => "a cancellation which arrived before its request",
        _ => "a local child task",
    };

    /// <summary>Describes a request for diagnostics: its method, and the fingerprint of its arguments.</summary>
    private string DescribeRequest(IDurableTaskRequest request) =>
        $"a request for {DescribeMethod(IDurableTaskRequest.GetMethod(request))} ({AbbreviateFingerprint(IDurableTaskRequest.GetFingerprint(request, _shared.Serializer))})";

    /// <summary>Describes a call to another grain for diagnostics: its target, its method, and the fingerprint of its arguments.</summary>
    private string DescribeCall(GrainId target, IDurableTaskRequest request) =>
        DescribeCall(target, IDurableTaskRequest.GetMethod(request), IDurableTaskRequest.GetFingerprint(request, _shared.Serializer));

    private static string DescribeCall(GrainId target, string? method, string? fingerprint) =>
        $"a call to {DescribeMethod(method)} on grain '{target}' ({AbbreviateFingerprint(fingerprint)})";

    private static string DescribeMethod(string? method) => method is null ? "an unrecorded method" : $"method '{method}'";

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
    /// An attempt which starts, or replays, after cancellation of its task was requested receives the request here, and
    /// its body observes it at a cancellation point: the one which the execution recorded when an earlier attempt first
    /// observed it, or otherwise the start of a first run, or the point at which a replay ends. From then on, its
    /// <see cref="DurableTaskContext.CancellationToken"/> is canceled,
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
            context.DeliverTaskCancellation(taskState.CancellationReason);
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

        // Storage records the caller of every invocation from another grain, so the check decodes no request.
        var existingCaller = state.CallerId;
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
            message = MessageTransport.CreateInvocation(messageId, invocation.Target, invocation.Payload);
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
        MessageTransport.SendCancellation(GrainId, target, GetWireId(taskId), reason);

    private ValueTask WriteStateAsync(CancellationToken cancellationToken) => _storage.WriteAsync(cancellationToken);

    /// <summary>Returns a task's state, staging the state of a task which doesn't exist.</summary>
    private IDurableTaskState GetOrCreateTask(TaskId taskId, DurableTaskRecordKind kind, IDurableTaskRequest? request)
    {
        var reportCreation = DurableTaskEvents.IsTaskCreatedEnabled && !_storage.TryGetTask(taskId, out _);
        var state = _storage.GetOrCreateTask(taskId, kind, request);
        if (reportCreation)
        {
            DurableTaskEvents.EmitTaskCreated(GrainContext, taskId);
        }

        return state;
    }

    /// <summary>Stages a task's terminal result, and returns the result which it staged.</summary>
    /// <remarks>
    /// Storage serializes the result before it stages anything, and throws when the result doesn't serialize, such as a
    /// value or an exception of a type which has no codec. The task then faults with a <see cref="SerializerException"/>
    /// which names the type, which commits like any result, so the task ends rather than holding a result which no write
    /// can commit.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A result which can't be stored is stored as that failure.")]
    private DurableTaskResponse SetResponse(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        try
        {
            _storage.SetResponse(taskId, state, response);
        }
        catch (Exception exception)
        {
            var (kind, value) = response.Exception is { } failure ? ("exception", (object)failure) : ("result", response.Result);
            _shared.Logger.LogWarning(exception, "{Id} the {Kind} of durable task '{TaskId}' doesn't serialize.", GrainId, kind, taskId);
            response = DurableTaskResponse.FromException(new SerializerException(
                $"The {kind} of durable task '{taskId}', of type '{value?.GetType()}', can't be stored because it doesn't serialize: {exception.GetType().Name}: {exception.Message}"));
            _storage.SetResponse(taskId, state, response);
        }

        DurableTaskEvents.EmitTaskCompleted(GrainContext, taskId, response);
        return response;
    }

    /// <summary>Stages a new terminal record of an execution, such as an effect's outcome or a version decision.</summary>
    private void AddRecord(TaskId taskId, DurableTaskRecordKind kind, string fingerprint, DurableTaskResponse response)
    {
        _storage.AddRecord(taskId, kind, fingerprint, response);
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

        var method = IDurableTaskRequest.GetMethod(request);
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (state.HasRequest
                || state.RequestFingerprint is not null
                || !state.CallerId.IsDefault
                || state.DueTime is not null
                || (!state.RemoteTarget.IsDefault && state.RemoteTarget != context.TargetId)
                || (state.Method is { } recordedMethod && !string.Equals(recordedMethod, method, StringComparison.Ordinal)))
            {
                throw CreateDivergenceException(
                    $"Durable task '{taskId}' is already associated with a different request.",
                    state,
                    DescribeCall(context.TargetId, request));
            }

            if (ReadRecordedResult(taskId, state) is { IsCompleted: true } completed)
            {
                return completed;
            }
        }

        state ??= GetOrCreateTask(taskId, DurableTaskRecordKind.RemoteCall, request: null);
        MarkShielded(taskId, state, shielded);
        if (state.RemoteTarget.IsDefault || state.Method is null)
        {
            _storage.SetRemoteRequest(taskId, state, context.TargetId, method, IDurableTaskRequest.GetFingerprint(request, _shared.Serializer));
        }

        context.CallerId = GrainId;
        context.SupportsDurableCompletion = true;

        // A recorded acceptance shows that the invocation retired, and the target holds the task.
        if (state.Accepted is null)
        {
            var wireId = GetWireId(taskId);
            SendInvocation(transport.CreateInvocation(GrainId, context.TargetId, wireId, request), request, recordId: wireId);
        }

        return DurableTaskResponse.Pending;
    }

    // Fingerprints of operations in an execution tree begin with '$', so they never equal a hexadecimal request fingerprint.
    private const string EffectFingerprint = "$effect";

    private static bool IsEffectRecord(IDurableTaskState state) => state.Kind == DurableTaskRecordKind.Effect;

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
        if (TryGetEffectOutcome(effectId, "a DurableTask.RunInline effect") is { } outcome)
        {
            return outcome;
        }

        var response = effect(state);
        try
        {
            AddRecord(effectId, DurableTaskRecordKind.Effect, EffectFingerprint, response);
        }
        catch (Exception exception) when (response.Exception is null)
        {
            response = DurableTaskResponse.FromException(exception);
            AddRecord(effectId, DurableTaskRecordKind.Effect, EffectFingerprint, response);
        }

        return response;
    }

    /// <summary>
    /// Runs <paramref name="body"/> as a transaction over the grain's journaled state, whose commit stages a record of the
    /// result under <paramref name="effectId"/> together with the transaction's changes, and returns once a journal write
    /// holds the commit.
    /// </summary>
    /// <remarks>
    /// A body which fails, a nested transaction whose failure dooms it, or a result which doesn't serialize aborts the
    /// transaction, which leaves no trace, and <paramref name="onAbort"/> records the failure in its place. A commit whose
    /// write fails fails the journal, so the next activation runs the body again.
    /// </remarks>
    Task<T> IDurableTaskGrainRuntime.RunTransactionAsync<T>(
        TaskId effectId,
        Func<CancellationToken, Task<T>> body,
        Func<bool> stagesRecord,
        Action<Exception> onAbort,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var manager = _stateManager as JournaledStateManager ?? throw new NotSupportedException("Durable transactions require the journaled state manager which Orleans.Journaling registers.");
        return manager.RunTransactionAsync(
            body,
            onCommit: result =>
            {
                if (stagesRecord())
                {
                    AddRecord(effectId, DurableTaskRecordKind.Effect, EffectFingerprint, DurableTaskResponse.FromResult(result));
                }
            },
            onAbort,
            cancellationToken);
    }

    /// <summary>Stages a record of an effect's outcome under <paramref name="effectId"/>, without writing.</summary>
    void IDurableTaskGrainRuntime.RecordEffect(TaskId effectId, DurableTaskResponse response)
    {
        ThrowIfStopping();
        AddRecord(effectId, DurableTaskRecordKind.Effect, EffectFingerprint, response);
    }

    /// <summary>Returns the outcome which an effect recorded under <paramref name="effectId"/>, or <see langword="null"/>.</summary>
    /// <param name="effectId">The effect's identifier.</param>
    /// <param name="requested">A description of the requested operation, for a divergence.</param>
    /// <exception cref="DurableTaskDivergenceException">Another kind of operation is recorded under the identifier.</exception>
    DurableTaskResponse? IDurableTaskGrainRuntime.TryGetEffectOutcome(TaskId effectId, string requested)
    {
        ThrowIfStopping();
        return TryGetEffectOutcome(effectId, requested);
    }

    private DurableTaskResponse? TryGetEffectOutcome(TaskId effectId, string requested)
    {
        if (_storage.TryGetTask(effectId, out var record))
        {
            if (!IsEffectRecord(record) || ReadRecordedResult(effectId, record) is not { IsCompleted: true } outcome)
            {
                throw CreateDivergenceException(
                    $"Durable child task '{effectId}' is already associated with a different operation.",
                    record,
                    requested);
            }

            return outcome;
        }

        // A named combinator records only its inputs, beneath its identifier.
        if (_storage.GetChildren(effectId).FirstOrDefault() is (var inputId, { } input))
        {
            throw CreateDivergenceException(
                $"Durable child task '{effectId}' holds '{inputId}', which an earlier run recorded.",
                input,
                requested);
        }

        return null;
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
                    IsVersionRecord(recorded) && decision is { IsCompleted: true },
                    "Only version decisions are recorded under version segments.");
                return decision!.GetResult<int>();
            }

            if (_storage.TryGetTask(execution, out var state) && IsInvoked(state))
            {
                break;
            }
        }

        var recordId = executionId.Child(segment);
        AddRecord(recordId, DurableTaskRecordKind.Version, VersionFingerprint, DurableTaskResponse.FromResult(version));
        DurableTaskEvents.EmitVersionRecorded(GrainContext, executionId, name, version);
        return version;
    }

    private static bool IsVersionRecord(IDurableTaskState state) => state.Kind == DurableTaskRecordKind.Version;

    /// <summary>The segment of the record which holds the cancellation point at which an execution observed its cancellation.</summary>
    internal const string CancellationPointSegment = "$cancellation-observed";

    private const string CancellationPointFingerprint = "$cancellation-observed";

    /// <remarks>An earlier attempt's delivery records the point, and later attempts read it.</remarks>
    public int? GetCancellationPoint(TaskId executionId)
    {
        if (!_storage.TryGetTask(executionId.Child(CancellationPointSegment), out var record))
        {
            return null;
        }

        Debug.Assert(
            IsCancellationPointRecord(record) && record.Result is { IsCompleted: true },
            "Only cancellation points are recorded under the cancellation point segment.");
        return record.Result!.GetResult<int>();
    }

    /// <remarks>
    /// The record is a terminal entry of the execution's tree, staged in the calling work item, so it commits with the
    /// grain's next journal write, and is deleted with the tree. A replay which starts before that write commits it
    /// delivers the cancellation as an execution without a record does.
    /// </remarks>
    public void RecordCancellationPoint(TaskId executionId, int point)
    {
        Debug.Assert(!_stopping, "A stopping activation's attempts are canceled, so none observes its task's cancellation.");
        var recordId = executionId.Child(CancellationPointSegment);
        Debug.Assert(!_storage.TryGetTask(recordId, out _), "The first observation of an execution's cancellation records its point.");
        AddRecord(recordId, DurableTaskRecordKind.CancellationObserved, CancellationPointFingerprint, DurableTaskResponse.FromResult(point));
    }

    private static bool IsCancellationPointRecord(IDurableTaskState state) => state.Kind == DurableTaskRecordKind.CancellationObserved;

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

        // The cancellation and its message commit together; the outbox delivers the message only after that commit, and
        // retries it under the admission cutoff of that send. A recorded request, such as one which a replay repeats,
        // therefore stages nothing: sending the identity again after the outbox retired it would be a new send, which a
        // receiver that processed the first rejects for its different cutoff.
        if (!state.CancellationRequestedAt.HasValue)
        {
            _storage.RequestCancellation(taskId, state, reason);
            StageCancellation(taskId, target, reason);
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
            // A delay replays only from a delay's record, or from the record of the invoked task whose body is the delay:
            // another operation's outcome or state is never the delay's.
            if (state.Kind != DurableTaskRecordKind.Delay
                && !(state.Kind == DurableTaskRecordKind.Invocation && TryGetExecutionContext(taskId, out _)))
            {
                throw CreateDivergenceException(
                    $"Durable delay '{taskId}' is already associated with another operation.",
                    state,
                    $"a delay due at {dueTime:O}");
            }

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

        state ??= GetOrCreateTask(taskId, DurableTaskRecordKind.Delay, request: null);
        var generation = checked(state.ResumeGeneration + 1);
        _storage.SetDelay(taskId, state, dueTime, generation);

        // The grain's own inbox accepts the resume in the same journal write as the delay, so every durable delay has a
        // durable resume.
        transport.SendResume(GrainId, taskId, GetIncarnation(taskId), generation, dueTime);
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
    /// <param name="taskId">The delay.</param>
    /// <param name="incarnation">The incarnation of the delay's tree when the resume was scheduled.</param>
    /// <param name="generation">The delay's generation when the resume was scheduled.</param>
    /// <param name="sender">The grain which sent the resume, which is this grain.</param>
    /// <remarks>
    /// A delay whose cancellation was requested terminalizes as canceled. A pending delay of the same incarnation and
    /// generation completes. Resumes for an earlier run of the delay's tree, which a later run's delay with the same
    /// identifier can't tell apart by its generation, for an earlier generation, for a terminal delay, or for a missing
    /// task change nothing.
    /// </remarks>
    internal void ResumeDelayFromInbox(TaskId taskId, Guid incarnation, long generation, GrainId sender)
    {
        ThrowIfStopping();
        if (sender != GrainId)
        {
            throw new InvalidOperationException(
                $"Durable delay '{taskId}' does not accept resumes from grain '{sender}'.");
        }

        if (!_storage.TryGetTask(taskId, out var state)
            || state.Result is { IsCompleted: true }
            || GetIncarnation(taskId) != incarnation)
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
    /// Stages the terminal response of a task of <paramref name="sender"/> which this grain's node awaits: a remote
    /// child's own result, or the result of the existing task <paramref name="sourceTaskId"/> to which a subscription
    /// child subscribed. The completion names the node by its wire identifier, <paramref name="wireId"/>. The local handle
    /// completes after commit.
    /// </summary>
    /// <remarks>
    /// The inbox accepts the completion directly into task state, so this stored response is its only journaled copy.
    /// The sender retains its result until the inbox reports this acceptance to the sender's outbox. A completion for a
    /// node which no longer exists, or which belongs to another run of its tree than the run which the wire identifier
    /// names, is accepted without changing state: the run which awaited it was deleted with its execution tree, which
    /// canceled it, and nothing awaits its result. A subscription which already ended, for example because it was
    /// canceled, also accepts the completion without changes.
    /// </remarks>
    internal void AcceptResponseFromInbox(TaskId wireId, TaskId sourceTaskId, DurableTaskResponse response, GrainId sender)
    {
        ThrowIfStopping();
        if (!TryGetNode(wireId, out var taskId, out var state))
        {
            return;
        }

        ThrowIfNotStartedBy(taskId, wireId, sourceTaskId, state, sender);

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
                && state.HasRequest
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

            if (!state.HasRequest
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

        // Only invocations from other grains store tasks beneath the reserved caller scope root.
        if (!fromInbox)
        {
            CallerScope.ThrowIfReserved(taskId);
        }

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

        var method = IDurableTaskRequest.GetMethod(request);
        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = GetOrCreateTask(taskId, DurableTaskRecordKind.Invocation, request);
            StageCaller(taskId, state, requestContext.CallerId);
            _storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, _shared.Serializer), method);
            _storage.SetIncarnation(taskId, state, Guid.NewGuid());
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
                DescribeRequest(request));
        }

        ValidateCaller(taskId, state, requestContext.CallerId);
        if (!state.HasRequest
            && state.RequestFingerprint is null
            && state.CancellationRequestedAt is null)
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a non-request operation.",
                state,
                DescribeRequest(request));
        }

        // A request is the same operation as the stored task when it invokes the same method for the same caller, which
        // ValidateCaller checked: a retry whose arguments serialize differently reaches the stored task. A record of an
        // execution's own operation, such as an effect, has a fingerprint but invokes no method.
        if (state.Method is { } recordedMethod
                ? !string.Equals(recordedMethod, method, StringComparison.Ordinal)
                : state.RequestFingerprint is not null)
        {
            throw CreateScheduleConflictException(
                taskId,
                $"Durable task '{taskId}' is already associated with a different request.",
                state,
                DescribeRequest(request));
        }

        StageCaller(taskId, state, requestContext.CallerId);
        if (state.Method is null)
        {
            _storage.SetRequestFingerprint(taskId, state, IDurableTaskRequest.GetFingerprint(request, _shared.Serializer), method);
        }

        // A run's incarnation is minted when its invocation is first stored, which a record of an earlier cancellation
        // precedes. That record becomes the invocation's.
        if (state.Kind == DurableTaskRecordKind.EarlyCancellation)
        {
            _storage.SetInvocation(taskId, state);
        }

        if (state.Incarnation == Guid.Empty)
        {
            _storage.SetIncarnation(taskId, state, Guid.NewGuid());
        }

        // An invocation whose cancellation preceded its start terminalizes as canceled. A started body, which a duplicate
        // invocation reaches, decides its own outcome.
        if (state.CancellationRequestedAt.HasValue
            && state.Result is not { IsCompleted: true }
            && IsCancellationLeaf(taskId, state))
        {
            // A cancellation which preceded the invocation recorded no request.
            if (!state.HasRequest)
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

        if (!TryReadRequest(taskId, state, out var committedRequest))
        {
            return;
        }

        var executionContext = CreateExecutionContext(taskId);
        _ = GetOrCreateRunningTaskHandle(taskId);
        committedRequest.SetTarget(GrainContext);
        StartInvocation(taskId, static value => value.CreateTask(), committedRequest, executionContext);
    }

    /// <summary>
    /// Returns the request which <paramref name="taskId"/> stores, or quarantines the task when its request no longer
    /// decodes.
    /// </summary>
    /// <remarks>
    /// A quarantined task stays pending with its stored request unchanged, and stalls: the runtime reports it once per
    /// activation with <see cref="DurableTaskAttemptCancellationReason.Diverged"/> and keeps the grain's slow wake-up, so
    /// a later activation, such as one whose code decodes the request, starts it. The grain's other tasks are unaffected,
    /// and polls report the task as pending. Aborting the task ends it.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any decoding failure quarantines only the task which stores the request.")]
    private bool TryReadRequest(TaskId taskId, IDurableTaskState state, [NotNullWhen(true)] out IDurableTaskRequest? request)
    {
        try
        {
            request = state.Request;
            return request is not null;
        }
        catch (Exception exception)
        {
            request = null;
            if (_stalledExecutions.Add(taskId))
            {
                ReportStall(
                    taskId,
                    DurableTaskAttemptCancellationReason.Diverged,
                    new DurableTaskDivergenceException(
                        $"The request which durable task '{taskId}' stores no longer decodes: {exception.GetType().Name}: {exception.Message} The task stays pending with its stored request, and code which decodes the request runs it.",
                        exception));
            }

            return false;
        }
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
    /// Messaging also consults them while the activation deactivates, until its pump stops. The runtime discards every
    /// attempt as deactivation starts, so none of them is runnable any more, and a wake-up which ran then would end without
    /// a successor, stranding an execution which was runnable when the activation stopped or whose awaited event committed
    /// while it stopped. A stopping runtime therefore reports its pending executions as runnable too. A parked execution
    /// which this keeps awake costs one more activation, whose recovery parks it again.
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
        || ((!_recovered || _stopping) && HasPendingExecutions())
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
            if (!HasStoredResult(state) && state.HasRequest && state.DueTime is null)
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
        ReportStall(context.Id, reason, exception);
    }

    private void ReportStall(TaskId taskId, DurableTaskAttemptCancellationReason reason, Exception exception)
    {
        _shared.Logger.LogWarning(exception, "{Id} durable task '{TaskId}' stalled: {Reason}.", GrainId, taskId, reason);
        DurableTaskEvents.EmitExecutionStalled(GrainContext, taskId, reason, exception);
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
                context.DeliverTaskCancellation(propagation.CancellationReason);
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
            var method = IDurableTaskRequest.GetMethod(remoteRequest);
            if (state is not null)
            {
                if (state.HasRequest
                    || state.RequestFingerprint is not null
                    || !state.CallerId.IsDefault
                    || state.DueTime is not null
                    || (existingScheduledHandle is TaskHandle existingTaskHandle
                        && existingTaskHandle.RemoteTarget.IsDefault)
                    || (!state.RemoteTarget.IsDefault && state.RemoteTarget != target)
                    || (state.Method is { } recordedMethod
                        && !string.Equals(recordedMethod, method, StringComparison.Ordinal))
                    || (handle is not null
                        && (state.RemoteTarget.IsDefault || state.Method is null)))
                {
                    throw CreateDivergenceException(
                        $"Durable child task '{taskId}' is already associated with a different request.",
                        state,
                        DescribeCall(target, remoteRequest));
                }

                if (state.RemoteTarget.IsDefault || state.Method is null)
                {
                    _storage.SetRemoteRequest(taskId, state, target, method, IDurableTaskRequest.GetFingerprint(remoteRequest, _shared.Serializer));
                }
            }
        }
        else if (state is not null && state.Kind != GetLocalKind(durableTask))
        {
            throw CreateDivergenceException(
                $"Durable child task '{taskId}' is already associated with a different request.",
                state,
                GetLocalKind(durableTask) == DurableTaskRecordKind.Delay ? "a delay" : "a local child task");
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
        // runs the step again. A delay is a local child whose body records the delay under the child's own identifier.
        state ??= GetOrCreateTask(taskId, GetLocalKind(durableTask), request: null);
        MarkShielded(taskId, state, shielded);

        var executionContext = CreateExecutionContext(taskId);
        handle = GetOrCreateRunningTaskHandle(taskId);
        StartInvocation(taskId, static task => task, durableTask, executionContext);
        return new(handle, RequiresWrite: false);
    }

    /// <summary>A staged start's handle, and whether the start staged changes which a write commits.</summary>
    private readonly record struct StagedChild(IScheduledTaskHandle Handle, bool RequiresWrite);

    // The definitions which DurableTask.Delay and DurableTask.DelayUntil create, whose bodies record a delay under their
    // child's own identifier.
    private static readonly Type DelayType = DurableTask.Delay(TimeSpan.Zero).GetType();
    private static readonly Type DelayUntilType = DurableTask.DelayUntil(DateTimeOffset.MinValue).GetType();

    /// <summary>Returns the kind of the record of a local child which runs <paramref name="durableTask"/>.</summary>
    private static DurableTaskRecordKind GetLocalKind(DurableTask durableTask) =>
        durableTask.GetType() is var type && (type == DelayType || type == DelayUntilType)
            ? DurableTaskRecordKind.Delay
            : DurableTaskRecordKind.LocalStep;

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
                    context.DeliverTaskCancellation(ancestorState.CancellationReason);
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
            response = await RunAttemptAsync(createTask, state, context);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException && !IsUnregistered(context))
            {
                _shared.Logger.LogWarning(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            }

            response = DurableTaskResponse.FromException(exception);
        }

        context.VerifyCancellationPointReached();
        context.AccessLog?.Close();

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
            if (response.IsCompleted && !context.IsAttemptEnded)
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
            // The abstractions end an attempt whose replay departs from its record, such as a recorded result of another
            // type, without the runtime; the attempt then stalls like one whose operation diverged.
            if (context.AttemptEndReason is DurableTaskAttemptCancellationReason.Diverged or DurableTaskAttemptCancellationReason.UnsupportedVersion
                && _stalledExecutions.Add(context.Id))
            {
                ReportStall(context.Id, context.AttemptEndReason.GetValueOrDefault(), response.Exception?.InnerException ?? response.Exception ?? new DurableTaskDivergenceException($"Durable task '{context.Id}' diverged from its record."));
            }

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

    /// <summary>Runs an attempt's body, whose accesses to journaled state the attempt's access log records.</summary>
    /// <remarks>
    /// The body's definition is created within the attempt, so the code which a method that returns a definition without
    /// being <see langword="async"/> runs belongs to the body.
    /// </remarks>
    private static ValueTask<DurableTaskResponse> RunAttemptAsync<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableTaskContext context)
    {
        DurableTask task;
        using (DurableTaskContext.Enter(context))
        {
            task = createTask(state);
        }

        return DurableTaskRuntimeHelper.RunAsync(task, context);
    }

    DurableTaskAccessLog IDurableTaskGrainRuntime.CreateAccessLog(
        DurableTaskContext attempt,
        TaskScheduler scheduler,
        Func<DurableTaskDivergenceException, Exception> diverge)
    {
        Debug.Assert(_accessLogStorage is not null, "Attempts start once the runtime is bound to its activation.");
        return new(attempt, _accessLogStorage, _shared.Serializer, scheduler, diverge);
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
        Debug.Assert(!context.IsAttemptEnded && !_stopping && !IsUnregistered(context), "Only a running attempt stages its response.");
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
        response = StoreResult(taskId, state, response);
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
    /// <returns>The stored result: <paramref name="response"/>, or the failure which replaced a result that doesn't serialize.</returns>
    private DurableTaskResponse StoreResult(TaskId taskId, IDurableTaskState state, DurableTaskResponse response, bool subscribed = false)
    {
        response = SetResponse(taskId, state, response);
        if (!subscribed && TryGetSubscription(state, out var target, out var subscribedTaskId))
        {
            Unsubscribe(target, subscribedTaskId, taskId);
        }

        if (state.CompletionDestinations.Count == 0)
        {
            TrackExpiry(GetRetentionExpiry(state));
        }

        return response;
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
                // The invocation which the cancellation precedes is admitted within the record's retention, or never.
                var recordExpiry = AddSaturating(state.CreatedAt, _shared.EarlyRecordRetention);
                expiry = recordExpiry > expiry ? recordExpiry : expiry;
            }
            else if (IsSignalRecord(state))
            {
                // A signal record without its task preceded the invocation, which is admitted within the record's retention,
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
        state.Kind == DurableTaskRecordKind.EarlyCancellation;

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
            // A recorded cancellation request staged its message, which the outbox owns until it retires; sending the
            // identity again would be a new send with a different admission cutoff.
            if (!state.CancellationRequestedAt.HasValue)
            {
                StageCancellation(taskId, state.RemoteTarget, state.CancellationReason);
            }
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
            // Only a caller's cancellation of its own child, which the inbox delivers, can overtake the child's
            // invocation: the record cancels the invocation when it arrives. A direct request names a task which this
            // grain retains, so a request for another task stages nothing.
            if (callerId.IsDefault)
            {
                throw new DurableTaskNotFoundException(taskId);
            }

            taskState = GetOrCreateTask(taskId, DurableTaskRecordKind.EarlyCancellation, request: null);
            StageCaller(taskId, taskState, callerId);
            _storage.RequestCancellation(taskId, taskState, reason);
            TrackExpiry(AddSaturating(taskState.CreatedAt, _shared.EarlyRecordRetention));
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
                    // The recorded request covers the execution's own cancellation of its remote call; see CancelRemoteAsync.
                    StageCancellation(currentTaskId, requestContext.TargetId, reason);
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
            MessageTransport.SendAbort(GrainId, state.RemoteTarget, GetWireId(taskId), reason);
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
    /// other parties address. A call to a grain reaches the task which the target stores under this grain's scope, by the
    /// call's wire identifier, and retains until this grain accepts its completion. The identifier of any other operation,
    /// or of one which recorded nothing, such as a start which an earlier cancellation prevented, is its own.
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

        return state.RemoteTarget.IsDefault ? taskId : CallerScope.GetTaskId(GrainId, GetWireId(taskId));
    }

    /// <summary>
    /// Returns the wire identifier of <paramref name="nodeId"/>, a stored node of one of this grain's execution trees, or
    /// one which its execution is about to record: the identifier which messages to and from other grains name it by.
    /// </summary>
    /// <remarks>
    /// The identifier covers the incarnation of the node's tree, so each run of a root identifier sends its own messages.
    /// See <see cref="CallerScope"/>.
    /// </remarks>
    internal TaskId GetWireId(TaskId nodeId) => CallerScope.GetWireId(GetIncarnation(nodeId), nodeId);

    /// <summary>
    /// Returns the node which <paramref name="wireId"/> names, and its state when the node is stored and belongs to the run
    /// of its tree which <paramref name="wireId"/> names.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the run which the wire identifier names was deleted: the node isn't stored, or a later
    /// run of its tree stores it.
    /// </returns>
    /// <exception cref="InvalidOperationException"><paramref name="wireId"/> isn't a wire identifier.</exception>
    private bool TryGetNode(TaskId wireId, out TaskId nodeId, [NotNullWhen(true)] out IDurableTaskState? state)
    {
        if (!CallerScope.TryParseWireId(wireId, out var incarnation, out nodeId))
        {
            throw new InvalidOperationException(
                $"Task ID '{wireId}' doesn't name a node of an execution tree of grain '{GrainId}'.");
        }

        if (_storage.TryGetTask(nodeId, out state) && GetIncarnation(nodeId) == incarnation)
        {
            return true;
        }

        state = null;
        return false;
    }

    /// <remarks>
    /// Every execution belongs to the tree of an invoked task, which records its incarnation when its invocation is first
    /// stored, so the walk always ends at a stored invoked task.
    /// </remarks>
    public Guid GetIncarnation(TaskId executionId)
    {
        for (var taskId = executionId; !taskId.IsDefault; taskId = taskId.Parent())
        {
            if (_storage.TryGetTask(taskId, out var state) && IsInvoked(state))
            {
                Debug.Assert(state.Incarnation != Guid.Empty, "An invoked task records its incarnation with its invocation.");
                return state.Incarnation;
            }
        }

        throw new InvalidOperationException($"Durable task '{executionId}' belongs to no invoked task of grain '{GrainId}'.");
    }

    public DateTimeOffset? GetCompletedAt(TaskId taskId) =>
        _storage.TryGetTask(taskId, out var state) ? state.CompletedAt : null;

    public void OnReplayEnded(TaskId executionId) => DurableTaskEvents.EmitReplayEnded(GrainContext, executionId);

    /// <remarks>
    /// An execution's recorded operations are its nearest stored descendants: descendants which are themselves executions,
    /// such as local steps, hold their own operations. Signal values belong to the invoked task, which senders deliver
    /// independently of its attempts; invoked descendants keep their own lifecycle; a cancellation which arrived
    /// before its request records no operation of this execution; version decisions record which code the execution
    /// follows rather than an operation which it reaches, so a deployment which removes a version check leaves no
    /// operation which its replays never reach; and a cancellation point records where the execution observed its
    /// cancellation, which a replay reaches by counting its operations.
    /// </remarks>
    public HashSet<TaskId>? GetRecordedOperations(TaskId executionId)
    {
        HashSet<TaskId>? operations = null;
        foreach (var (id, state) in _storage.GetChildren(executionId))
        {
            if (!IsInvoked(state)
                && !IsSignalRecord(state)
                && !IsCancellationRecord(state)
                && !IsVersionRecord(state)
                && !IsCancellationPointRecord(state))
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
    /// so the runtime records none of them, and the attempt's result is discarded. Journaled state changes which an aborted
    /// or deleted task's code makes, such as cleanup, commit with the grain's next journal write; those of an attempt which
    /// a later attempt replays, after deactivation or a stall, throw its cancellation. Its code observes the cancellation
    /// through its tokens once they are signaled, outside the runtime's current operation.
    /// </remarks>
    private bool CancelAttempt(GrainDurableTaskContext context, DurableTaskAttemptCancellationReason reason)
    {
        if (!context.EndAttempt(reason))
        {
            return false;
        }

        TrackBackgroundTask(ApplyCancellationAsync(context), "canceling a durable task attempt");
        return true;

        static async Task ApplyCancellationAsync(GrainDurableTaskContext context)
        {
            await Task.Yield();
            context.ApplyCancellation();
        }
    }
}
