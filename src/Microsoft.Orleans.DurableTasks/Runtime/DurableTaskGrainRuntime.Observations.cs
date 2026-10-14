using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// An observation lets a durable execution await the terminal result of an invoked task which it didn't invoke. The
/// observing grain records the observation as a child of the execution, whose fingerprint names the observed grain and
/// task, and stages a subscription to the observed grain in the same journal write. Like a delay, the observation is a
/// cancellation leaf: its own cancellation, its ancestors' cancellation, an abort of its tree, or deletion of its tree
/// terminalizes it without reaching the observed task, and ends its subscription.
/// </para>
/// <para>
/// The observed grain records each subscription as a terminal record in the observed task's execution tree, the child
/// <c>$observer:{hash}</c>, whose fingerprint names the observing grain and observation. When the observed task stores its
/// result, the write sends a completion to each subscribed observation through the outbox, as it does to the task's
/// caller, and the record retains the task and its result until that completion retires. A subscription to a task which
/// is already terminal sends its completion at once, and one to a task which the grain doesn't retain as invoked receives
/// a completion with <see cref="DurableTaskNotFoundException"/>. A subscription can arrive before the invocation of the
/// task which it observes, so one for a task which isn't stored waits in the task's tree for Messaging's
/// <c>OutboxRetryTimeout</c>, after which the invocation can no longer arrive and the observation completes as not found.
/// An observation of a task of the same grain subscribes and
/// completes directly in task state, in the write which stores the observed result.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // Execution-tree records begin with '$', so neither collides with an explicitly named child or a request fingerprint.
    private const string ObservationFingerprintPrefix = "$observe:";
    private const string ObserverFingerprintPrefix = "$observer:";
    private const string ObserverSegmentPrefix = "$observer:";

    /// <summary>Schedules, or reaches the record of, an observation under <paramref name="taskId"/>.</summary>
    /// <remarks>
    /// The record and its subscription commit in one journal write, so a replay which finds the record doesn't subscribe
    /// again; it awaits that write, which an earlier attempt can still be awaiting, and the recorded outcome.
    /// </remarks>
    private async ValueTask<IScheduledTaskHandle> ScheduleObservationAsync(
        TaskId taskId,
        IDurableTaskObservation observation,
        bool suppressCancellation,
        CancellationToken cancellationToken)
    {
        var fingerprint = FormatIdentity(ObservationFingerprintPrefix, observation.Target, observation.TaskId);
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (!string.Equals(state.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw CreateDivergenceException(
                    $"Durable child task '{taskId}' is already associated with a different operation.",
                    state,
                    DescribeObservation(observation.Target, observation.TaskId));
            }

            if (state.Result is { IsCompleted: true } recorded)
            {
                return new CompletedTaskHandle(taskId, recorded);
            }

            if (state.CancellationRequestedAt.HasValue)
            {
                return new CompletedTaskHandle(
                    taskId,
                    await CommitResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken));
            }

            var existing = GetScheduledTaskHandle(taskId);
            await WriteStateAsync(cancellationToken);
            return existing;
        }

        // An observation of a canceled ancestor is not created, unless the parent scheduled it in a cancellation
        // suppression scope.
        if (IsAncestorCancellationRequested(taskId, state: null, suppressCancellation))
        {
            return new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled);
        }

        // Retention ends at a deadline, so an observation of a task of this grain doesn't find a task whose retention ended.
        DeleteExpiredTasks();
        var transport = MessageTransport;
        DurableTaskResponse? response = null;
        if (observation.Target == GrainId)
        {
            // The observed result is read before anything is staged, so a result which no longer decodes fails the
            // scheduling without recording the observation.
            response = SubscribeObserver(observation.TaskId, GrainId, taskId) switch
            {
                ObserverSubscription.NotFound => CreateObservationNotFound(observation.TaskId),
                ObserverSubscription.Terminal => _storage.TryGetTask(observation.TaskId, out var observed) ? observed.Result! : null,
                _ => null,
            };
        }
        else
        {
            transport.SendObservation(GrainId, observation.Target, observation.TaskId, taskId, unsubscribe: false);
        }

        state = _storage.GetOrCreateTask(taskId, request: null);
        _storage.SetRequestFingerprint(taskId, state, fingerprint);
        MarkCancellationSuppressed(taskId, state, suppressCancellation);
        var handle = GetOrCreateRunningTaskHandle(taskId);
        if (response is not null)
        {
            StoreResult(taskId, state, response, observed: true);
            _pendingHandleResponses[taskId] = response;
        }

        await WriteStateAsync(cancellationToken);
        return handle;
    }

    /// <summary>
    /// Stages the outcome of the observation <paramref name="observerId"/> which <paramref name="sender"/> delivered,
    /// completing the observation's handle after commit.
    /// </summary>
    /// <remarks>
    /// The inbox accepts the completion directly into task state. An observation which was deleted with its tree, or which
    /// already ended, for example because it was canceled, accepts the completion without changing state.
    /// </remarks>
    internal void AcceptObservationFromInbox(TaskId observerId, TaskId taskId, DurableTaskResponse response, GrainId sender)
    {
        ThrowIfStopping();
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can complete an observation.");
        }

        if (!_storage.TryGetTask(observerId, out var state))
        {
            return;
        }

        if (!TryParseIdentity(ObservationFingerprintPrefix, state.RequestFingerprint, out var target, out var observedId)
            || target != sender
            || observedId != taskId)
        {
            throw new InvalidOperationException(
                $"Durable task '{observerId}' does not observe task '{taskId}' of grain '{sender}'.");
        }

        if (HasStoredResult(state))
        {
            return;
        }

        StoreResult(observerId, state, response, observed: true);
        _pendingHandleResponses[observerId] = response;
    }

    /// <summary>
    /// Records a subscription of <paramref name="observer"/>'s observation <paramref name="observerId"/> which the inbox
    /// delivered, sending the completion at once when the task is terminal or isn't retained as an invoked task.
    /// </summary>
    internal void SubscribeObservationFromInbox(TaskId taskId, TaskId observerId, GrainId observer)
    {
        ThrowIfStopping();
        if (observer == GrainId)
        {
            throw new InvalidOperationException(
                $"Durable task '{observerId}' observes a task of its own grain '{GrainId}' without durable messaging.");
        }

        // Retention ends at a deadline, so a subscription doesn't find a task whose retention ended.
        DeleteExpiredTasks();
        switch (SubscribeObserver(taskId, observer, observerId, fromInbox: true))
        {
            case ObserverSubscription.NotFound:
                MessageTransport.SendObservationNotFound(GrainId, observer, taskId, observerId);
                break;
            case ObserverSubscription.Terminal:
                SendObservationCompletion(taskId, observer, observerId);
                break;
        }
    }

    /// <summary>
    /// Removes a subscription which the observation ended before the observed task completed. A terminal task's completion
    /// is already pending, and retires as usual.
    /// </summary>
    /// <remarks>
    /// The outbox delivers a subscription and its end independently. An end which arrives first finds no record, so the
    /// subscription which follows it receives a completion which the ended observation ignores.
    /// </remarks>
    internal void UnsubscribeObservationFromInbox(TaskId taskId, TaskId observerId, GrainId observer)
    {
        ThrowIfStopping();
        RemoveObserver(taskId, observer, observerId);
    }

    /// <summary>
    /// Records a subscription to <paramref name="taskId"/> unless the observation completes at once, without writing.
    /// </summary>
    /// <param name="fromInbox">
    /// <see langword="true"/> when the subscription was delivered through the inbox, so it can precede the task's
    /// invocation.
    /// </param>
    /// <returns>
    /// <see cref="ObserverSubscription.Pending"/> when the observation awaits the task's result,
    /// <see cref="ObserverSubscription.Terminal"/> when the task has stored its result, or
    /// <see cref="ObserverSubscription.NotFound"/> when this grain doesn't retain the task as an invoked task. Another
    /// grain's subscription to a terminal task is recorded, so the task retains its result until the completion retires;
    /// this grain's own observation reads the result directly. A subscription which was recorded already, because the
    /// inbox delivered it again, is <see cref="ObserverSubscription.Pending"/>, and its completion, if any, is pending.
    /// </returns>
    /// <remarks>
    /// The outbox delivers messages from different senders, and from one sender, independently, so a subscription can
    /// arrive before the invocation of the task which it observes, for example when the invoking grain started the task
    /// and the observing execution in one write. A delivered subscription for a task which isn't stored, or which records
    /// only a cancellation that preceded its invocation, is therefore recorded for that invocation, which finds the
    /// record in its tree. The record is deleted, and the observation completes as not found, once Messaging's
    /// <c>OutboxRetryTimeout</c> has elapsed without the invocation, after which it can no longer arrive.
    /// </remarks>
    private ObserverSubscription SubscribeObserver(TaskId taskId, GrainId observer, TaskId observerId, bool fromInbox = false)
    {
        var found = _storage.TryGetTask(taskId, out var state);
        var precedesInvocation = fromInbox && (!found || IsCancellationRecord(state!));
        if (!precedesInvocation && (!found || !IsInvoked(state!)))
        {
            return ObserverSubscription.NotFound;
        }

        var terminal = !precedesInvocation && HasStoredResult(state!);
        if (terminal && observer == GrainId)
        {
            return ObserverSubscription.Terminal;
        }

        var recordId = GetObserverRecordId(taskId, observer, observerId);
        if (_storage.TryGetTask(recordId, out var record))
        {
            Debug.Assert(IsObserverRecord(record), "Observer records are named by their observer.");
            return ObserverSubscription.Pending;
        }

        record = _storage.GetOrCreateTask(recordId, request: null);
        _storage.SetRequestFingerprint(recordId, record, FormatIdentity(ObserverFingerprintPrefix, observer, observerId));
        _storage.SetResponse(recordId, record, DurableTaskResponse.Completed);
        if (precedesInvocation)
        {
            // The grain applies retention after each write, and a resume message which it schedules for itself at the
            // record's expiry wakes it then, so the observation learns of the missing task while the grain is otherwise
            // idle. The resume finds a terminal record and changes nothing.
            var expiry = GetEarlyObserverExpiry(record);
            TrackExpiry(expiry);
            MessageTransport.SendResume(GrainId, recordId, generation: 1, expiry);
        }

        return terminal ? ObserverSubscription.Terminal : ObserverSubscription.Pending;
    }

    /// <summary>Returns when a subscription which arrived before its task's invocation ends unless the invocation arrives.</summary>
    private DateTimeOffset GetEarlyObserverExpiry(IDurableTaskState record) =>
        AddSaturating(record.CreatedAt, _shared.OutboxRetryTimeout);

    /// <summary>
    /// Completes the observation of a subscription which is deleted before the observed task stored a result, because the
    /// task's invocation didn't arrive, as not found.
    /// </summary>
    private void DropObserver(TaskId recordId, IDurableTaskState record)
    {
        TryParseIdentity(ObserverFingerprintPrefix, record.RequestFingerprint, out var observer, out var observerId);
        var taskId = recordId.Parent();
        if (observer != GrainId)
        {
            MessageTransport.SendObservationNotFound(GrainId, observer, taskId, observerId);
        }
        else if (_storage.TryGetTask(observerId, out var observation) && !HasStoredResult(observation))
        {
            var response = CreateObservationNotFound(taskId);
            StoreResult(observerId, observation, response, observed: true);
            _pendingHandleResponses[observerId] = response;
        }
    }

    private enum ObserverSubscription
    {
        Pending,
        Terminal,
        NotFound,
    }

    private static DurableTaskResponse CreateObservationNotFound(TaskId taskId) =>
        DurableTaskResponse.FromException(new DurableTaskNotFoundException(taskId));

    private void RemoveObserver(TaskId taskId, GrainId observer, TaskId observerId)
    {
        var recordId = GetObserverRecordId(taskId, observer, observerId);
        if (_storage.TryGetTask(recordId, out _)
            && (!_storage.TryGetTask(taskId, out var state) || !HasStoredResult(state)))
        {
            _storage.RemoveTask(recordId);
        }
    }

    /// <summary>
    /// Ends the subscription of an observation which ends without the observed result, such as a canceled, aborted, or
    /// deleted observation.
    /// </summary>
    private void Unsubscribe(TaskId observerId, IDurableTaskState state)
    {
        var parsed = TryParseIdentity(ObservationFingerprintPrefix, state.RequestFingerprint, out var target, out var taskId);
        Debug.Assert(parsed, "Only observations have subscriptions.");
        if (target == GrainId)
        {
            RemoveObserver(taskId, GrainId, observerId);
        }
        else
        {
            MessageTransport.SendObservation(GrainId, target, taskId, observerId, unsubscribe: true);
        }
    }

    /// <summary>
    /// Completes the observations of an invoked task whose result is being stored: an observation of this grain directly,
    /// in the same write, and an observation of another grain with a completion which retains the result until it
    /// retires.
    /// </summary>
    private void CompleteObservers(TaskId taskId, DurableTaskResponse response)
    {
        List<(TaskId Id, IDurableTaskState State)>? records = null;
        foreach (var child in _storage.GetChildren(taskId))
        {
            if (IsObserverRecord(child.State))
            {
                (records ??= []).Add(child);
            }
        }

        if (records is null)
        {
            return;
        }

        foreach (var (recordId, record) in records)
        {
            TryParseIdentity(ObserverFingerprintPrefix, record.RequestFingerprint, out var observer, out var observerId);
            if (observer != GrainId)
            {
                SendObservationCompletion(taskId, observer, observerId);
                continue;
            }

            _storage.RemoveTask(recordId);
            if (_storage.TryGetTask(observerId, out var observation) && !HasStoredResult(observation))
            {
                StoreResult(observerId, observation, response, observed: true);
                _pendingHandleResponses[observerId] = response;
            }
        }
    }

    private void SendObservationCompletion(TaskId taskId, GrainId observer, TaskId observerId)
    {
        var transport = MessageTransport;
        var messageId = transport.GetObservationCompletionMessageId(GrainId, observer, taskId, observerId);
        PendingCompletions[messageId] = new(taskId, observer, observerId);
        transport.SendCompletion(messageId);
    }

    /// <summary>Returns whether a task has subscribed observations, whose records retain the task.</summary>
    private bool HasObservers(TaskId taskId)
    {
        foreach (var (_, child) in _storage.GetChildren(taskId))
        {
            if (IsObserverRecord(child))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns whether a task is an observation of another invoked task.</summary>
    private static bool IsObservation(IDurableTaskState state) =>
        state.RequestFingerprint is { } fingerprint
        && fingerprint.StartsWith(ObservationFingerprintPrefix, StringComparison.Ordinal);

    /// <summary>Returns whether a task records an observation's subscription to its parent.</summary>
    private static bool IsObserverRecord(IDurableTaskState state) =>
        state.RequestFingerprint is { } fingerprint
        && fingerprint.StartsWith(ObserverFingerprintPrefix, StringComparison.Ordinal);

    private static bool TryGetObserver(TaskId recordId, IDurableTaskState record, out PendingCompletion completion)
    {
        if (TryParseIdentity(ObserverFingerprintPrefix, record.RequestFingerprint, out var observer, out var observerId))
        {
            completion = new(recordId.Parent(), observer, observerId);
            return true;
        }

        completion = default;
        return false;
    }

    /// <summary>Gets the record of <paramref name="observer"/>'s observation <paramref name="observerId"/> of a task.</summary>
    /// <remarks>The segment hashes the observation's identity, so its length doesn't depend on the observer's identifiers.</remarks>
    private static TaskId GetObserverRecordId(TaskId taskId, GrainId observer, TaskId observerId)
    {
        var identity = Encoding.UTF8.GetBytes(FormatIdentity(ObserverFingerprintPrefix, observer, observerId));
        return taskId.Child(ObserverSegmentPrefix + Convert.ToHexString(SHA256.HashData(identity)));
    }

    /// <summary>Formats a grain and a task as <c>{prefix}{grain length}:{grain}{task}</c>, which parses unambiguously.</summary>
    private static string FormatIdentity(string prefix, GrainId grain, TaskId taskId)
    {
        var grainId = grain.ToString();
        return $"{prefix}{grainId.Length.ToString(CultureInfo.InvariantCulture)}:{grainId}{taskId}";
    }

    private static bool TryParseIdentity(string prefix, string? value, out GrainId grain, out TaskId taskId)
    {
        grain = default;
        taskId = default;
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = value.AsSpan(prefix.Length);
        var separator = rest.IndexOf(':');
        if (separator <= 0
            || !int.TryParse(rest[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || length > rest.Length - separator - 1)
        {
            return false;
        }

        rest = rest[(separator + 1)..];
        return GrainId.TryParse(rest[..length], provider: null, out grain)
            && TaskId.TryParse(rest[length..], out taskId);
    }

    private static string DescribeObservation(GrainId target, TaskId taskId) =>
        $"an observation of task '{taskId}' on grain '{target}'";
}
