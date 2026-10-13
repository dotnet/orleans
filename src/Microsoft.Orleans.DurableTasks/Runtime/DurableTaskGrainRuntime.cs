using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Journaling;

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
/// <see cref="Orleans.Messaging.Envelope.DeliverAt"/> for the delay's due time. The message is durable whenever the
/// delay is, and the inbox processes it once it is due, so the adapter schedules no Durable Jobs of its own.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    [FromKeyedServices("$task-logical-times")] IDurableDictionary<TaskId, DateTimeOffset> logicalTimes,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IStateMachine
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
            if (decision.TombstonedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"Durable completion decision '{decisionId}' completed and its retained result has expired.");
            }

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
                throw new InvalidOperationException(
                    $"Durable completion decision '{decisionId}' is already associated with another operation.");
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
                _storage.SetResponse(decisionId, decision, DurableTaskResponse.FromResult(candidate));
                await WriteStateAsync(cancellationToken);
                return candidate;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), _shared.TimeProvider, cancellationToken);
        }
    }

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

        return $"$completion-decision:{Convert.ToHexString(hash.GetHashAndReset())}";
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableExecutionContext CreateExecutionContext(TaskId taskId)
    {
        var utcNow = GetOrCreateLogicalTime(taskId);

        return _executionContexts.GetOrAdd(taskId, static (id, state) => new(
            id,
            state.Runtime,
            TaskScheduler.Current,
            state.Runtime._deactivationCts.Token,
            state.UtcNow), (Runtime: this, UtcNow: utcNow));
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

    private void SendCompletion(TaskId taskId, GrainId destination, DurableTaskResponse response) =>
        MessageTransport.SendCompletion(GrainId, destination, taskId, response);

    private void StageCancellation(TaskId taskId, GrainId target) =>
        MessageTransport.SendCancellation(GrainId, target, taskId);

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
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' is already associated with a different request.");
            }

            if (state.TombstonedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' completed and its retained result has expired.");
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

    public async ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, CancellationToken cancellationToken)
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

        // The cancellation and its message commit together; the outbox delivers the message only after that commit.
        _storage.RequestCancellation(taskId, state);
        if (!_preStagedCancellations.TryRemove((taskId, target), out _))
        {
            StageCancellation(taskId, target);
        }

        await WriteStateAsync(cancellationToken);
    }

    internal async ValueTask CancelScheduledTaskAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        var propagation = StageCancellationTree(taskId, callerId: default);
        await CommitResponseAsync(
            taskId,
            DurableTaskResponse.FromException(new OperationCanceledException()),
            cancellationToken);
        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var transport = MessageTransport;
        var logicalUtcNow = GetOrCreateLogicalTime(taskId);
        var duration = dueTime - logicalUtcNow;
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (state.Result is { IsCompleted: true } completed)
            {
                return completed;
            }

            if (state.DueTime is { } existingDueTime)
            {
                if (state.DelayDuration is { } existingDuration && existingDuration != duration)
                {
                    throw new InvalidOperationException(
                        $"Durable delay '{taskId}' was already scheduled for duration '{existingDuration}', not '{duration}'.");
                }

                if (existingDueTime != dueTime)
                {
                    throw new InvalidOperationException(
                        $"Durable delay '{taskId}' was already scheduled for '{existingDueTime:O}', not '{dueTime:O}'.");
                }

                if (state.DelayDuration is null)
                {
                    _storage.SetDelay(
                        taskId,
                        state,
                        existingDueTime,
                        duration,
                        state.ResumeGeneration);
                }

                // A replay can observe a delay staged by an earlier attempt whose write did not complete.
                await WriteStateAsync(cancellationToken);
                return DurableTaskResponse.Pending;
            }
        }

        state ??= _storage.GetOrCreateTask(taskId, request: null);
        var generation = checked(state.ResumeGeneration + 1);
        _storage.SetDelay(taskId, state, dueTime, duration, generation);

        // The resume commits in the same journal write as the delay, so every durable delay has a durable resume.
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

    private DurableTaskResponse StageAcceptedResponse(TaskId taskId, DurableTaskResponse response)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can be accepted.");
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = _storage.GetOrCreateTask(taskId, request: null);
        }

        if (state.Result is { IsCompleted: true } terminal)
        {
            return terminal;
        }

        _storage.SetResponse(taskId, state, response);
        return response;
    }

    /// <summary>
    /// Stages a remote child's terminal response and its acknowledgement, completing the local handle after commit.
    /// </summary>
    internal void AcceptResponseFromInbox(TaskId taskId, DurableTaskResponse response, GrainId sender)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state)
            || state.TombstonedAt.HasValue
            || state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is null
            || state.RemoteTarget != sender)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' does not accept completions from grain '{sender}'.");
        }

        var transport = MessageTransport;
        var winningResponse = StageAcceptedResponse(taskId, response);

        // The child retains its result until this acknowledgement arrives, and the acknowledgement commits with the
        // accepted response. Retention does not depend on outbox dead-lettering or the inbox deduplication window: a
        // caller which replays after its completion message was dead-lettered can invoke the child again and receive
        // the result.
        transport.SendCompletionAck(GrainId, sender, taskId);
        _pendingHandleResponses[taskId] = winningResponse;
    }

    /// <summary>
    /// Stages removal of an acknowledged completion destination and any pruning it enables.
    /// </summary>
    internal void AcknowledgeCompletionFromInbox(TaskId taskId, GrainId sender)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state)
            || !state.CompletionDestinations.Contains(sender))
        {
            return;
        }

        _storage.RemoveCompletionDestination(taskId, state, sender);
        PruneCompletedTasks();
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

    private async Task ResumePendingTasksCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var tasks = _storage.Tasks.ToList();
        foreach (var (taskId, state) in tasks)
        {
            if (state.Request is not null
                && state.Result is not { IsCompleted: true }
                && !state.CancellationRequestedAt.HasValue
                && state.DueTime is null)
            {
                _taskHandles.GetOrAdd(taskId, id => new TaskHandle(id, this));
            }
        }

        foreach (var (taskId, state) in tasks)
        {
            if (state.Result is { IsCompleted: true })
            {
                continue;
            }

            if (state.CancellationRequestedAt.HasValue)
            {
                if (!state.RemoteTarget.IsDefault)
                {
                    StageCancellation(taskId, state.RemoteTarget);
                }

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
            if (PruneCompletedTasks())
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
    internal DurableTaskResponse ScheduleFromInbox(TaskId taskId, IDurableTaskRequest request)
    {
        var scheduled = StageSchedule(taskId, request, fromInbox: true);
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
            throw new InvalidOperationException(
                $"Durable task '{taskId}' is already associated with a remote child request.");
        }

        ValidateCaller(taskId, state, requestContext.CallerId);
        if (state.Request is null
            && state.RequestFingerprint is null
            && state.CancellationRequestedAt is null)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' is already associated with a non-request operation.");
        }

        if (state.RequestFingerprint is { } existingFingerprint
            && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' is already associated with a different request.");
        }

        if (state.RequestFingerprint is null
            && state.Request is { } existingRequest
            && !string.Equals(
                IDurableTaskRequest.GetFingerprint(existingRequest, _shared.Serializer),
                fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' is already associated with a different request.");
        }

        if (state.TombstonedAt.HasValue)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' completed and its retained result has expired.");
        }

        StageCaller(taskId, state, requestContext.CallerId);
        if (state.RequestFingerprint is null)
        {
            _storage.SetRequestFingerprint(taskId, state, fingerprint);
        }

        if (state.CancellationRequestedAt.HasValue && state.Result is not { IsCompleted: true })
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
                SendCompletion(taskId, requestContext.CallerId, completed);
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
            return true;
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
            || state.TombstonedAt.HasValue
            || state.Result is { IsCompleted: true })
        {
            return;
        }

        if (state.CancellationRequestedAt.HasValue)
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
            if (!_pendingHandleResponses.IsEmpty)
            {
                QueueBackgroundWrite();
            }
            else if (handleResponses.Length > 0 && PruneCompletedTasks())
            {
                QueueBackgroundWrite();
            }
        }

        foreach (var id in propagations)
        {
            if (_pendingCancellationPropagations.TryRemove(id, out var propagation))
            {
                TrackBackgroundTask(
                    PropagateCancellationAfterWriteAsync(propagation.Contexts, propagation.Handles),
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
                    throw new InvalidOperationException(
                        $"Durable child task '{taskId}' is already associated with a different request.");
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
            throw new InvalidOperationException(
                $"Durable child task '{taskId}' is already associated with a different request.");
        }

        if (state is { CancellationRequestedAt: not null, Result: not { IsCompleted: true } })
        {
            if (!state.RemoteTarget.IsDefault)
            {
                StageCancellation(taskId, state.RemoteTarget);
            }

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

        // A child of a canceled ancestor is not created.
        if (IsAncestorCancellationRequested(taskId))
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

        // Otherwise, the task must be a local method invocation. Commit its state before invoking it, including when
        // an earlier attempt staged that state but its write did not complete.
        if (state is null)
        {
            _ = _storage.GetOrCreateTask(taskId, null);
        }

        await WriteStateAsync(cancellationToken);
        if (_storage.TryGetTask(taskId, out var committed))
        {
            if (committed.CancellationRequestedAt.HasValue && committed.Result is not { IsCompleted: true })
            {
                return new CompletedTaskHandle(
                    taskId,
                    await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken));
            }

            if (committed.Result is { IsCompleted: true } || committed.TombstonedAt.HasValue)
            {
                return GetScheduledTaskHandle(taskId);
            }
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
            throw new InvalidOperationException(
                $"Durable child task '{taskId}' is already associated with a different request.");
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
                _storage.SetResponse(taskId, state, schedulingResponse);
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

        public async ValueTask CancelAsync(CancellationToken cancellationToken) =>
            await (await _schedulerHandle.Task.WaitAsync(cancellationToken)).CancelAsync(cancellationToken);

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
            if (exception is not OperationCanceledException)
            {
                _shared.Logger.LogWarning(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            }

            response = DurableTaskResponse.FromException(exception);
        }

        try
        {
            if (response.IsCompleted && !_stopping)
            {
                await SetResponseAsync(context.TaskId, response, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            _shared.Logger.LogWarning(exception, "{Id} error committing the response of durable task '{TaskId}'.", GrainId, context.TaskId);
        }
        finally
        {
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

    private sealed record CancellationPropagation(
        List<GrainDurableExecutionContext> Contexts,
        List<IScheduledTaskHandle> Handles);

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
        await WriteStateAsync(cancellationToken);
        CompleteTaskHandle(taskId, winningResponse);
        if (PruneCompletedTasks())
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

        if (state.CompletionDestinations.Count > 0)
        {
            var transport = MessageTransport;
            foreach (var destination in state.CompletionDestinations)
            {
                transport.SendCompletion(GrainId, destination, taskId, response);
            }
        }

        _storage.SetResponse(taskId, state, response);
        return response;
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

    private static async Task PropagateCancellationAfterWriteAsync(
        List<GrainDurableExecutionContext> contexts,
        List<IScheduledTaskHandle> handles)
    {
        await Task.Yield();
        await PropagateCancellationAsync(contexts, handles, CancellationToken.None);
    }

    private void CompleteTaskHandle(TaskId taskId, DurableTaskResponse response)
    {
        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is TaskHandle localHandle)
        {
            localHandle.TrySetResponse(response);
        }
    }

    private bool PruneCompletedTasks()
    {
        // Prune all tasks which:
        // * Have a response
        // * Have no remaining clients to notify
        // * Have no parents waiting on them within this context
        // * Have been completed for more than a configured period of time
        var allTasks = _storage.Tasks.ToDictionary(static task => task.Id, static task => task.State);
        HashSet<TaskId>? completedTaskIds = default;
        Dictionary<TaskId, HashSet<TaskId>>? waitingOnParent = default;
        var now = _shared.TimeProvider.GetUtcNow();
        foreach (var (taskId, state) in allTasks)
        {
            if (state.Result is not { IsCompleted: true })
            {
                // The task is incomplete.
                continue;
            }

            if (state.CompletionDestinations.Count > 0)
            {
                // There are still unacknowledged clients.
                continue;
            }

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < _shared.Options.ResultRetentionPeriod)
            {
                // The task is being retained for at least the specified period of time.
                continue;
            }

            var parent = taskId.Parent();
            while (!parent.IsDefault
                && parent != TaskId.None
                && (!allTasks.TryGetValue(parent, out var parentState)
                    || parentState.TombstonedAt.HasValue))
            {
                parent = parent.Parent();
            }

            if (!parent.IsDefault
                && parent != TaskId.None
                && allTasks.TryGetValue(parent, out var retainedParent)
                && !retainedParent.TombstonedAt.HasValue)
            {
                // There is a local parent task which this task is waiting on, and that is the last thing keeping this task alive.
                waitingOnParent ??= [];
                ref var waiters = ref CollectionsMarshal.GetValueRefOrAddDefault(waitingOnParent, parent, out var exists);
                waiters ??= [];
                waiters.Add(taskId);
                continue;
            }

            completedTaskIds ??= [];
            completedTaskIds.Add(taskId);
        }

        if (completedTaskIds is not null)
        {
            foreach (var taskId in completedTaskIds)
            {
                PruneTaskTree(taskId);
            }
        }

        return completedTaskIds is not null;

        void PruneTaskTree(TaskId taskId)
        {
            if (waitingOnParent is not null && waitingOnParent.TryGetValue(taskId, out var childTaskIds))
            {
                foreach (var childTaskId in childTaskIds)
                {
                    PruneTaskTree(childTaskId);
                }
            }

            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
            }

            var completedState = allTasks[taskId];
            if (completedState.RequestFingerprint is not null || completedState.RemoteRequestFingerprint is not null)
            {
                _storage.CreateTombstone(taskId, completedState);
            }
            else
            {
                _storage.RemoveTask(taskId);
            }

            _executionContexts.TryRemove(taskId, out _);
            _taskHandles.TryRemove(taskId, out _);
            _logicalTimes.Remove(taskId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        if (!_storage.TryGetTask(taskId, out var taskState)
            || taskState.TombstonedAt.HasValue)
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
            return new DurableTaskDiagnosticState
            {
                CompletedAt = taskState.CompletedAt,
                CreatedAt = taskState.CreatedAt,
                Response = taskState.Result?.ToString(),
                Request = taskState.Request?.ToMethodCallString(),
                Status = taskState.Result switch
                {
                    { } response when response.Exception is null => "Completed",
                    { } => "Faulted",
                    null => "Pending",
                },
                Waiters = [.. taskState.CompletionDestinations.Select(static destination => destination.ToString())],
            };
        }
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
        SignalCancellationAsync(taskId, callerId: default, cancellationToken);

    /// <summary>
    /// Stages a cancellation received through the inbox, propagating it after the inbox batch commits.
    /// </summary>
    internal void SignalCancellationFromInbox(TaskId taskId, GrainId callerId)
    {
        var propagation = StageCancellationTree(taskId, callerId);
        if (propagation.Contexts.Count > 0 || propagation.Handles.Count > 0)
        {
            _pendingCancellationPropagations[Interlocked.Increment(ref _cancellationPropagationId)] =
                new(propagation.Contexts, propagation.Handles);
        }
    }

    private async ValueTask SignalCancellationAsync(
        TaskId taskId,
        GrainId callerId,
        CancellationToken cancellationToken)
    {
        // Cancellation is persisted before it propagates.
        var propagation = StageCancellationTree(taskId, callerId);
        if (propagation.Changed)
        {
            await WriteStateAsync(cancellationToken);
        }

        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, cancellationToken);
    }

    private (bool Changed, List<GrainDurableExecutionContext> Contexts, List<IScheduledTaskHandle> Handles) StageCancellationTree(
        TaskId taskId,
        GrainId callerId)
    {
        ThrowIfStopping();
        if (taskId.IsDefault)
        {
            throw new ArgumentException("Invalid TaskId.", nameof(taskId));
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            taskState = _storage.GetOrCreateTask(taskId, request: null);
            StageCaller(taskId, taskState, callerId);
            _storage.RequestCancellation(taskId, taskState);
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

            _storage.RequestCancellation(currentTaskId, currentTaskState);
            if (TryGetExecutionContext(currentTaskId, out var context))
            {
                if (currentTaskState.Request is IDurableTaskRequest request
                    && request.Context is { } requestContext
                    && requestContext.TargetId != GrainId)
                {
                    StageCancellation(currentTaskId, requestContext.TargetId);
                    _preStagedCancellations.TryAdd((currentTaskId, requestContext.TargetId), 0);
                }

                contexts.Add(context);
            }
            else if (!currentTaskState.RemoteTarget.IsDefault)
            {
                StageCancellation(currentTaskId, currentTaskState.RemoteTarget);
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
        CancellationToken cancellationToken)
    {
        var tasks = new List<Task>(canceledContexts.Count);
        foreach (var context in canceledContexts)
        {
            tasks.Add(DurableTaskRuntimeHelper.RequestCancellationAsync(context, CancellationToken.None));
        }

        foreach (var handle in canceledHandles)
        {
            tasks.Add(handle.CancelAsync(CancellationToken.None).AsTask());
        }

        await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    async ValueTask IDurableTaskServer.CancelAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await SignalCancellationAsync(taskId, cancellationToken);
    }

    private bool TryGetScheduledTaskHandle(TaskId taskId, [NotNullWhen(true)] out IScheduledTaskHandle? handle)
    {
        if (_taskHandles.TryGetValue(taskId, out handle))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var taskState))
        {
            if (taskState.TombstonedAt.HasValue)
            {
                handle = new CompletedTaskHandle(taskId, DurableTaskNotFoundException.CreateResponse(taskId));
                return true;
            }

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
