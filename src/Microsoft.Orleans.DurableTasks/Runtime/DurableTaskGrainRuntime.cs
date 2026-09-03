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
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableJobs;
using Orleans.Journaling;

namespace Orleans.DurableTasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports,
    IJournaledStateManager stateManager) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IDurableJobFeatureHandler,
    IJournaledStateObserver
{
    private readonly ConcurrentDictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly ConcurrentDictionary<TaskId, RunningRequest> _runningRequests = [];
    private readonly ConcurrentDictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly ConcurrentDictionary<TaskId, IDurableTaskRequest> _pendingStarts = [];
    private readonly ConcurrentDictionary<TaskId, IDurableTaskRequest> _committingStarts = [];
    private readonly ConcurrentDictionary<TaskId, DurableTaskResponse> _pendingHandleResponses = [];
    private readonly ConcurrentDictionary<TaskId, DurableTaskResponse> _committingHandleResponses = [];
    private readonly ConcurrentDictionary<string, byte> _missingTaskStateRetries = [];
    private readonly ConcurrentDictionary<(TaskId TaskId, GrainId Target), byte> _preStagedCancellations = [];
    private readonly ConcurrentDictionary<long, Task> _backgroundTasks = [];
    private readonly Dictionary<TaskId, (List<GrainDurableExecutionContext> Contexts, List<IScheduledTaskHandle> Handles)> _inboxCancellationPropagations = [];
    private readonly HashSet<TaskId> _readyInboxCancellationCommits = [];
    private readonly HashSet<TaskId> _committingInboxCancellationCommits = [];
    private readonly HashSet<TaskId> _inboxCancellationCommitOwners = [];
#if NET9_0_OR_GREATER
    private readonly Lock _inboxCancellationLock = new();
#else
    private readonly object _inboxCancellationLock = new();
#endif
    private readonly SemaphoreSlim _journalWriteGate = new(1, 1);
    private readonly SemaphoreSlim _responseCommitGate = new(1, 1);
    private readonly SemaphoreSlim _resumePendingGate = new(1, 1);
#if NET9_0_OR_GREATER
    private readonly Lock _stopLock = new();
#else
    private readonly object _stopLock = new();
#endif
    private Task _recoveryReconciliation = Task.CompletedTask;
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;
    private readonly IDurableTaskMessageTransport? _messageTransport = messageTransports.SingleOrDefault();
    private readonly IJournaledStateManager _stateManager = stateManager;
    private readonly CancellationTokenSource _deactivationCts = new();
    private Task? _stopTask;
    private long _backgroundWriteId;
    private long _recoveryGeneration;
    private int _activated;
    private int _initialized;
    private volatile bool _stopping;

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    public DateTimeOffset UtcNow => _shared.TimeProvider.GetUtcNow();

    internal void InitializeForActivation() => Volatile.Write(ref _initialized, 1);

    internal void MarkActivated() => Volatile.Write(ref _activated, 1);

    public async ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        await Volatile.Read(ref _recoveryReconciliation).WaitAsync(cancellationToken);
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
        return _executionContexts.GetOrAdd(taskId, static (id, runtime) => new(
            id,
            runtime,
            TaskScheduler.Current,
            runtime._deactivationCts.Token), this);
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

    private void ValidateOrSetCaller(TaskId taskId, IDurableTaskState state, GrainId callerId)
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

        if (state.CallerId.IsDefault)
        {
            _storage.SetCallerId(taskId, state, callerId);
        }
    }

    private void SendCompletion(TaskId taskId, GrainId destination, DurableTaskResponse response)
    {
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCompletion(GrainId, destination, taskId, response);
    }

    private void StageCancellation(TaskId taskId, GrainId target)
    {
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCancellation(GrainId, target, taskId);
    }

    private async ValueTask WriteStateAsync(CancellationToken cancellationToken)
    {
        await _journalWriteGate.WaitAsync(cancellationToken);
        try
        {
            await WriteStateUnderJournalWriteGateAsync(cancellationToken);
        }
        finally
        {
            _journalWriteGate.Release();
        }
    }

    private async ValueTask WriteStateWithRollbackAsync(CancellationToken cancellationToken)
    {
        await _journalWriteGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await WriteStateUnderJournalWriteGateAsync(cancellationToken);
            }
            catch
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _journalWriteGate.Release();
        }
    }

    private ValueTask SetResponseWithRollbackAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken) =>
        WithResponseAndJournalWriteGatesAsync(
            async () =>
            {
                try
                {
                    await SetResponseCoreAsync(
                        taskId,
                        response,
                        cancellationToken,
                        persist: true,
                        journalWriteGateHeld: true);
                }
                catch
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                    throw;
                }
            },
            cancellationToken);

    private ValueTask WriteStateUnderJournalWriteGateAsync(CancellationToken cancellationToken) =>
        _storage.WriteAsync(cancellationToken);

    private async ValueTask<TResult> WithJournalWriteGateAsync<TResult>(
        Func<ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        await _journalWriteGate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            _journalWriteGate.Release();
        }
    }

    public async ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        await Volatile.Read(ref _recoveryReconciliation).WaitAsync(cancellationToken);
        return await WithJournalWriteGateAsync(
            () => ScheduleRemoteUnderJournalWriteGateAsync(taskId, request, cancellationToken),
            cancellationToken);
    }

    private async ValueTask<DurableTaskResponse> ScheduleRemoteUnderJournalWriteGateAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        var context = request.Context ?? throw new InvalidOperationException("The durable task request has no context.");
        if (context.TargetId == GrainId)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' cannot invoke its owning grain '{GrainId}' through durable messaging.");
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        var stateExisted = _storage.TryGetTask(taskId, out var state);
        state ??= _storage.GetOrCreateTask(taskId, request: null);
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

        try
        {
            if (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)
            {
                _storage.SetRemoteRequest(taskId, state, context.TargetId, fingerprint);
            }

            context.CallerId = GrainId;
            context.SupportsDurableCompletion = true;
            transport.SendInvocation(GrainId, context.TargetId, taskId, request);
            await WriteStateUnderJournalWriteGateAsync(cancellationToken);
        }
        catch
        {
            if (!stateExisted)
            {
                _storage.RemoveTask(taskId);
            }

            await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
            throw;
        }

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

        _storage.RequestCancellation(taskId, state);
        if (_preStagedCancellations.TryRemove((taskId, target), out _))
        {
            return;
        }

        await WriteStateAsync(cancellationToken);
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCancellation(GrainId, target, taskId);
        await WriteStateAsync(cancellationToken);
    }

    internal async ValueTask CancelScheduledTaskAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        TryGetExecutionContext(taskId, out var executionContext);
        _storage.RequestCancellation(taskId, state);
        await SetResponseAsync(
            taskId,
            DurableTaskResponse.FromException(new OperationCanceledException()),
            cancellationToken);
        if (executionContext is not null)
        {
            var cancellation = DurableTaskRuntimeHelper.RequestCancellationAsync(
                executionContext,
                CancellationToken.None);
            await cancellation.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        var dueTime = UtcNow + duration;
        var stateExisted = _storage.TryGetTask(taskId, out var state);
        state ??= _storage.GetOrCreateTask(taskId, request: null);
        if (state.Result is { IsCompleted: true } completed)
        {
            return completed;
        }

        if (state.DueTime is { } existingDueTime)
        {
            if (state.DelayDuration is { } existingDuration
                && existingDuration != duration)
            {
                throw new InvalidOperationException(
                    $"Durable delay '{taskId}' was already scheduled for duration '{existingDuration}', not '{duration}'.");
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

            return DurableTaskResponse.Pending;
        }

        var generation = checked(state.ResumeGeneration + 1);
        try
        {
            _storage.SetDelay(taskId, state, dueTime, duration, generation);
            await transport.ScheduleResumeAsync(GrainId, taskId, generation, dueTime, cancellationToken);
            await WriteStateAsync(cancellationToken);
        }
        catch
        {
            if (!stateExisted)
            {
                _storage.RemoveTask(taskId);
            }

            await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
            throw;
        }

        return DurableTaskResponse.Pending;
    }

    public bool CanHandle(string jobName) =>
        string.Equals(jobName, DurableTaskMessageTransport.ResumeJobName, StringComparison.Ordinal);

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (context.Job.Metadata is not { } metadata
            || !metadata.TryGetValue(DurableTaskMessageTransport.ResumeTaskIdMetadata, out var taskIdText)
            || !TaskId.TryParse(taskIdText, out var taskId)
            || !metadata.TryGetValue(DurableTaskMessageTransport.ResumeGenerationMetadata, out var generationText)
            || !long.TryParse(
                generationText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation))
        {
            return DurableJobRunResult.Completed;
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            if (_stateManager.PendingWriteByteCount == 0
                || !_missingTaskStateRetries.TryAdd(context.Job.Id, 0))
            {
                _missingTaskStateRetries.TryRemove(context.Job.Id, out _);
                return DurableJobRunResult.Completed;
            }

            return DurableJobRunResult.RescheduleAt(UtcNow + TimeSpan.FromMilliseconds(10));
        }

        _missingTaskStateRetries.TryRemove(context.Job.Id, out _);
        if (state.Result is { IsCompleted: true })
        {
            return DurableJobRunResult.Completed;
        }

        if (state.CancellationRequestedAt.HasValue)
        {
            await SetResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
            return DurableJobRunResult.Completed;
        }

        if (state.ResumeGeneration != generation)
        {
            return DurableJobRunResult.Completed;
        }

        if (state.DueTime is { } dueTime && dueTime > UtcNow)
        {
            return DurableJobRunResult.RescheduleAt(dueTime);
        }

        await SetResponseAsync(taskId, DurableTaskResponse.Completed, cancellationToken);
        return DurableJobRunResult.Completed;
    }

    internal void AcceptResponse(TaskId taskId, DurableTaskResponse response) =>
        CompleteTaskHandle(taskId, AcceptResponseCore(taskId, response));

    private DurableTaskResponse AcceptResponseCore(TaskId taskId, DurableTaskResponse response)
    {
        ThrowIfStopping();
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can be accepted.");
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = _storage.GetOrCreateTask(taskId, request: null);
        }

        var winningResponse = state.Result is { IsCompleted: true } terminal ? terminal : response;
        if (state.Result is not { IsCompleted: true })
        {
            _storage.SetResponse(taskId, state, response);
        }

        return winningResponse;
    }

    internal async ValueTask AcceptResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        GrainId target,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        await _responseCommitGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
            {
                return;
            }

            await AcceptResponseCoreAsync(taskId, response, target, cancellationToken, persist);
        }
        finally
        {
            _responseCommitGate.Release();
        }
    }

    private async ValueTask AcceptResponseCoreAsync(
        TaskId taskId,
        DurableTaskResponse response,
        GrainId target,
        CancellationToken cancellationToken,
        bool persist)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state)
            || state.TombstonedAt.HasValue
            || state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is null
            || state.RemoteTarget != target)
        {
            throw new InvalidOperationException(
                $"Durable task '{taskId}' does not accept completions from grain '{target}'.");
        }

        var winningResponse = AcceptResponseCore(taskId, response);
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCompletionAck(GrainId, target, taskId);
        if (persist)
        {
            await WriteStateAsync(cancellationToken);
            CompleteTaskHandle(taskId, winningResponse);
            if (PruneCompletedTasks())
            {
                await WriteStateAsync(cancellationToken);
            }
        }
        else
        {
            _pendingHandleResponses[taskId] = winningResponse;
        }
    }

    internal async ValueTask AcknowledgeCompletionAsync(
        TaskId taskId,
        GrainId destination,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state)
            || !state.CompletionDestinations.Contains(destination))
        {
            return;
        }

        _storage.RemoveCompletionDestination(taskId, state, destination);
        PruneCompletedTasks();
        if (persist)
        {
            await WriteStateAsync(cancellationToken);
        }
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
        foreach (var (taskId, state) in _storage.Tasks.ToList())
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

            if (state.DueTime is { } dueTime && state.ResumeGeneration > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                await transport.ScheduleResumeAsync(
                    GrainId,
                    taskId,
                    state.ResumeGeneration,
                    dueTime,
                    cancellationToken);
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
        await Volatile.Read(ref _recoveryReconciliation).WaitAsync(cancellationToken);
        return await ScheduleAsyncCore(taskId, request, persist: true, cancellationToken);
    }

    internal ValueTask<DurableTaskResponse> ScheduleFromInboxAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken) =>
        ScheduleAsyncCore(taskId, request, persist: false, cancellationToken);

    private async ValueTask<DurableTaskResponse> ScheduleAsyncCore(
        TaskId taskId,
        IDurableTaskRequest request,
        bool persist,
        CancellationToken cancellationToken)
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

        if (persist)
        {
            requestContext.CallerId = default;
            requestContext.SupportsDurableCompletion = false;
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        if (_storage.TryGetTask(taskId, out var identifiedState))
        {
            if (!identifiedState.RemoteTarget.IsDefault
                || identifiedState.RemoteRequestFingerprint is not null)
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' is already associated with a remote child request.");
            }

            ValidateOrSetCaller(taskId, identifiedState, requestContext.CallerId);
            if (identifiedState.Request is null
                && identifiedState.RequestFingerprint is null
                && identifiedState.CancellationRequestedAt is null
                && (identifiedState.DueTime is not null
                    || TryGetScheduledTaskHandle(taskId, out _)))
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' is already associated with a non-request operation.");
            }

            if (identifiedState.RequestFingerprint is { } existingFingerprint
                && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' is already associated with a different request.");
            }

            if (identifiedState.RequestFingerprint is null)
            {
                if (identifiedState.Request is { } existingRequest
                    && !string.Equals(
                        IDurableTaskRequest.GetFingerprint(existingRequest, _shared.Serializer),
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Durable task '{taskId}' is already associated with a different request.");
                }

                _storage.SetRequestFingerprint(taskId, identifiedState, fingerprint);
            }

            if (identifiedState.TombstonedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' completed and its retained result has expired.");
            }
        }

        if (_storage.TryGetTask(taskId, out var existingState)
            && existingState.CancellationRequestedAt.HasValue
            && existingState.Result is not { IsCompleted: true })
        {
            _storage.SetRequest(taskId, existingState, request);
            if (requestContext.SupportsDurableCompletion)
            {
                TryRegisterCompletionDestination(taskId, existingState, requestContext.CallerId);
            }
            var canceled = DurableTaskResponse.FromException(new OperationCanceledException());
            if (persist)
            {
                await SetResponseWithRollbackAsync(taskId, canceled, cancellationToken);
            }
            else
            {
                await SetResponseAsync(taskId, canceled, cancellationToken, persist: false);
            }

            return canceled;
        }

        // Check if the task is already running.
        if (TryGetScheduledTaskHandle(taskId, out var handle))
        {
            // If it is and it's completed, return the result immediately.
            var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
            if (response.IsCompleted)
            {
                if (_storage.TryGetTask(taskId, out var completedState)
                    && ShouldSendCompletion(taskId, completedState, requestContext))
                {
                    SendCompletion(taskId, requestContext.CallerId, response);
                }

                if (persist)
                {
                    await WriteStateWithRollbackAsync(cancellationToken);
                }
                return response;
            }

            // Register a durable completion destination for grain callers.
            if (_storage.TryGetTask(taskId, out var state)
                && requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId))
            {
                if (persist)
                {
                    await WriteStateWithRollbackAsync(cancellationToken);
                }
                return DurableTaskResponse.Subscribed;
            }

            if (persist)
            {
                await WriteStateWithRollbackAsync(cancellationToken);
            }
            return DurableTaskResponse.Pending;
        }
        else
        {
            // Create the task state and register the caller if they are addressable.
            var state = _storage.GetOrCreateTask(taskId, request);
            ValidateOrSetCaller(taskId, state, requestContext.CallerId);
            if (state.RequestFingerprint is null)
            {
                _storage.SetRequestFingerprint(taskId, state, fingerprint);
            }
            if (state.Request is null)
            {
                _storage.SetRequest(taskId, state, request);
            }

            var subscribed = requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);

            // If the task was already scheduled, return a response immediately.
            if (state.Result is { } response && response.IsCompleted)
            {
                if (subscribed || ShouldSendCompletion(taskId, state, requestContext))
                {
                    SendCompletion(taskId, requestContext.CallerId, response);
                }

                if (persist)
                {
                    await WriteStateWithRollbackAsync(cancellationToken);
                }
                return response;
            }

            // Persist the task state before invoking the task.
            // Note that if we intercept all outgoing calls to other durable tasks, then we do not need to do this here.
            // Instead, we can defer it until either the task completes or an outgoing call is made, since we can guarantee
            // no visible side-effects.
            // If the user does the 'wrong' thing and calls a non-durable task from their code, then that could expose an externality.
            if (persist)
            {
                await WriteStateWithRollbackAsync(cancellationToken);
                StartRequest(taskId);
            }
            else
            {
                _pendingStarts[taskId] = request;
            }

            return subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending;
        }
    }

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

    public void OnWriteStarted()
    {
        _committingStarts.Clear();
        foreach (var entry in _pendingStarts)
        {
            _committingStarts[entry.Key] = entry.Value;
        }

        _committingHandleResponses.Clear();
        foreach (var entry in _pendingHandleResponses)
        {
            _committingHandleResponses[entry.Key] = entry.Value;
        }

        lock (_inboxCancellationLock)
        {
            foreach (var taskId in _readyInboxCancellationCommits)
            {
                _committingInboxCancellationCommits.Add(taskId);
            }
        }
    }

    public void OnWriteCompleted()
    {
        foreach (var taskId in _committingStarts.Keys)
        {
            _pendingStarts.TryRemove(taskId, out _);
            StartRequest(taskId);
        }

        _committingStarts.Clear();
        var committedHandleResponses = !_committingHandleResponses.IsEmpty;
        foreach (var (taskId, response) in _committingHandleResponses)
        {
            _pendingHandleResponses.TryRemove(taskId, out _);
            CompleteTaskHandle(taskId, response);
        }

        _committingHandleResponses.Clear();
        if (!_pendingHandleResponses.IsEmpty)
        {
            QueueBackgroundWrite();
        }
        else if (committedHandleResponses && PruneCompletedTasks())
        {
            QueueBackgroundWrite();
        }

        List<(List<GrainDurableExecutionContext> Contexts, List<IScheduledTaskHandle> Handles)>? completedInboxCancellations = null;
        var releaseJournalGate = false;
        lock (_inboxCancellationLock)
        {
            foreach (var taskId in _committingInboxCancellationCommits)
            {
                if (_inboxCancellationCommitOwners.Remove(taskId))
                {
                    releaseJournalGate = true;
                    if (_inboxCancellationPropagations.Remove(taskId, out var propagation))
                    {
                        completedInboxCancellations ??= [];
                        completedInboxCancellations.Add(propagation);
                    }
                }

                _readyInboxCancellationCommits.Remove(taskId);
            }

            _committingInboxCancellationCommits.Clear();
        }

        if (releaseJournalGate)
        {
            _journalWriteGate.Release();
        }

        if (completedInboxCancellations is not null)
        {
            foreach (var propagation in completedInboxCancellations)
            {
                TrackBackgroundTask(
                    PropagateCancellationAfterWriteAsync(propagation.Contexts, propagation.Handles),
                    "propagating committed durable task cancellation");
            }
        }
    }

    public void OnRecoveryCompleted()
    {
        var reconciliation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _recoveryReconciliation, reconciliation.Task);
        var recoveryGeneration = Interlocked.Increment(ref _recoveryGeneration);
        var staleRequests = _runningRequests.Values
            .Where(request => request.RecoveryGeneration < recoveryGeneration)
            .ToArray();
        _pendingStarts.Clear();
        _committingStarts.Clear();
        _pendingHandleResponses.Clear();
        _committingHandleResponses.Clear();
        _preStagedCancellations.Clear();
        var releaseJournalGate = false;
        lock (_inboxCancellationLock)
        {
            releaseJournalGate = _inboxCancellationCommitOwners.Count > 0;
            _inboxCancellationCommitOwners.Clear();
            _readyInboxCancellationCommits.Clear();
            _committingInboxCancellationCommits.Clear();
            _inboxCancellationPropagations.Clear();
        }

        if (releaseJournalGate)
        {
            _journalWriteGate.Release();
        }

        if (Volatile.Read(ref _activated) != 0)
        {
            TrackBackgroundTask(
                ReconcileAfterRecoveryAsync(
                    recoveryGeneration,
                    staleRequests,
                    reconciliation),
                "reconciling durable task executions after recovery");
        }
        else
        {
            reconciliation.TrySetResult();
        }
    }

    private async Task ReconcileAfterRecoveryAsync(
        long recoveryGeneration,
        RunningRequest[] staleRequests,
        TaskCompletionSource reconciliation)
    {
        try
        {
            await Task.Yield();
            if (staleRequests.Length > 0)
            {
                foreach (var request in staleRequests)
                {
                    if (request.Context.AbortExecution() is { } exception)
                    {
                        _shared.Logger.LogWarning(
                            exception,
                            "{Id} error aborting stale durable task execution '{TaskId}' after recovery.",
                            GrainId,
                            request.Context.TaskId);
                    }
                }

                try
                {
                    var drain = Task.WhenAll(staleRequests.Select(request => request.Task));
                    await drain.WaitAsync(
                        _shared.Options.RecoveryExecutionDrainTimeout,
                        _shared.TimeProvider,
                        CancellationToken.None);
                }
                catch (TimeoutException exception)
                {
                    var failure = new TimeoutException(
                        $"Durable task recovery for grain '{GrainId}' timed out waiting for {staleRequests.Length} stale execution(s) to stop.",
                        exception);
                    _shared.GrainContextAccessor.GrainContext.Deactivate(
                        new DeactivationReason(
                            DeactivationReasonCode.ApplicationError,
                            failure.Message));
                    reconciliation.TrySetException(failure);
                    return;
                }
                catch (Exception exception)
                {
                    _shared.Logger.LogWarning(
                        exception,
                        "{Id} error canceling stale durable task executions after recovery.",
                        GrainId);
                }

                foreach (var request in staleRequests)
                {
                    ((ICollection<KeyValuePair<TaskId, RunningRequest>>)_runningRequests)
                        .Remove(new(request.Context.TaskId, request));
                    ((ICollection<KeyValuePair<TaskId, GrainDurableExecutionContext>>)_executionContexts)
                        .Remove(new(request.Context.TaskId, request.Context));
                    if (_taskHandles.TryGetValue(request.Context.TaskId, out var handle)
                        && handle is TaskHandle { RemoteTarget.IsDefault: true } localHandle)
                    {
                        localHandle.IsRunning = false;
                    }
                }
            }

            if (_stopping || recoveryGeneration != Volatile.Read(ref _recoveryGeneration))
            {
                return;
            }

            foreach (var (taskId, state) in _storage.Tasks)
            {
                if (state.TombstonedAt.HasValue)
                {
                    CompleteTaskHandle(taskId, DurableTaskTerminalFailure.CreateResponse(taskId));
                }
                else if (state.Result is { IsCompleted: true } response)
                {
                    CompleteTaskHandle(taskId, response);
                }
            }

            if (Volatile.Read(ref _activated) != 0)
            {
                await ResumePendingTasksAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            reconciliation.TrySetException(exception);
            throw;
        }
        finally
        {
            reconciliation.TrySetResult();
        }
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask durableTask,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        await Volatile.Read(ref _recoveryReconciliation).WaitAsync(cancellationToken);
        if (durableTask is ISchedulableTask schedulableTask and not IDurableTaskRequest)
        {
            return await ScheduleCustomChildAsync(
                taskId,
                schedulableTask,
                cancellationToken);
        }

        return await WithResponseAndJournalWriteGatesAsync(
            () => ScheduleChildUnderJournalWriteGateAsync(taskId, durableTask, cancellationToken),
            cancellationToken);
    }

    private async ValueTask<TResult> WithResponseAndJournalWriteGatesAsync<TResult>(
        Func<ValueTask<TResult>> action,
        CancellationToken cancellationToken)
    {
        await _responseCommitGate.WaitAsync(cancellationToken);
        try
        {
            return await WithJournalWriteGateAsync(
                action,
                cancellationToken);
        }
        finally
        {
            _responseCommitGate.Release();
        }
    }

    private async ValueTask WithResponseAndJournalWriteGatesAsync(
        Func<ValueTask> action,
        CancellationToken cancellationToken)
    {
        await _responseCommitGate.WaitAsync(cancellationToken);
        try
        {
            await WithJournalWriteGateAsync(
                async () =>
                {
                    await action();
                    return true;
                },
                cancellationToken);
        }
        finally
        {
            _responseCommitGate.Release();
        }
    }

    private async ValueTask<IScheduledTaskHandle> ScheduleCustomChildAsync(
        TaskId taskId,
        ISchedulableTask schedulableTask,
        CancellationToken cancellationToken)
    {
        var preparation = await WithResponseAndJournalWriteGatesAsync(
            () => PrepareCustomChildUnderJournalWriteGateAsync(taskId),
            cancellationToken);
        if (preparation.Handle is not null)
        {
            return preparation.Handle;
        }

        DurableTaskResponse schedulingResponse;
        IScheduledTaskHandle? scheduledHandle = null;
        try
        {
            schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (!schedulingResponse.IsCompleted)
            {
                scheduledHandle = schedulableTask.GetHandle(taskId);
                preparation.DeferredHandle?.SetSchedulerHandle(scheduledHandle);
            }
        }
        catch (Exception exception)
        {
            await WithResponseAndJournalWriteGatesAsync(
                () => CleanupFailedCustomChildUnderJournalWriteGateAsync(
                    taskId,
                    preparation,
                    exception),
                CancellationToken.None);
            throw;
        }

        try
        {
            return await WithResponseAndJournalWriteGatesAsync(
                () => CompleteCustomChildUnderJournalWriteGateAsync(
                    taskId,
                    schedulingResponse,
                    scheduledHandle,
                    preparation,
                    cancellationToken),
                cancellationToken);
        }
        catch (Exception exception)
        {
            IScheduledTaskHandle? recoveredHandle = null;
            await WithResponseAndJournalWriteGatesAsync(
                async () =>
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                    if (_storage.TryGetTask(taskId, out var recoveredState)
                        && !recoveredState.TombstonedAt.HasValue)
                    {
                        if (recoveredState.Result is { IsCompleted: true } recoveredResponse)
                        {
                            recoveredHandle = new CompletedTaskHandle(taskId, recoveredResponse);
                        }
                        else if (!recoveredState.CancellationRequestedAt.HasValue
                            && !schedulingResponse.IsCompleted
                            && scheduledHandle is not null)
                        {
                            recoveredHandle = scheduledHandle;
                        }
                    }

                    if (recoveredHandle is not null)
                    {
                        CompleteDeferredCustomHandle(taskId, preparation, recoveredHandle);
                        return;
                    }

                    await CleanupFailedCustomChildUnderJournalWriteGateAsync(
                        taskId,
                        preparation,
                        exception,
                        removeProvisionalState: false);
                },
                CancellationToken.None);

            if (recoveredHandle is not null)
            {
                return recoveredHandle;
            }

            if (scheduledHandle is not null)
            {
                try
                {
                    await scheduledHandle.CancelAsync(CancellationToken.None);
                }
                catch (Exception cancellationException)
                {
                    _shared.Logger.LogWarning(
                        cancellationException,
                        "{Id} error canceling durable child task '{TaskId}' after finalization failed.",
                        GrainId,
                        taskId);
                }
            }

            throw;
        }
    }

    private async ValueTask<CustomChildPreparation> PrepareCustomChildUnderJournalWriteGateAsync(TaskId taskId)
    {
        ThrowIfStopping();
        var stateExisted = _storage.TryGetTask(taskId, out var state);
        state ??= _storage.GetOrCreateTask(taskId, null);
        if (state.Request is not null
            || state.RequestFingerprint is not null
            || !state.CallerId.IsDefault
            || !state.RemoteTarget.IsDefault
            || state.RemoteRequestFingerprint is not null)
        {
            throw new InvalidOperationException(
                $"Durable child task '{taskId}' is already associated with a different request.");
        }

        IScheduledTaskHandle? replaceableHandle = null;
        if (stateExisted && TryGetScheduledTaskHandle(taskId, out var existingHandle))
        {
            if (existingHandle is not TaskHandle localHandle || localHandle.IsRunning)
            {
                return new CustomChildPreparation(stateExisted, state, existingHandle, null);
            }

            replaceableHandle = existingHandle;
        }

        if (state.CancellationRequestedAt.HasValue && state.Result is not { IsCompleted: true })
        {
            var canceled = DurableTaskResponse.Canceled;
            try
            {
                await SetResponseCoreAsync(
                    taskId,
                    canceled,
                    CancellationToken.None,
                    persist: true,
                    journalWriteGateHeld: true);
            }
            catch
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                throw;
            }

            return new CustomChildPreparation(
                stateExisted,
                state,
                new CompletedTaskHandle(taskId, canceled),
                null);
        }

        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (_storage.TryGetTask(ancestorId, out var ancestorState)
                && ancestorState.CancellationRequestedAt.HasValue)
            {
                if (!stateExisted)
                {
                    _storage.RemoveTask(taskId);
                    return new CustomChildPreparation(
                        stateExisted,
                        state,
                        new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled),
                        null);
                }

                _storage.RequestCancellation(taskId, state);
                var canceled = DurableTaskResponse.Canceled;
                try
                {
                    await SetResponseCoreAsync(
                        taskId,
                        canceled,
                        CancellationToken.None,
                        persist: true,
                        journalWriteGateHeld: true);
                }
                catch
                {
                    await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                    throw;
                }

                return new CustomChildPreparation(
                    stateExisted,
                    state,
                    new CompletedTaskHandle(taskId, canceled),
                    null);
            }
        }

        var deferredHandle = new DeferredScheduledTaskHandle(taskId);
        if (replaceableHandle is not null)
        {
            if (!_taskHandles.TryUpdate(taskId, deferredHandle, replaceableHandle))
            {
                return new CustomChildPreparation(
                    stateExisted,
                    state,
                    _taskHandles[taskId],
                    null);
            }
        }
        else
        {
            var registeredHandle = _taskHandles.GetOrAdd(taskId, deferredHandle);
            if (!ReferenceEquals(registeredHandle, deferredHandle))
            {
                return new CustomChildPreparation(stateExisted, state, registeredHandle, null);
            }
        }

        return new CustomChildPreparation(stateExisted, state, null, deferredHandle);
    }

    private ValueTask CleanupFailedCustomChildUnderJournalWriteGateAsync(
        TaskId taskId,
        CustomChildPreparation preparation,
        Exception exception,
        bool removeProvisionalState = true)
    {
        preparation.DeferredHandle?.SetException(exception);
        if (preparation.DeferredHandle is not null)
        {
            _taskHandles.TryRemove(
                new KeyValuePair<TaskId, IScheduledTaskHandle>(
                    taskId,
                    preparation.DeferredHandle));
        }

        if (removeProvisionalState
            && !preparation.StateExisted
            && !preparation.State.CancellationRequestedAt.HasValue)
        {
            _storage.RemoveTask(taskId);
        }

        return default;
    }

    private async ValueTask<IScheduledTaskHandle> CompleteCustomChildUnderJournalWriteGateAsync(
        TaskId taskId,
        DurableTaskResponse schedulingResponse,
        IScheduledTaskHandle? scheduledHandle,
        CustomChildPreparation preparation,
        CancellationToken cancellationToken)
    {
        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
        }

        if (state.Result is { IsCompleted: true } completedResponse)
        {
            return CompleteDeferredCustomHandle(
                taskId,
                preparation,
                new CompletedTaskHandle(taskId, completedResponse));
        }

        if (state.CancellationRequestedAt.HasValue)
        {
            var canceled = DurableTaskResponse.Canceled;
            await SetResponseCoreAsync(
                taskId,
                canceled,
                cancellationToken,
                persist: true,
                journalWriteGateHeld: true);
            return CompleteDeferredCustomHandle(
                taskId,
                preparation,
                new CompletedTaskHandle(taskId, canceled));
        }

        if (schedulingResponse.IsCompleted)
        {
            _storage.SetResponse(taskId, state, schedulingResponse);
            await WriteStateUnderJournalWriteGateAsync(cancellationToken);
            return CompleteDeferredCustomHandle(
                taskId,
                preparation,
                new CompletedTaskHandle(taskId, schedulingResponse));
        }

        var handle = scheduledHandle
            ?? throw new InvalidOperationException(
                $"The scheduler for durable child task '{taskId}' did not provide a handle.");
        await WriteStateUnderJournalWriteGateAsync(cancellationToken);
        return CompleteDeferredCustomHandle(taskId, preparation, handle);
    }

    private IScheduledTaskHandle CompleteDeferredCustomHandle(
        TaskId taskId,
        CustomChildPreparation preparation,
        IScheduledTaskHandle handle)
    {
        if (preparation.DeferredHandle is { } deferredHandle)
        {
            deferredHandle.SetResultHandle(handle);
            _taskHandles.TryUpdate(taskId, handle, deferredHandle);
        }

        return handle;
    }

    private sealed record CustomChildPreparation(
        bool StateExisted,
        IDurableTaskState State,
        IScheduledTaskHandle? Handle,
        DeferredScheduledTaskHandle? DeferredHandle);

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

    private async ValueTask<IScheduledTaskHandle> ScheduleChildUnderJournalWriteGateAsync(
        TaskId taskId,
        DurableTask durableTask,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        var stateExisted = _storage.TryGetTask(taskId, out var state);
        state ??= _storage.GetOrCreateTask(taskId, null);
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

        if (durableTask is IDurableTaskRequest remoteRequest)
        {
            var target = remoteRequest.Context?.TargetId
                ?? throw new InvalidOperationException("The durable task request has no target.");
            var fingerprint = IDurableTaskRequest.GetFingerprint(remoteRequest, _shared.Serializer);
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
        else
        {
            if (state.Request is not null
                || state.RequestFingerprint is not null
                || !state.CallerId.IsDefault
                || !state.RemoteTarget.IsDefault
                || state.RemoteRequestFingerprint is not null)
            {
                throw new InvalidOperationException(
                    $"Durable child task '{taskId}' is already associated with a different request.");
            }

        }

        if (state.CancellationRequestedAt.HasValue && state.Result is not { IsCompleted: true })
        {
            if (!state.RemoteTarget.IsDefault)
            {
                StageCancellation(taskId, state.RemoteTarget);
            }

            var canceled = DurableTaskResponse.Canceled;
            await SetResponseCoreAsync(
                taskId,
                canceled,
                cancellationToken,
                persist: true,
                journalWriteGateHeld: true);
            return new CompletedTaskHandle(taskId, canceled);
        }

        if (handle is CompletedTaskHandle)
        {
            return handle;
        }

        for (var ancestorId = taskId.Parent(); !ancestorId.IsDefault; ancestorId = ancestorId.Parent())
        {
            if (_storage.TryGetTask(ancestorId, out var ancestorState)
                && ancestorState.CancellationRequestedAt.HasValue)
            {
                if (!stateExisted)
                {
                    _storage.RemoveTask(taskId);
                }

                return new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled);
            }
        }

        if (handle is not null)
        {
            return handle;
        }

        // Durable task requests have a runtime-owned scheduling path which is safe to invoke while
        // the journal write gate is held.
        if (durableTask is IDurableTaskRequest messageRequest)
        {
            TaskHandle? transientHandle = null;
            if (_messageTransport is not null)
            {
                transientHandle = new TaskHandle(taskId, this, messageRequest.Context!.TargetId) { IsRunning = true };
                handle = _taskHandles.GetOrAdd(taskId, transientHandle);
                if (handle is TaskHandle taskHandle)
                {
                    taskHandle.IsRunning = true;
                }
            }

            try
            {
                var schedulingResponse = await ScheduleRemoteUnderJournalWriteGateAsync(
                    taskId,
                    messageRequest,
                    cancellationToken);
                if (schedulingResponse.IsCompleted)
                {
                    _storage.SetResponse(taskId, state, schedulingResponse);
                    await WriteStateUnderJournalWriteGateAsync(cancellationToken);
                    if (handle is TaskHandle completedHandle)
                    {
                        completedHandle.TrySetResponse(schedulingResponse);
                    }
                    return new CompletedTaskHandle(taskId, schedulingResponse);
                }

                if (state.Result is { IsCompleted: true } completedResponse)
                {
                    return new CompletedTaskHandle(taskId, completedResponse);
                }

                return handle!;
            }
            catch
            {
                if (transientHandle is not null
                    && _taskHandles.TryGetValue(taskId, out var current)
                    && ReferenceEquals(current, transientHandle))
                {
                    _taskHandles.TryRemove(taskId, out _);
                }

                throw;
            }
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.
        if (!stateExisted)
        {
            try
            {
                await WriteStateUnderJournalWriteGateAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                _storage.RemoveTask(taskId);
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                if (_taskHandles.TryRemove(taskId, out var failedHandle)
                    && failedHandle is TaskHandle taskHandle)
                {
                    taskHandle.TrySetException(exception);
                }

                throw;
            }
        }

        var executionContext = CreateExecutionContext(taskId);
        handle = GetOrCreateRunningTaskHandle(taskId);
        StartInvocation(taskId, static task => task, durableTask, executionContext);
        return handle;
    }

    private void StartInvocation<TState>(
        TaskId taskId,
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context)
    {
        var recoveryGeneration = Volatile.Read(ref _recoveryGeneration);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Invoke(createTask, state, context, recoveryGeneration, start.Task);
        _runningRequests[taskId] = new RunningRequest(invocation, context, recoveryGeneration);
        start.SetResult();
    }

    private async Task Invoke<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context,
        long recoveryGeneration,
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
            if (response.IsCompleted
                && !_stopping
                && recoveryGeneration == Volatile.Read(ref _recoveryGeneration))
            {
                await SetResponseAsync(
                    context.TaskId,
                    response,
                    CancellationToken.None,
                    expectedRecoveryGeneration: recoveryGeneration);
            }
        }
        finally
        {
            if (_runningRequests.TryGetValue(context.TaskId, out var running)
                && running.RecoveryGeneration == recoveryGeneration
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
        GrainDurableExecutionContext Context,
        long RecoveryGeneration);

    private async Task SetResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken,
        bool persist = true,
        long? expectedRecoveryGeneration = null)
    {
        await _responseCommitGate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
            {
                return;
            }

            if (expectedRecoveryGeneration is { } generation
                && persist)
            {
                await _journalWriteGate.WaitAsync(cancellationToken);
                try
                {
                    if (generation != Volatile.Read(ref _recoveryGeneration))
                    {
                        return;
                    }

                    await SetResponseCoreAsync(
                        taskId,
                        response,
                        cancellationToken,
                        persist: true,
                        journalWriteGateHeld: true);
                    return;
                }
                finally
                {
                    _journalWriteGate.Release();
                }
            }

            if (expectedRecoveryGeneration is { } expectedGeneration
                && expectedGeneration != Volatile.Read(ref _recoveryGeneration))
            {
                return;
            }

            await SetResponseCoreAsync(
                taskId,
                response,
                cancellationToken,
                persist,
                journalWriteGateHeld: false);
        }
        catch
        {
            if (persist
                && (expectedRecoveryGeneration is not { } expectedGeneration
                    || expectedGeneration == Volatile.Read(ref _recoveryGeneration)))
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
            }

            throw;
        }
        finally
        {
            _responseCommitGate.Release();
        }
    }

    private async Task SetResponseCoreAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken,
        bool persist,
        bool journalWriteGateHeld)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can complete a task.");
        }

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} task {TaskId} completed with result '{Result}'.", GrainId, taskId, response);
        }

        // Only update the result if an existing result has not been set. If this were to overwrite an already-persisted result,
        // that could cause the result to appear to change after it has already been observed.
        // This condition guards against the case where a scheduling call fails after the response has already been received via an OnResponse callback,
        // which could occur due to a recovery retry or concurrency (multiple clients scheduling the same workflow).
        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
        }

        var winningResponse = state.Result is { IsCompleted: true } terminal ? terminal : response;
        if (state.Result is not { IsCompleted: true })
        {
            if (state is DurableTaskState durableState)
            {
                durableState.MigrateLegacyObservers();
            }

            if (state.CompletionDestinations.Count > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                foreach (var destination in state.CompletionDestinations)
                {
                    transport.SendCompletion(GrainId, destination, taskId, response);
                }

            }

            _storage.SetResponse(taskId, state, response);
            if (persist)
            {
                if (journalWriteGateHeld)
                {
                    await WriteStateUnderJournalWriteGateAsync(cancellationToken);
                }
                else
                {
                    await WriteStateAsync(cancellationToken);
                }
            }
        }

        if (persist)
        {
            CompleteTaskHandle(taskId, winningResponse);
            if (PruneCompletedTasks())
            {
                if (journalWriteGateHeld)
                {
                    await WriteStateUnderJournalWriteGateAsync(cancellationToken);
                }
                else
                {
                    await WriteStateAsync(cancellationToken);
                }
            }
        }
        else
        {
            _pendingHandleResponses[taskId] = winningResponse;
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

        if (_storage.TryGetTask(taskId, out var taskState) && taskState.TombstonedAt.HasValue)
        {
            return DurableTaskTerminalFailure.CreateResponse(taskId);
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

    public async ValueTask SignalCancellationAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await Volatile.Read(ref _recoveryReconciliation).WaitAsync(cancellationToken);
        await SignalCancellationAsync(taskId, callerId: default, cancellationToken);
    }

    internal async ValueTask SignalCancellationFromInboxAsync(
        TaskId taskId,
        GrainId callerId,
        CancellationToken cancellationToken)
    {
        await _journalWriteGate.WaitAsync(cancellationToken);
        try
        {
            lock (_inboxCancellationLock)
            {
                _inboxCancellationCommitOwners.Add(taskId);
            }

            var propagation = StageCancellationTree(taskId, callerId);
            lock (_inboxCancellationLock)
            {
                _inboxCancellationPropagations[taskId] = (propagation.Contexts, propagation.Handles);
            }
        }
        catch
        {
            lock (_inboxCancellationLock)
            {
                _inboxCancellationCommitOwners.Remove(taskId);
            }

            _journalWriteGate.Release();
            throw;
        }
    }

    internal void CompleteInboxCancellationHandling(TaskId taskId)
    {
        lock (_inboxCancellationLock)
        {
            if (_inboxCancellationCommitOwners.Contains(taskId))
            {
                _readyInboxCancellationCommits.Add(taskId);
            }
        }
    }

    private async ValueTask SignalCancellationAsync(
        TaskId taskId,
        GrainId callerId,
        CancellationToken cancellationToken)
    {
        var propagation = await WithJournalWriteGateAsync(
            () => StageAndCommitCancellationTreeAsync(taskId, callerId, cancellationToken),
            cancellationToken);
        await PropagateCancellationAsync(propagation.Contexts, propagation.Handles, cancellationToken);
    }

    private async ValueTask<(List<GrainDurableExecutionContext> Contexts, List<IScheduledTaskHandle> Handles)> StageAndCommitCancellationTreeAsync(
        TaskId taskId,
        GrainId callerId,
        CancellationToken cancellationToken)
    {
        var propagation = StageCancellationTree(taskId, callerId);
        if (propagation.Changed)
        {
            try
            {
                await WriteStateUnderJournalWriteGateAsync(cancellationToken);
            }
            catch
            {
                await _stateManager.RevertPendingChangesAsync(CancellationToken.None);
                throw;
            }
        }

        return (propagation.Contexts, propagation.Handles);
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
            ValidateOrSetCaller(taskId, taskState, callerId);
            _storage.RequestCancellation(taskId, taskState);
            return (true, [], []);
        }

        ValidateOrSetCaller(taskId, taskState, callerId);
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

            foreach (var (childTaskId, childTaskState) in _storage.GetChildren(currentTaskId))
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
                handle = new CompletedTaskHandle(taskId, DurableTaskTerminalFailure.CreateResponse(taskId));
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

    internal Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_stopLock)
        {
            return _stopTask ??= StopCoreAsync(cancellationToken);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _responseCommitGate.WaitAsync(CancellationToken.None);
        try
        {
            _stopping = true;
        }
        finally
        {
            _responseCommitGate.Release();
        }

        await _deactivationCts.CancelAsync();
        while (!_pendingHandleResponses.IsEmpty || !_committingHandleResponses.IsEmpty)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

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
            await Task.WhenAll(running);
        }
    }

    private bool IsRequestRunning(TaskId taskId) => _runningRequests.ContainsKey(taskId);

    private void ThrowIfStopping()
    {
        if (Volatile.Read(ref _initialized) == 0)
        {
            throw new InvalidOperationException(
                "Durable Tasks requires grain activations to derive from Orleans.Journaling.DurableGrain so journal participation is initialized.");
        }

        if (_stopping)
        {
            throw new OperationCanceledException(
                "The durable task runtime is stopping and cannot accept new work.",
                _deactivationCts.Token);
        }
    }
}
