using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
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
/// once it is due. The adapter schedules no Durable Jobs of its own.
/// </para>
/// <para>
/// The runtime is the <see cref="IOutboxMessageSource"/> of its completion messages: the outbox journals a reference to
/// each completion, and the runtime materializes the message from the task's stored result, so the result is journaled
/// once. A stored result which no longer decodes fails only the operations which read it: the outbox dead-letters its
/// completion, whose retirement doesn't decode the result, and activation never decodes stored results.
/// </para>
/// <para>
/// An invoked task, which was scheduled as a request, owns an execution tree: the task and its descendants which are not
/// invoked tasks themselves, such as local children, delays, completion decisions, detach markers, and remote-child stubs. Task
/// identifiers are chosen by callers, so an invoked descendant keeps its own lifecycle. Once an invoked task is terminal
/// and no completion is pending, the runtime deletes its execution tree: in the write which retires its last completion
/// as delivered, because that destination durably holds the result, or otherwise once
/// <see cref="Orleans.Configuration.DurableTaskOptions.ResultRetentionPeriod"/> has elapsed since completion or since a
/// completion was dead-lettered. A task without a stored ancestor, such as a remote child scheduled outside a durable
/// execution, is deleted on the same retention schedule. Deleting a tree cancels its non-terminal tasks: it stages a
/// cancellation to each pending remote child and signals each running execution, whose result is then discarded.
/// Completions and resumes which arrive for a deleted task are ignored. A cancellation which arrives before its
/// invocation is recorded until <see cref="Orleans.Messaging.Configuration.DurableMessagingOptions.MaxOutboxRetryAge"/>
/// has elapsed, after which the invocation can no longer arrive.
/// </para>
/// <para>
/// Cancellation is cooperative: a request is recorded in task state, cancels the durable token of running attempts,
/// and terminalizes only leaves, such as delays and tasks whose bodies haven't started. A started body decides its task's
/// outcome, and recovery replays it with the request applied. An abort commits a task's execution tree as failed in one
/// write and then signals the host abort token of the tree's running attempts, whose results are discarded.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    [FromKeyedServices("$task-logical-times")] IDurableDictionary<TaskId, DateTimeOffset> logicalTimes,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IDurableTaskManager,
    IStateMachine,
    IOutboxMessageSource
{
    /// <summary>The journal state machine name of the runtime's commit hook.</summary>
    internal const string StateMachineName = "orleans.durable-tasks.runtime";

    private readonly ConcurrentDictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly ConcurrentDictionary<TaskId, RunningRequest> _runningRequests = [];
    private readonly ConcurrentDictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly ConcurrentDictionary<TaskId, byte> _pendingStarts = [];
    private readonly ConcurrentDictionary<TaskId, DurableTaskResponse> _pendingHandleResponses = [];
    private readonly ConcurrentDictionary<long, CancellationPropagation> _pendingCancellationPropagations = [];
    private readonly ConcurrentDictionary<(TaskId TaskId, GrainId Target), byte> _preStagedCancellations = [];
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = [];
    private readonly SemaphoreSlim _resumePendingGate = new(1, 1);
#if NET9_0_OR_GREATER
    private readonly Lock _stopLock = new();
#else
    private readonly object _stopLock = new();
#endif
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;
    private readonly IDurableDictionary<TaskId, DateTimeOffset> _logicalTimes = logicalTimes;
    private readonly IDurableTaskMessageTransport? _messageTransport = messageTransports.SingleOrDefault();
    private readonly CancellationTokenSource _deactivationCts = new();
    private TaskId[] _committingStarts = [];
    private KeyValuePair<TaskId, DurableTaskResponse>[] _committingHandleResponses = [];
    private long[] _committingCancellationPropagations = [];
    private Task? _stopTask;
    private long _backgroundWriteId;
    private long _cancellationPropagationId;
    private int _initialized;
    private volatile bool _stopping;

    // The completions pending in the outbox, by message ID. The outbox resolves recovered completions before the runtime
    // observes recovery, so the index is built from task state on first use, then maintained as completions are sent and
    // retire.
    private Dictionary<MessageId, PendingCompletion>? _pendingCompletions;

    // The earliest time at which a retained result or a cancellation record can expire. It is unknown after activation,
    // so the first check scans every task; afterwards every change which starts a retention period or records a
    // cancellation lowers it, and each scan recomputes it from the remaining tasks.
    private DateTimeOffset _nextExpiry = DateTimeOffset.MinValue;

    internal GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    public DateTimeOffset UtcNow => _shared.TimeProvider.GetUtcNow();

    private IDurableTaskMessageTransport MessageTransport => _messageTransport ?? throw new InvalidOperationException(
        "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");

    internal void InitializeForActivation() => Volatile.Write(ref _initialized, 1);

    internal Task OnActivateAsync(CancellationToken cancellationToken) => ResumePendingTasksAsync(cancellationToken);

    public async ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        var fingerprint = GetCompletionDecisionFingerprint(candidates);
        if (_storage.TryGetTask(decisionId, out var decision))
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
                    $"a WhenAny decision among {candidates.Count} candidates");
            }

            if (decision.RequestFingerprint is null)
            {
                _storage.SetRequestFingerprint(decisionId, decision, fingerprint);
            }

            if (decision.Result is { IsCompleted: true } recorded)
            {
                var winner = recorded.GetResult<TaskId>();
                if (!candidates.Contains(winner))
                {
                    throw new InvalidOperationException(
                        $"Durable completion decision '{decisionId}' was recorded for candidate '{winner}', which is not in the supplied candidate set.");
                }

                return winner;
            }
        }
        else
        {
            decision = _storage.GetOrCreateTask(decisionId, request: null);
            _storage.SetRequestFingerprint(decisionId, decision, fingerprint);
        }

        while (true)
        {
            // When several candidates have completed, the first in candidate order wins.
            foreach (var candidate in candidates)
            {
                var response = await GetScheduledTaskHandle(candidate).PollAsync(
                    new PollingOptions { PollTimeout = TimeSpan.Zero },
                    cancellationToken);
                if (!response.IsCompleted)
                {
                    continue;
                }

                decision = _storage.GetOrCreateTask(decisionId, request: null);
                StoreResult(decisionId, decision, DurableTaskResponse.FromResult(candidate));
                await WriteStateAsync(cancellationToken);
                return candidate;
            }

            await WaitForAnyAsync(candidates, cancellationToken);
        }
    }

    /// <summary>Waits until any candidate's handle completes, then cancels and drains the other waits.</summary>
    private async Task WaitForAnyAsync(IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waits = new Task<DurableTaskResponse>[candidates.Count];
        for (var index = 0; index < waits.Length; index++)
        {
            waits[index] = GetScheduledTaskHandle(candidates[index]).WaitAsync(waitCancellation.Token).AsTask();
        }

        try
        {
            // A failed wait propagates, as a failed poll does.
            await await Task.WhenAny(waits);
        }
        finally
        {
            waitCancellation.Cancel();
            await ((Task)Task.WhenAll(waits)).ConfigureAwait(
                ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // A request fingerprint is a hexadecimal hash, so it never has this prefix.
    private const string CompletionDecisionFingerprintPrefix = "$completion-decision:";

    /// <summary>
    /// Creates the error for scheduling an operation under an identifier which records another operation, which is how a
    /// replay which diverges from its recorded execution surfaces.
    /// </summary>
    private static InvalidOperationException CreateDivergenceException(string message, IDurableTaskState recorded, string requested) =>
        new($"{message} It records {DescribeOperation(recorded)}, and this attempt requested {requested}. A replay schedules the same operation, with the same arguments, under each identifier; new workflow steps use new identifiers.");

    private static string DescribeOperation(IDurableTaskState state) => state switch
    {
        { RemoteTarget.IsDefault: false } => $"a call to grain '{state.RemoteTarget}' ({AbbreviateFingerprint(state.RemoteRequestFingerprint)})",
        { DueTime: { } dueTime } => $"a delay due at {dueTime:O}",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(CompletionDecisionFingerprintPrefix, StringComparison.Ordinal) => "a WhenAny decision",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(DetachMarkerFingerprintPrefix, StringComparison.Ordinal) => "a detached task's start",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalSendFingerprintPrefix, StringComparison.Ordinal) => "a signal's send",
        { RequestFingerprint: { } fingerprint } when fingerprint.StartsWith(SignalFingerprintPrefix, StringComparison.Ordinal) => "a signal's value",
        { RequestFingerprint: { } fingerprint } => $"a request ({AbbreviateFingerprint(fingerprint)})",
        { CancellationRequestedAt: not null, CompletedAt: null } => "a cancellation which arrived before its request",
        _ => "a local child task",
    };

    private static string DescribeRequest(IDurableTaskRequest request, string fingerprint) =>
        $"{request.GetInterfaceName()}.{request.GetMethodName()} ({AbbreviateFingerprint(fingerprint)})";

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
    /// its durable token is canceled, <see cref="DurableExecutionContext.IsCancellationRequested"/> is set, and
    /// <see cref="DurableExecutionContext.CancellationReason"/> reports the recorded reason.
    /// </remarks>
    private GrainDurableExecutionContext CreateExecutionContext(TaskId taskId)
    {
        var utcNow = GetOrCreateLogicalTime(taskId);

        var context = _executionContexts.GetOrAdd(taskId, static (id, state) => new(
            id,
            state.Runtime,
            TaskScheduler.Current,
            state.Runtime._deactivationCts.Token,
            state.UtcNow), (Runtime: this, UtcNow: utcNow));
        if (_storage.TryGetTask(taskId, out var taskState) && taskState.CancellationRequestedAt.HasValue)
        {
            // No callback is registered before the body starts, so the request completes synchronously.
            _ = DurableTaskRuntimeHelper.RequestCancellationAsync(context, taskState.CancellationReason);
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
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableExecutionContext? executionContext) => _executionContexts.TryGetValue(taskId, out executionContext);

    private TaskHandle GetOrCreateRunningTaskHandle(TaskId taskId, GrainId remoteTarget = default)
    {
        var handle = _taskHandles.GetOrAdd(taskId, id => new TaskHandle(id, this, remoteTarget));
        if (handle is not TaskHandle taskHandle)
        {
            throw new InvalidOperationException($"Durable task '{taskId}' already has a completed handle.");
        }

        taskHandle.IsRunning = true;
        return taskHandle;
    }

    private bool TryRegisterCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (destination.IsDefault || destination.IsClient() || state.CompletionDestinations.Contains(destination))
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
            throw new InvalidOperationException(
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

    private void SendCompletion(TaskId taskId, GrainId destination)
    {
        var transport = MessageTransport;
        var messageId = transport.GetCompletionMessageId(GrainId, destination, taskId);
        PendingCompletions[messageId] = new(taskId, destination);
        transport.SendCompletion(messageId);
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
            // the result, and the destination is removed when the outbox retires it.
            pending = [];
            var transport = MessageTransport;
            foreach (var (taskId, state) in _storage.Tasks)
            {
                if (state.CompletedAt.HasValue)
                {
                    foreach (var destination in state.CompletionDestinations)
                    {
                        pending[transport.GetCompletionMessageId(GrainId, destination, taskId)] = new(taskId, destination);
                    }
                }
            }

            return _pendingCompletions = pending;
        }
    }

    /// <remarks>
    /// The runtime never withdraws a completion. A pending completion's destination keeps its task and result: only
    /// <see cref="IOutboxMessageSource.OnRetired"/> removes the destination, and tasks with destinations are never
    /// deleted. An exception from decoding the stored result propagates, so the outbox dead-letters the completion.
    /// </remarks>
    bool IOutboxMessageSource.TryGetMessage(MessageId messageId, out OutboxMessage message)
    {
        if (!PendingCompletions.TryGetValue(messageId, out var completion))
        {
            message = default;
            return false;
        }

        var found = _storage.TryGetTask(completion.TaskId, out var state);
        Debug.Assert(found && state!.Result is { IsCompleted: true }, "A pending completion retains its task's result.");
        message = MessageTransport.CreateCompletionMessage(messageId, GrainId, completion.Destination, completion.TaskId, state!.Result!);
        return true;
    }

    /// <summary>
    /// Removes a retired completion's destination, deleting the task with its execution tree once its result has been
    /// delivered to every destination.
    /// </summary>
    /// <remarks>
    /// The outbox stages the completion's removal in the same journal write, so the deletion commits with the retirement.
    /// A delivered destination durably holds the result, so the task needs no retention for it. A dead-lettered completion
    /// restarts the result's retention period, so a poller, or a caller which invokes the task again, can still observe
    /// it; the task is then deleted when that period ends. Retirement updates only the task's metadata, so it succeeds
    /// when the stored result no longer decodes; polling such a result surfaces the decoding error to the poller.
    /// </remarks>
    void IOutboxMessageSource.OnRetired(MessageId messageId, OutboxMessageRetirement retirement)
    {
        var removed = PendingCompletions.Remove(messageId, out var completion);
        Debug.Assert(removed, "The outbox retires only the completions which the runtime provides.");
        if (!_storage.TryGetTask(completion.TaskId, out var state))
        {
            Debug.Fail("A pending completion's task is retained until the completion retires.");
            return;
        }

        Debug.Assert(IsInvoked(state), "Only invoked tasks have completion destinations.");
        _storage.RemoveCompletionDestination(completion.TaskId, state, completion.Destination);
        if (retirement == OutboxMessageRetirement.DeadLettered)
        {
            // Retention starts now: the dead letter can follow a long retry period, and the result must remain
            // available to a poller or a re-invocation for the full retention period.
            _storage.RetainResult(completion.TaskId, state);
        }

        if (state.CompletionDestinations.Count > 0)
        {
            return;
        }

        if (state.RetainedSince.HasValue)
        {
            TrackExpiry(GetRetentionExpiry(state));
        }
        else
        {
            DeleteTree(completion.TaskId, state);
        }
    }

    private readonly record struct PendingCompletion(TaskId TaskId, GrainId Destination);

    private void StageCancellation(TaskId taskId, GrainId target, string? reason) =>
        MessageTransport.SendCancellation(GrainId, target, taskId, reason);

    private ValueTask WriteStateAsync(CancellationToken cancellationToken) => _storage.WriteAsync(cancellationToken);

    public async ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
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

            if (state.Result is { IsCompleted: true } completed)
            {
                return completed;
            }
        }

        state ??= _storage.GetOrCreateTask(taskId, request: null);
        if (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)
        {
            _storage.SetRemoteRequest(taskId, state, context.TargetId, fingerprint);
        }

        context.CallerId = GrainId;
        context.SupportsDurableCompletion = true;
        transport.SendInvocation(GrainId, context.TargetId, taskId, request);
        await WriteStateAsync(cancellationToken);
        return DurableTaskResponse.Pending;
    }

    /// <summary>
    /// Starts a request as a detached root task and waits for the journal write which commits it, together with this
    /// grain's other pending changes.
    /// </summary>
    /// <remarks>
    /// A detached task has no caller: its target retains it like a client-scheduled root and sends its result to no grain,
    /// so this grain stores no state for it. A task for this grain is staged locally and starts after the commit; a task
    /// for another grain is an invocation staged in the outbox. Within a durable execution, a terminal marker in the
    /// execution's tree records the start in the same write, so a replay which reaches the marker doesn't start the task
    /// again, and the marker is deleted with that tree. The marker is terminal, so canceling or deleting its tree doesn't
    /// reach the detached task.
    /// </remarks>
    public async ValueTask DetachAsync(
        TaskId rootId,
        IDurableTaskRequest request,
        TaskId markerId,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        Debug.Assert(!rootId.IsDefault && rootId.Parent().IsDefault, "A detached task is a root task.");
        var context = request.Context ?? throw new InvalidOperationException("The durable task request has no context.");
        string? markerFingerprint = null;
        if (!markerId.IsDefault)
        {
            markerFingerprint = GetDetachMarkerFingerprint(rootId, context.TargetId, request);
            if (_storage.TryGetTask(markerId, out var recorded))
            {
                if (!string.Equals(recorded.RequestFingerprint, markerFingerprint, StringComparison.Ordinal))
                {
                    throw CreateDivergenceException(
                        $"Durable task '{markerId}' is already associated with another operation.",
                        recorded,
                        $"detaching root task '{rootId}'");
                }

                // The marker commits with the start, possibly in a write which an earlier attempt is still awaiting.
                await WriteStateAsync(cancellationToken);
                return;
            }
        }

        if (context.TargetId == GrainId)
        {
            StageScheduleWithCommitEffects(rootId, request, fromInbox: false);
        }
        else
        {
            context.CallerId = default;
            context.SupportsDurableCompletion = false;
            MessageTransport.SendDetachedInvocation(GrainId, context.TargetId, rootId, request);
        }

        if (markerFingerprint is not null)
        {
            var marker = _storage.GetOrCreateTask(markerId, request: null);
            _storage.SetRequestFingerprint(markerId, marker, markerFingerprint);
            _storage.SetResponse(markerId, marker, DurableTaskResponse.Completed);
        }

        await WriteStateAsync(cancellationToken);
    }

    // Fingerprints of operations in an execution tree begin with '$', so they never equal a hexadecimal request fingerprint.
    private const string DetachMarkerFingerprintPrefix = "$detach:";

    private string GetDetachMarkerFingerprint(TaskId rootId, GrainId target, IDurableTaskRequest request)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[sizeof(int)];
        foreach (var value in (ReadOnlySpan<string>)[rootId.ToString(), target.ToString(), IDurableTaskRequest.GetFingerprint(request, _shared.Serializer)])
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return $"{DetachMarkerFingerprintPrefix}{Convert.ToHexString(hash.GetHashAndReset())}";
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
        _storage.RequestCancellation(taskId, state, reason);
        if (!_preStagedCancellations.TryRemove((taskId, target), out _))
        {
            StageCancellation(taskId, target, state.CancellationReason);
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
        await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, reason, cancellationToken);
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
            if (state.Result is { IsCompleted: true } completed)
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

        state ??= _storage.GetOrCreateTask(taskId, request: null);
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
    /// Stages a remote child's terminal response, completing the local handle after commit.
    /// </summary>
    /// <remarks>
    /// The inbox accepts the completion directly into task state, so this stored response is its only journaled copy.
    /// The child retains its result until the inbox reports this acceptance to the child's outbox. A completion for a
    /// task which no longer exists is accepted without changing state: the task was deleted with its execution tree,
    /// which canceled it, and nothing awaits its result.
    /// </remarks>
    internal void AcceptResponseFromInbox(TaskId taskId, DurableTaskResponse response, GrainId sender)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state))
        {
            return;
        }

        if (state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is null
            || state.RemoteTarget != sender)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' does not accept completions from grain '{sender}'.");
        }

        _pendingHandleResponses[taskId] = StageAcceptedResponse(taskId, state, response);
    }

    internal async Task ResumePendingTasksAsync(CancellationToken cancellationToken)
    {
        await _resumePendingGate.WaitAsync(cancellationToken);
        try
        {
            await ResumePendingTasksCoreAsync(cancellationToken);
        }
        finally
        {
            _resumePendingGate.Release();
        }
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
        var tasks = _storage.Tasks.ToList();
        foreach (var (taskId, state) in tasks)
        {
            if (!HasStoredResult(state)
                && state.Request is not null
                && state.DueTime is null
                && (!state.CancellationRequestedAt.HasValue || !IsCancellationLeaf(taskId, state)))
            {
                _taskHandles.GetOrAdd(taskId, id => new TaskHandle(id, this));
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

            var executionContext = CreateExecutionContext(taskId);
            _ = GetOrCreateRunningTaskHandle(taskId);
            state.Request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            StartInvocation(taskId, static request => request.CreateTask(), state.Request, executionContext);
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
            _pendingStarts[taskId] = 0;
        }

        return scheduled.Response;
    }

    private ScheduledRequest StageSchedule(TaskId taskId, IDurableTaskRequest request, bool fromInbox)
    {
        ThrowIfStopping();
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
            // A completion is never addressed to its own grain: ScheduleRemoteAsync rejects self-invocation.
            throw new InvalidOperationException(
                $"Durable task '{taskId}' cannot be invoked by its owning grain '{GrainId}' through durable messaging.");
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = _storage.GetOrCreateTask(taskId, request);
            StageCaller(taskId, state, requestContext.CallerId);
            _storage.SetRequestFingerprint(taskId, state, fingerprint);
            var subscribedNew = requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);
            return new(subscribedNew ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending, Start: true, CompletedResponse: null);
        }

        if (!state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is not null)
        {
            throw CreateDivergenceException(
                $"Durable task '{taskId}' is already associated with a remote child request.",
                state,
                DescribeRequest(request, fingerprint));
        }

        ValidateCaller(taskId, state, requestContext.CallerId);
        if (state.Request is null
            && state.RequestFingerprint is null
            && state.CancellationRequestedAt is null)
        {
            throw CreateDivergenceException(
                $"Durable task '{taskId}' is already associated with a non-request operation.",
                state,
                DescribeRequest(request, fingerprint));
        }

        if (state.RequestFingerprint is { } existingFingerprint
            && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw CreateDivergenceException(
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
            throw CreateDivergenceException(
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
            _storage.SetRequest(taskId, state, request);
            if (requestContext.SupportsDurableCompletion)
            {
                TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);
            }

            var canceled = StageResponse(taskId, DurableTaskResponse.FromException(new OperationCanceledException()));
            return new(canceled, Start: false, CompletedResponse: canceled);
        }

        if (state.Result is { IsCompleted: true } completed)
        {
            if (ShouldSendCompletion(taskId, state, requestContext))
            {
                SendCompletion(taskId, requestContext.CallerId);
            }

            return new(completed, Start: false, CompletedResponse: null);
        }

        var subscribed = requestContext.SupportsDurableCompletion
            && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);

        // A request staged by an attempt whose write did not complete has not started yet.
        return new(subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending, Start: true, CompletedResponse: null);
    }

    private readonly record struct ScheduledRequest(DurableTaskResponse Response, bool Start, DurableTaskResponse? CompletedResponse);

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

        if (state.CompletionDestinations.Contains(requestContext.CallerId))
        {
            // The completion to this destination is still pending in the outbox.
            return false;
        }

        return TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);
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
        committedRequest.SetTarget(_shared.GrainContextAccessor.GrainContext);
        StartInvocation(taskId, static value => value.CreateTask(), committedRequest, executionContext);
    }

    void IStateMachine.ReplayEntry(JournalEntry entry, JournalReplayContext context) =>
        throw new InvalidOperationException($"Journal state '{StateMachineName}' does not write journal entries.");

    void IStateMachine.Reset(JournalStreamWriter writer)
    {
        _pendingStarts.Clear();
        _pendingHandleResponses.Clear();
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
        _committingStarts = _pendingStarts.IsEmpty ? [] : [.. _pendingStarts.Keys];
        _committingHandleResponses = _pendingHandleResponses.IsEmpty ? [] : [.. _pendingHandleResponses];
        _committingCancellationPropagations = _pendingCancellationPropagations.IsEmpty ? [] : [.. _pendingCancellationPropagations.Keys];
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
            if (_pendingStarts.TryRemove(taskId, out _))
            {
                StartRequest(taskId);
            }
        }

        foreach (var entry in handleResponses)
        {
            _pendingHandleResponses.TryRemove(entry);
            CompleteTaskHandle(entry.Key, entry.Value);
        }

        if (!_stopping)
        {
            // Checking for expired tasks is cheap until the earliest expiry passes.
            if (!_pendingHandleResponses.IsEmpty || DeleteExpiredTasks())
            {
                QueueBackgroundWrite();
            }
        }

        foreach (var id in propagations)
        {
            if (_pendingCancellationPropagations.TryRemove(id, out var propagation))
            {
                TrackBackgroundTask(
                    PropagateCancellationAfterWriteAsync(propagation),
                    "propagating committed durable task cancellation");
            }
        }
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask durableTask,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (durableTask is ISchedulableTask schedulableTask and not IDurableTaskRequest)
        {
            return await ScheduleCustomChildAsync(
                taskId,
                schedulableTask,
                cancellationToken);
        }

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        var stateExisted = _storage.TryGetTask(taskId, out var state);
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
            return new CompletedTaskHandle(
                taskId,
                await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken));
        }

        if (handle is CompletedTaskHandle)
        {
            return handle;
        }

        if (handle is TaskHandle cachedHandle
            && cachedHandle.ResponseTask.IsCompletedSuccessfully
            && cachedHandle.ResponseTask.Result.IsCompleted)
        {
            return handle;
        }

        // A child of a canceled ancestor is not created. A child which existed when the cancellation was requested
        // records the request itself.
        if (state is not { CancellationRequestedAt: not null } && IsAncestorCancellationRequested(taskId))
        {
            return new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled);
        }

        if (handle is not null)
        {
            return handle;
        }

        if (remoteRequest is not null)
        {
            return await ScheduleRemoteChildAsync(taskId, remoteRequest, cancellationToken);
        }

        // Otherwise, the task must be a local method invocation. Its state is staged and commits with the next write,
        // such as the first durable operation of its body or its result, so a synchronous step costs one write. A step's
        // identity and inputs follow from its parent's inputs and earlier results, so a replay which finds no record
        // runs the step again.
        if (state is null)
        {
            _ = _storage.GetOrCreateTask(taskId, null);
        }

        var executionContext = CreateExecutionContext(taskId);
        handle = GetOrCreateRunningTaskHandle(taskId);
        StartInvocation(taskId, static task => task, durableTask, executionContext);
        return handle;
    }

    private async ValueTask<IScheduledTaskHandle> ScheduleRemoteChildAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        _ = MessageTransport;
        var transientHandle = new TaskHandle(taskId, this, request.Context!.TargetId) { IsRunning = true };
        var handle = _taskHandles.GetOrAdd(taskId, transientHandle);
        if (handle is TaskHandle taskHandle)
        {
            taskHandle.IsRunning = true;
        }

        try
        {
            var response = await ScheduleRemoteAsync(taskId, request, cancellationToken);
            if (response.IsCompleted)
            {
                return new CompletedTaskHandle(taskId, response);
            }

            return handle;
        }
        catch
        {
            ((ICollection<KeyValuePair<TaskId, IScheduledTaskHandle>>)_taskHandles)
                .Remove(new(taskId, transientHandle));
            throw;
        }
    }

    private bool IsAncestorCancellationRequested(TaskId taskId)
    {
        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (_storage.TryGetTask(ancestorId, out var ancestorState)
                && ancestorState.CancellationRequestedAt.HasValue)
            {
                return true;
            }
        }

        return false;
    }

    private async ValueTask<IScheduledTaskHandle> ScheduleCustomChildAsync(
        TaskId taskId,
        ISchedulableTask schedulableTask,
        CancellationToken cancellationToken)
    {
        var stateExisted = _storage.TryGetTask(taskId, out var state);
        if (state is not null
            && (state.Request is not null
                || state.RequestFingerprint is not null
                || !state.CallerId.IsDefault
                || !state.RemoteTarget.IsDefault
                || state.RemoteRequestFingerprint is not null))
        {
            throw CreateDivergenceException(
                $"Durable child task '{taskId}' is already associated with a different request.",
                state,
                "a custom child task");
        }

        IScheduledTaskHandle? replaceableHandle = null;
        if (stateExisted && TryGetScheduledTaskHandle(taskId, out var existingHandle))
        {
            if (existingHandle is not TaskHandle localHandle || localHandle.IsRunning)
            {
                return existingHandle;
            }

            replaceableHandle = existingHandle;
        }

        if (state is { CancellationRequestedAt: not null, Result: not { IsCompleted: true } })
        {
            return new CompletedTaskHandle(
                taskId,
                await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, CancellationToken.None));
        }

        if (IsAncestorCancellationRequested(taskId))
        {
            if (state is null)
            {
                return new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled);
            }

            _storage.RequestCancellation(taskId, state);
            return new CompletedTaskHandle(
                taskId,
                await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, CancellationToken.None));
        }

        // The deferred handle claims the child while its scheduler runs, so concurrent callers and cancellation
        // observe the scheduler's handle once it is available.
        var deferredHandle = new DeferredScheduledTaskHandle(taskId);
        if (replaceableHandle is not null)
        {
            if (!_taskHandles.TryUpdate(taskId, deferredHandle, replaceableHandle))
            {
                return _taskHandles[taskId];
            }
        }
        else
        {
            var registeredHandle = _taskHandles.GetOrAdd(taskId, deferredHandle);
            if (!ReferenceEquals(registeredHandle, deferredHandle))
            {
                return registeredHandle;
            }
        }

        // The child state lets cancellation of the child or its ancestors reach the deferred handle.
        state ??= _storage.GetOrCreateTask(taskId, null);

        DurableTaskResponse schedulingResponse;
        IScheduledTaskHandle? scheduledHandle = null;
        try
        {
            schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (!schedulingResponse.IsCompleted)
            {
                scheduledHandle = schedulableTask.GetHandle(taskId);
                deferredHandle.SetSchedulerHandle(scheduledHandle);
            }
        }
        catch (Exception exception)
        {
            FailDeferredCustomHandle(taskId, deferredHandle, exception);

            // Forget a child which only this attempt created, unless cancellation was requested for it meanwhile.
            if (!stateExisted
                && _storage.TryGetTask(taskId, out var created)
                && !created.CancellationRequestedAt.HasValue
                && created.Result is null)
            {
                _storage.RemoveTask(taskId);
            }

            throw;
        }

        try
        {
            IScheduledTaskHandle resultHandle;
            if (!_storage.TryGetTask(taskId, out state))
            {
                throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
            }

            if (state.Result is { IsCompleted: true } completedResponse)
            {
                resultHandle = new CompletedTaskHandle(taskId, completedResponse);
            }
            else if (state.CancellationRequestedAt.HasValue)
            {
                resultHandle = new CompletedTaskHandle(
                    taskId,
                    await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken));
            }
            else if (schedulingResponse.IsCompleted)
            {
                StoreResult(taskId, state, schedulingResponse);
                await WriteStateAsync(cancellationToken);
                resultHandle = new CompletedTaskHandle(taskId, schedulingResponse);
            }
            else
            {
                await WriteStateAsync(cancellationToken);
                resultHandle = scheduledHandle
                    ?? throw new InvalidOperationException(
                        $"The scheduler for durable child task '{taskId}' did not provide a handle.");
            }

            deferredHandle.SetResultHandle(resultHandle);
            _taskHandles.TryUpdate(taskId, resultHandle, deferredHandle);
            return resultHandle;
        }
        catch (Exception exception)
        {
            FailDeferredCustomHandle(taskId, deferredHandle, exception);
            throw;
        }
    }

    private void FailDeferredCustomHandle(TaskId taskId, DeferredScheduledTaskHandle deferredHandle, Exception exception)
    {
        deferredHandle.SetException(exception);
        ((ICollection<KeyValuePair<TaskId, IScheduledTaskHandle>>)_taskHandles)
            .Remove(new(taskId, deferredHandle));
    }

    private sealed class DeferredScheduledTaskHandle(TaskId taskId) : IScheduledTaskHandle
    {
        private readonly TaskCompletionSource<IScheduledTaskHandle> _handle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IScheduledTaskHandle> _schedulerHandle =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskId TaskId { get; } = taskId;

        public async ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken) =>
            await (await _handle.Task.WaitAsync(cancellationToken)).WaitAsync(cancellationToken);

        public async ValueTask<DurableTaskResponse> PollAsync(
            PollingOptions options,
            CancellationToken cancellationToken) =>
            await (await _handle.Task.WaitAsync(cancellationToken)).PollAsync(options, cancellationToken);

        public ValueTask CancelAsync(CancellationToken cancellationToken) => CancelAsync(reason: null, cancellationToken);

        public async ValueTask CancelAsync(string? reason, CancellationToken cancellationToken) =>
            await (await _schedulerHandle.Task.WaitAsync(cancellationToken)).CancelAsync(reason, cancellationToken);

        public async ValueTask AbortAsync(string reason, CancellationToken cancellationToken) =>
            await (await _schedulerHandle.Task.WaitAsync(cancellationToken)).AbortAsync(reason, cancellationToken);

        public void SetSchedulerHandle(IScheduledTaskHandle handle) =>
            _schedulerHandle.TrySetResult(handle);

        public void SetResultHandle(IScheduledTaskHandle handle)
        {
            _schedulerHandle.TrySetResult(handle);
            _handle.TrySetResult(handle);
        }

        public void SetException(Exception exception)
        {
            _schedulerHandle.TrySetException(exception);
            _handle.TrySetException(exception);
        }
    }

    private void StartInvocation<TState>(
        TaskId taskId,
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Invoke(createTask, state, context, start.Task);

        // Callers check for a running request before an await, so the registration decides which one executes.
        if (!_runningRequests.TryAdd(taskId, new RunningRequest(invocation, context)))
        {
            start.SetCanceled();
            return;
        }

        start.SetResult();
    }

    private async Task Invoke<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context,
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
            if (exception is not OperationCanceledException && !IsDiscarded(context))
            {
                _shared.Logger.LogWarning(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            }

            response = DurableTaskResponse.FromException(exception);
        }

        // A cancellation which arrives while this attempt still runs, for example while a delay's journal write is in
        // flight, cancels only the attempt's token. The attempt then terminalizes the task, so a pending operation
        // which it returns, such as a delay, does not outlive the cancellation.
        if (!response.IsCompleted
            && _storage.TryGetTask(context.TaskId, out var taskState)
            && taskState.CancellationRequestedAt.HasValue)
        {
            response = DurableTaskResponse.Canceled;
        }

        try
        {
            if (response.IsCompleted && !_stopping && !IsDiscarded(context))
            {
                await SetResponseAsync(context.TaskId, response, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _shared.Logger.LogWarning(exception, "{Id} error committing the response of durable task '{TaskId}'.", GrainId, context.TaskId);

            // A preparation failure retains the staged response, so a later write commits it and completes the handle.
            if (exception is JournaledStatePreCommitException && !_stopping)
            {
                QueueBackgroundWrite();
            }
        }
        finally
        {
            context.OnAttemptCompleted();
            if (_runningRequests.TryGetValue(context.TaskId, out var running)
                && ReferenceEquals(running.Context, context))
            {
                ((ICollection<KeyValuePair<TaskId, RunningRequest>>)_runningRequests)
                    .Remove(new(context.TaskId, running));
                ((ICollection<KeyValuePair<TaskId, GrainDurableExecutionContext>>)_executionContexts)
                    .Remove(new(context.TaskId, context));
            }
        }
    }

    private sealed record RunningRequest(
        Task Task,
        GrainDurableExecutionContext Context);

    /// <summary>
    /// Returns whether an attempt's task was deleted with its execution tree, which unregisters the attempt's context,
    /// so the attempt's result is discarded.
    /// </summary>
    private bool IsDiscarded(GrainDurableExecutionContext context) =>
        !_executionContexts.TryGetValue(context.TaskId, out var registered) || !ReferenceEquals(registered, context);

    /// <summary>
    /// The cancellation which a committed write propagates: durable cancellation, carrying
    /// <see cref="CancellationReason"/>, of running attempts and of scheduler-owned children, or, for an abort with
    /// <see cref="AbortReason"/>, the attempts' host abort and the children's abort.
    /// </summary>
    private sealed record CancellationPropagation(
        List<GrainDurableExecutionContext> Contexts,
        List<IScheduledTaskHandle> Handles,
        string? AbortReason = null,
        string? CancellationReason = null);

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
        var winningResponse = StageResponse(taskId, response);

        // The handle completes when a write commits the response: this write, or a later one when this write fails
        // before it commits, because a failed preparation retains staged changes.
        _pendingHandleResponses[taskId] = winningResponse;
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

        // The outbox reads each completion from the stored result, so the result is staged first.
        StoreResult(taskId, state, response);
        foreach (var destination in state.CompletionDestinations)
        {
            SendCompletion(taskId, destination);
        }

        return response;
    }

    /// <summary>
    /// Stores a task's terminal result. Without completion destinations, the result's retention period starts now.
    /// </summary>
    private void StoreResult(TaskId taskId, IDurableTaskState state, DurableTaskResponse response)
    {
        _storage.SetResponse(taskId, state, response);
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

    private void TrackBackgroundTask(Task task, string operation)
    {
        var id = Interlocked.Increment(ref _backgroundWriteId);
        var observedTask = ObserveBackgroundTaskAsync(task, operation);
        _backgroundTasks[id] = observedTask;
        _ = observedTask.ContinueWith(
            static (_, state) =>
            {
                var (runtime, taskId) = ((DurableTaskGrainRuntime Runtime, long TaskId))state!;
                runtime._backgroundTasks.TryRemove(taskId, out Task? _);
            },
            (this, id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveBackgroundTaskAsync(Task task, string operation)
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
    }

    private async Task PersistBackgroundChangesAsync()
    {
        await Task.Yield();
        await WriteStateAsync(CancellationToken.None);
    }

    private static async Task PropagateCancellationAfterWriteAsync(CancellationPropagation propagation)
    {
        await Task.Yield();
        if (propagation.AbortReason is { } reason)
        {
            // An aborted attempt runs no durable cancellation callbacks: it observes only its host abort token.
            foreach (var context in propagation.Contexts)
            {
                context.SignalAbort();
            }

            await Task.WhenAll(propagation.Handles.Select(handle => AbortSchedulerOwnedChildAsync(handle, reason)));
            return;
        }

        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, propagation.CancellationReason, CancellationToken.None);
    }

    /// <summary>Aborts a child which a custom scheduler runs, or requests its cancellation when the scheduler can't abort.</summary>
    private static async Task AbortSchedulerOwnedChildAsync(IScheduledTaskHandle handle, string reason)
    {
        try
        {
            await handle.AbortAsync(reason, CancellationToken.None);
        }
        catch (NotSupportedException)
        {
            await handle.CancelAsync(CancellationToken.None);
        }
    }

    private void CompleteTaskHandle(TaskId taskId, DurableTaskResponse response)
    {
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
                // The invocation which the cancellation precedes is delivered within the sender's retry age, or never.
                var recordExpiry = AddSaturating(state.CreatedAt, _shared.MaxOutboxRetryAge);
                expiry = recordExpiry > expiry ? recordExpiry : expiry;
            }

            if (expiry <= now)
            {
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

        // Execution trees are disjoint: each one ends at the invoked tasks below it.
        foreach (var (taskId, state) in expired)
        {
            DeleteTree(taskId, state);
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
    /// Completion decisions, detach markers, signal records, and signal send markers record fingerprints which begin with
    /// <c>$</c>, but they are part of their parent's tree; request fingerprints are hexadecimal.
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
    /// child's cancellation is staged with the deletion, and a running execution is signaled after the deletion commits;
    /// its result is then discarded.
    /// </remarks>
    private void DeleteTree(TaskId taskId, IDurableTaskState state)
    {
        Debug.Assert(state.CompletionDestinations.Count == 0, "A pending completion retains its task's result.");
        List<GrainDurableExecutionContext>? contexts = null;
        List<IScheduledTaskHandle>? handles = null;
        DeleteTreeCore(taskId, state, ref contexts, ref handles);
        if (contexts is not null || handles is not null)
        {
            _pendingCancellationPropagations[Interlocked.Increment(ref _cancellationPropagationId)] =
                new(contexts ?? [], handles ?? []);
        }
    }

    private void DeleteTreeCore(
        TaskId taskId,
        IDurableTaskState state,
        ref List<GrainDurableExecutionContext>? contexts,
        ref List<IScheduledTaskHandle>? handles)
    {
        foreach (var (childId, child) in _storage.GetChildren(taskId).ToList())
        {
            if (!IsInvoked(child))
            {
                DeleteTreeCore(childId, child, ref contexts, ref handles);
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
            _preStagedCancellations.TryRemove((taskId, state.RemoteTarget), out _);
        }

        if (_executionContexts.TryRemove(taskId, out var context))
        {
            context.MarkDeleted();
            if (!terminal)
            {
                (contexts ??= []).Add(context);
            }
        }

        if (_runningRequests.TryRemove(taskId, out var running))
        {
            // Deactivation still drains the discarded attempt.
            TrackBackgroundTask(running.Task, "running a deleted durable task");
        }

        if (_taskHandles.TryRemove(taskId, out var handle))
        {
            if (handle is TaskHandle { ResponseTask.IsCompleted: false } localHandle)
            {
                localHandle.TrySetResponse(terminal ? state.Result! : DurableTaskResponse.Canceled);
            }
            else if (!terminal && handle is not TaskHandle and not CompletedTaskHandle)
            {
                // A custom scheduler owns the child's work.
                (handles ??= []).Add(handle);
            }
        }

        _pendingStarts.TryRemove(taskId, out _);
        _pendingHandleResponses.TryRemove(taskId, out _);
        _logicalTimes.Remove(taskId);
        _storage.RemoveTask(taskId);
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        if (!_storage.TryGetTask(taskId, out _))
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
            var state = GetDiagnosticState(taskState);

            yield return (taskId, state);
        }

        static DurableTaskDiagnosticState GetDiagnosticState(IDurableTaskState taskState)
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
                Waiters = [.. taskState.CompletionDestinations.Select(static destination => destination.ToString())],
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
            _pendingCancellationPropagations[Interlocked.Increment(ref _cancellationPropagationId)] =
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
        if (propagation.Changed)
        {
            await WriteStateAsync(cancellationToken);
        }

        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, reason, cancellationToken);
    }

    /// <remarks>
    /// Each task which the request newly reaches records <paramref name="reason"/>, so every execution and handle which
    /// the request collects is canceled carrying that reason.
    /// </remarks>
    private (bool Changed, List<GrainDurableExecutionContext> Contexts, List<IScheduledTaskHandle> Handles) StageCancellationTree(
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
            taskState = _storage.GetOrCreateTask(taskId, request: null);
            StageCaller(taskId, taskState, callerId);
            _storage.RequestCancellation(taskId, taskState, reason);
            TrackExpiry(AddSaturating(taskState.CreatedAt, _shared.MaxOutboxRetryAge));
            return (true, [], []);
        }

        ValidateCaller(taskId, taskState, callerId);
        StageCaller(taskId, taskState, callerId);
        List<GrainDurableExecutionContext> canceledContexts = [];
        List<IScheduledTaskHandle> canceledHandles = [];
        var changed = RequestCancellationCore(taskId, taskState, canceledContexts, canceledHandles);
        return (changed, canceledContexts, canceledHandles);

        bool RequestCancellationCore(
            TaskId currentTaskId,
            IDurableTaskState currentTaskState,
            List<GrainDurableExecutionContext> contexts,
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
                Debug.Assert(currentTaskId.IsParentOf(childTaskId));
                _ = RequestCancellationCore(childTaskId, childTaskState, contexts, handles);
            }

            _storage.RequestCancellation(currentTaskId, currentTaskState, reason);
            if (TryGetExecutionContext(currentTaskId, out var context))
            {
                if (currentTaskState.Request is IDurableTaskRequest request
                    && request.Context is { } requestContext
                    && requestContext.TargetId != GrainId)
                {
                    StageCancellation(currentTaskId, requestContext.TargetId, reason);
                    _preStagedCancellations.TryAdd((currentTaskId, requestContext.TargetId), 0);
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

    private static async Task PropagateCancellationAsync(
        List<GrainDurableExecutionContext> canceledContexts,
        List<IScheduledTaskHandle> canceledHandles,
        string? reason,
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(canceledContexts.Count);
        foreach (var context in canceledContexts)
        {
            tasks.Add(DurableTaskRuntimeHelper.RequestCancellationAsync(context, reason, CancellationToken.None));
        }

        foreach (var handle in canceledHandles)
        {
            tasks.Add(handle.CancelAsync(reason, CancellationToken.None).AsTask());
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
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
    /// running attempt is signaled after that write and its result discarded.
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

    /// <summary>Stages an abort received through the inbox, signaling the aborted attempts after the inbox batch commits.</summary>
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
        List<GrainDurableExecutionContext>? contexts = null;
        List<IScheduledTaskHandle>? handles = null;
        AbortTreeCore(taskId, state, reason, response, ref contexts, ref handles);
        if (contexts is not null || handles is not null)
        {
            _pendingCancellationPropagations[Interlocked.Increment(ref _cancellationPropagationId)] =
                new(contexts ?? [], handles ?? [], AbortReason: reason);
        }

        return true;
    }

    private void AbortTreeCore(
        TaskId taskId,
        IDurableTaskState state,
        string reason,
        DurableTaskResponse response,
        ref List<GrainDurableExecutionContext>? contexts,
        ref List<IScheduledTaskHandle>? handles)
    {
        foreach (var (childId, child) in _storage.GetChildren(taskId).ToList())
        {
            if (!IsInvoked(child))
            {
                AbortTreeCore(childId, child, reason, response, ref contexts, ref handles);
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

        // Unregistering the attempt discards its result; deactivation still drains it.
        if (_executionContexts.TryRemove(taskId, out var context))
        {
            context.MarkAborted();
            (contexts ??= []).Add(context);
        }

        if (_runningRequests.TryRemove(taskId, out var running))
        {
            TrackBackgroundTask(running.Task, "running an aborted durable task");
        }

        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is not TaskHandle and not CompletedTaskHandle)
        {
            // A custom scheduler owns the child's work.
            (handles ??= []).Add(handle);
        }

        _pendingStarts.TryRemove(taskId, out _);
        _pendingHandleResponses[taskId] = StageResponse(taskId, response);
    }

    // The base constructor of ScheduledTask is internal to the abstractions, so an untyped handle uses an object result.
    ScheduledTask IDurableTaskManager.GetTask(TaskId taskId) => ((IDurableTaskManager)this).GetTask<object?>(taskId);

    // The runtime serves the handle's polls and cancellation directly, as it serves them for a grain call.
    ScheduledTask<TResult> IDurableTaskManager.GetTask<TResult>(TaskId taskId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(taskId, default);
        return new AttachedScheduledTask<TResult>(taskId, this);
    }

    ValueTask IDurableTaskManager.CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken) =>
        SignalCancellationAsync(taskId, callerId: default, reason, cancellationToken);

    private bool TryGetScheduledTaskHandle(TaskId taskId, [NotNullWhen(true)] out IScheduledTaskHandle? handle)
    {
        if (_taskHandles.TryGetValue(taskId, out handle))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var taskState))
        {
            if (_pendingHandleResponses.ContainsKey(taskId))
            {
                handle = new TaskHandle(taskId, this, taskState.RemoteTarget);
                handle = _taskHandles.GetOrAdd(taskId, handle);
                return true;
            }

            // Rehydrate the task handle.
            if (taskState.Result is { } response)
            {
                Debug.Assert(response.IsCompleted);
                handle = new CompletedTaskHandle(taskId, response);
                return true;
            }
            else
            {
                // Create a new handle for the task.
                handle = new TaskHandle(taskId, this, taskState.RemoteTarget);
                handle = _taskHandles.GetOrAdd(taskId, handle);
                return true;
            }
        }

        return false;
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

    public DateTimeOffset? GetCompletedAt(TaskId taskId) =>
        _storage.TryGetTask(taskId, out var state) ? state.CompletedAt : null;

    internal Task StopAsync(CancellationToken _)
    {
        lock (_stopLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        await _deactivationCts.CancelAsync();
        var shutdownResponse = DurableTaskResponse.FromCanceled(
            new OperationCanceledException("The grain activation is stopping.", _deactivationCts.Token));
        foreach (var handle in _taskHandles.Values)
        {
            if (handle is TaskHandle localHandle)
            {
                localHandle.TrySetResponse(shutdownResponse);
            }
        }

        while (true)
        {
            if (_runningRequests.IsEmpty && _backgroundTasks.IsEmpty)
            {
                return;
            }

            var running = _runningRequests.Values
                .Select(static request => request.Task)
                .Concat(_backgroundTasks.Values)
                .ToArray();
            // Abandoning the drain would allow a replacement activation to overlap this one.
            // Adapter-controlled waits observe the lifecycle token. Arbitrary user code can ignore it, so
            // deactivation deliberately waits for every execution before replacement replay can begin.
            await Task.WhenAll(running).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private bool IsRequestRunning(TaskId taskId) => _runningRequests.ContainsKey(taskId);

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException(
                $"Durable Tasks requires grain activations to derive from Orleans.Journaling.DurableGrain or implement {typeof(Orleans.Messaging.IMessagingGrain).FullName} so the runtime binds to the activation's journal and inbox.");
        }

        if (_stopping)
        {
            throw new OperationCanceledException(
                "The durable task runtime is stopping and cannot accept new work.",
                _deactivationCts.Token);
        }
    }
}
