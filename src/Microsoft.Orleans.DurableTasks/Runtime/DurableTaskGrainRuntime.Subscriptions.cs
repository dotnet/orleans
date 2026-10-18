using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// A subscription lets a durable execution await the terminal result of a task which it didn't start as an attached
/// child: an existing invoked task, which <c>GetDurableTask</c> addresses, or a detached task which the execution starts.
/// The subscribing grain records a child of the execution, whose fingerprint names the task's grain and identifier, and
/// stages the subscription in the same journal write: an invocation message which carries the child's identifier, so it
/// neither claims the task nor validates its caller, and which carries the request of a detached task. Like a delay, the
/// child is a cancellation leaf: its own cancellation, its ancestors' cancellation, an abort of its tree, or deletion of
/// its tree terminalizes it without reaching the task, and stages an unsubscription, a cancellation message which names
/// the child.
/// </para>
/// <para>
/// The task's grain adds the child to the task's completion destinations, beside the task's invoker, so the task's
/// result reaches it as it reaches the invoker: through the outbox, with the task retained until the completion retires.
/// A subscription to a terminal task sends its completion at once, and one to a task which the grain doesn't retain as
/// an invoked task receives a completion with <see cref="DurableTaskNotFoundException"/> at once. A task identifier
/// which a durable execution can hold refers to a task which its grain already accepted: a schedule which a client, a
/// grain, or a durable execution awaited, or a detached task which the execution started itself, whose invocation names
/// the child. A subscription to a task of the same grain is staged directly in task state, and completes in the write
/// which stores the task's result.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // Execution-tree records begin with '$', so a subscription never collides with a request fingerprint.
    private const string SubscriptionFingerprintPrefix = "$subscribe:";

    /// <summary>
    /// Stages a subscription child under <paramref name="taskId"/>, or reaches its record, without writing, and returns
    /// its handle and whether it staged changes.
    /// </summary>
    /// <remarks>
    /// The record and its subscription commit in one journal write, so a replay which finds the record doesn't subscribe
    /// again; it awaits that write, which an earlier attempt can still be awaiting, and the recorded outcome.
    /// </remarks>
    private StagedChild StageSubscription(TaskId taskId, IDurableTaskSubscription subscription, bool shielded)
    {
        var fingerprint = FormatIdentity(SubscriptionFingerprintPrefix, subscription.Target, subscription.Id);
        if (TryGetRecordedSubscription(taskId, fingerprint, DescribeSubscription(subscription.Target, subscription.Id)) is { } recorded)
        {
            return recorded;
        }

        // A subscription of a canceled ancestor is not created, unless the parent started it shielded.
        if (IsAncestorCancellationRequested(taskId, state: null, shielded))
        {
            return new(new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled), RequiresWrite: false);
        }

        // Retention ends at a deadline, so a subscription to a task of this grain doesn't find a task whose retention ended.
        DeleteExpiredTasks();
        DurableTaskResponse? response = null;
        if (subscription.Target == GrainId)
        {
            // The subscribed result is read before the child is staged, so a result which no longer decodes fails the
            // scheduling without recording the child.
            response = SubscribeLocal(subscription.Id, taskId);
        }
        else
        {
            MessageTransport.SendSubscription(GrainId, subscription.Target, subscription.Id, taskId);
        }

        return new(StageSubscriptionRecord(taskId, fingerprint, shielded, response), RequiresWrite: true);
    }

    /// <summary>
    /// Returns the handle of the subscription child recorded under <paramref name="taskId"/>, or <see langword="null"/>
    /// when there's no record.
    /// </summary>
    /// <exception cref="InvalidOperationException">The record holds another operation.</exception>
    private StagedChild? TryGetRecordedSubscription(TaskId taskId, string fingerprint, string requested)
    {
        if (!_storage.TryGetTask(taskId, out var state))
        {
            return null;
        }

        if (!string.Equals(state.RequestFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw CreateDivergenceException(
                $"Durable child task '{taskId}' is already associated with a different operation.",
                state,
                requested);
        }

        if (ReadRecordedResult(taskId, state) is { IsCompleted: true } recorded)
        {
            return new(new CompletedTaskHandle(taskId, recorded), RequiresWrite: false);
        }

        if (state.CancellationRequestedAt.HasValue)
        {
            var canceled = StageResponse(taskId, DurableTaskResponse.Canceled);
            _pendingHandleResponses[taskId] = canceled;
            return new(new CompletedTaskHandle(taskId, canceled), RequiresWrite: true);
        }

        return new(GetScheduledTaskHandle(taskId), RequiresWrite: true);
    }

    /// <summary>
    /// Subscribes this grain's child <paramref name="subscriberId"/> to this grain's task <paramref name="taskId"/>, and
    /// returns the task's terminal result, the not-found failure, or <see langword="null"/> while the task is pending.
    /// </summary>
    private DurableTaskResponse? SubscribeLocal(TaskId taskId, TaskId subscriberId) =>
        Subscribe(taskId, new(GrainId, subscriberId)) switch
        {
            SubscriptionOutcome.NotFound => CreateNotFound(taskId),
            SubscriptionOutcome.Terminal => _storage.TryGetTask(taskId, out var subscribed) ? subscribed.Result! : null,
            _ => null,
        };

    /// <summary>
    /// Stages the record of a subscription child, completing it with <paramref name="response"/> when the subscribed task
    /// is of this grain and has a result, and returns its handle.
    /// </summary>
    private IScheduledTaskHandle StageSubscriptionRecord(TaskId taskId, string fingerprint, bool shielded, DurableTaskResponse? response)
    {
        var state = GetOrCreateTask(taskId, request: null);
        _storage.SetRequestFingerprint(taskId, state, fingerprint);
        MarkShielded(taskId, state, shielded);
        var handle = GetOrCreateRunningTaskHandle(taskId);
        if (response is not null)
        {
            StoreResult(taskId, state, response, subscribed: true);
            _pendingHandleResponses[taskId] = response;
        }

        return handle;
    }

    /// <summary>Adds the subscriber which the inbox delivered to the task's completion destinations.</summary>
    /// <remarks>
    /// A subscription to a terminal task sends its completion at once, and one to a stored task which isn't invoked sends
    /// a completion with <see cref="DurableTaskNotFoundException"/>. A redelivered subscription changes nothing.
    /// </remarks>
    internal void SubscribeFromInbox(TaskId taskId, DurableTaskSubscriber subscriber)
    {
        ThrowIfStopping();
        if (subscriber.GrainId == GrainId)
        {
            throw new InvalidOperationException(
                $"Durable task '{subscriber.TaskId}' subscribes to a task of its own grain '{GrainId}' without durable messaging.");
        }

        // Retention ends at a deadline, so a subscription doesn't find a task whose retention ended.
        DeleteExpiredTasks();
        if (Subscribe(taskId, subscriber) == SubscriptionOutcome.NotFound)
        {
            MessageTransport.SendResponse(GrainId, subscriber, taskId, CreateNotFound(taskId));
        }
    }

    /// <summary>
    /// Removes a subscriber which ended before the task completed. A terminal task's completion is already pending, and
    /// retires as usual.
    /// </summary>
    /// <remarks>
    /// The outbox delivers a subscription and its end independently. An end which arrives first finds no subscriber, so
    /// the subscription which follows it receives a completion which the ended child ignores.
    /// </remarks>
    internal void UnsubscribeFromInbox(TaskId taskId, DurableTaskSubscriber subscriber)
    {
        ThrowIfStopping();
        RemoveSubscriber(taskId, subscriber);
    }

    /// <summary>Adds <paramref name="subscriber"/> to <paramref name="taskId"/>'s completion destinations, without writing.</summary>
    /// <returns>
    /// <see cref="SubscriptionOutcome.Pending"/> when the subscriber awaits the task's result,
    /// <see cref="SubscriptionOutcome.Terminal"/> when the task has stored its result, or
    /// <see cref="SubscriptionOutcome.NotFound"/> when this grain doesn't retain the task as an invoked task. A subscriber
    /// on another grain of a terminal task receives a completion through the outbox; a subscriber on this grain reads the
    /// result directly.
    /// </returns>
    private SubscriptionOutcome Subscribe(TaskId taskId, DurableTaskSubscriber subscriber)
    {
        if (!_storage.TryGetTask(taskId, out var state) || !IsInvoked(state))
        {
            return SubscriptionOutcome.NotFound;
        }

        if (HasStoredResult(state))
        {
            if (subscriber.GrainId != GrainId && TryRegisterCompletionDestination(taskId, state, subscriber))
            {
                SendCompletion(taskId, subscriber);
            }

            return SubscriptionOutcome.Terminal;
        }

        _storage.AddCompletionDestination(taskId, state, subscriber);
        return SubscriptionOutcome.Pending;
    }

    private enum SubscriptionOutcome
    {
        Pending,
        Terminal,
        NotFound,
    }

    /// <summary>Removes a subscriber of a task which hasn't stored its result, without writing.</summary>
    /// <remarks>The task's invoker isn't a subscriber which an unsubscription removes.</remarks>
    private void RemoveSubscriber(TaskId taskId, DurableTaskSubscriber subscriber)
    {
        if (!_storage.TryGetTask(taskId, out var state)
            || HasStoredResult(state)
            || subscriber == GetInvoker(state.CallerId, taskId)
            || !state.CompletionDestinations.Contains(subscriber))
        {
            return;
        }

        _storage.RemoveCompletionDestination(taskId, state, subscriber);
    }

    /// <summary>
    /// Ends the subscription of child <paramref name="subscriberId"/> to <paramref name="taskId"/> of
    /// <paramref name="target"/>, because the child ended without the task's result, for example when it was canceled.
    /// </summary>
    private void Unsubscribe(GrainId target, TaskId taskId, TaskId subscriberId)
    {
        if (target == GrainId)
        {
            RemoveSubscriber(taskId, new(GrainId, subscriberId));
        }
        else
        {
            MessageTransport.SendUnsubscription(GrainId, target, taskId, subscriberId);
        }
    }

    /// <summary>
    /// Stores <paramref name="response"/>, the result of <paramref name="sourceTaskId"/>, as the result of the subscription
    /// child <paramref name="subscriberId"/>, completing its handle after commit.
    /// </summary>
    /// <remarks>A child which was deleted with its tree, or which already ended, ignores the result.</remarks>
    private void CompleteSubscription(TaskId subscriberId, TaskId sourceTaskId, DurableTaskResponse response)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can complete a subscription.");
        }

        if (!_storage.TryGetTask(subscriberId, out var state) || HasStoredResult(state))
        {
            return;
        }

        Debug.Assert(
            TryGetSubscription(state, out _, out var subscribedTaskId) && subscribedTaskId == sourceTaskId,
            "A subscriber is a subscription child of the task.");
        StoreResult(subscriberId, state, response, subscribed: true);
        _pendingHandleResponses[subscriberId] = response;
    }

    private static DurableTaskResponse CreateNotFound(TaskId taskId) =>
        DurableTaskResponse.FromException(new DurableTaskNotFoundException(taskId));

    /// <summary>
    /// Returns whether a task is a subscription child, including the record of a detached task, and the task to which it
    /// subscribes.
    /// </summary>
    private static bool TryGetSubscription(IDurableTaskState state, out GrainId target, out TaskId taskId) =>
        TryParseIdentity(SubscriptionFingerprintPrefix, state.RequestFingerprint, out target, out taskId)
        || TryGetDetachedTask(state, out target, out taskId);

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

    private static string DescribeSubscription(GrainId target, TaskId taskId) =>
        $"a subscription to task '{taskId}' on grain '{target}'";

    /// <summary>Describes a subscriber of <paramref name="taskId"/>: its grain, followed by its task when it isn't the invoker's.</summary>
    private static string DescribeSubscriber(TaskId taskId, DurableTaskSubscriber subscriber) =>
        subscriber.TaskId == taskId ? subscriber.GrainId.ToString() : $"{subscriber.GrainId} {subscriber.TaskId}";
}
