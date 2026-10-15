using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// A subscription lets a durable execution await the terminal result of an existing invoked task which it didn't invoke.
/// The subscribing grain records a child of the execution, whose fingerprint names the task's grain and identifier, and
/// stages the subscription in the same journal write: an invocation message which carries the child's identifier
/// instead of a request, so it neither schedules nor claims the task. Like a delay, the child is a cancellation leaf: its
/// own cancellation, its ancestors' cancellation, an abort of its tree, or deletion of its tree terminalizes it without
/// reaching the task, and stages an unsubscription, a cancellation message which names the child.
/// </para>
/// <para>
/// The task's grain adds the child to the task's completion destinations, beside the task's invoker, so the task's
/// result reaches it as it reaches the invoker: through the outbox, with the task retained until the completion retires.
/// A subscription to a terminal task sends its completion at once, and one to a stored task which isn't invoked
/// receives a completion with <see cref="DurableTaskNotFoundException"/>. The outbox delivers messages independently,
/// so a subscription can arrive before the task's invocation; it is then held, in a task state without a request, until
/// the invocation arrives or Messaging's <c>OutboxRetryTimeout</c> has elapsed, after which the invocation can no longer
/// arrive and the subscription completes as not found. A subscription to a task of the same grain is staged directly in
/// task state, and completes in the write which stores the task's result.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // Execution-tree records begin with '$', so a subscription never collides with a request fingerprint.
    private const string SubscriptionFingerprintPrefix = "$subscribe:";

    // The segment of the resume message which wakes the grain when a subscription's awaited invocation can no longer
    // arrive. No task uses the segment, so the resume changes no state.
    private const string AwaitedInvocationResumeSegment = "$subscriptions";

    /// <summary>Schedules, or reaches the record of, a subscription child under <paramref name="taskId"/>.</summary>
    /// <remarks>
    /// The record and its subscription commit in one journal write, so a replay which finds the record doesn't subscribe
    /// again; it awaits that write, which an earlier attempt can still be awaiting, and the recorded outcome.
    /// </remarks>
    private async ValueTask<IScheduledTaskHandle> ScheduleSubscriptionAsync(
        TaskId taskId,
        IDurableTaskSubscription subscription,
        bool suppressCancellation,
        CancellationToken cancellationToken)
    {
        var fingerprint = FormatIdentity(SubscriptionFingerprintPrefix, subscription.Target, subscription.TaskId);
        if (_storage.TryGetTask(taskId, out var state))
        {
            if (!string.Equals(state.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw CreateDivergenceException(
                    $"Durable child task '{taskId}' is already associated with a different operation.",
                    state,
                    DescribeSubscription(subscription.Target, subscription.TaskId));
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

        // A subscription of a canceled ancestor is not created, unless the parent scheduled it in a cancellation
        // suppression scope.
        if (IsAncestorCancellationRequested(taskId, state: null, suppressCancellation))
        {
            return new CompletedTaskHandle(taskId, DurableTaskResponse.Canceled);
        }

        // Retention ends at a deadline, so a subscription to a task of this grain doesn't find a task whose retention ended.
        DeleteExpiredTasks();
        var transport = MessageTransport;
        DurableTaskResponse? response = null;
        if (subscription.Target == GrainId)
        {
            // The subscribed result is read before the child is staged, so a result which no longer decodes fails the
            // scheduling without recording the child.
            response = Subscribe(subscription.TaskId, new(GrainId, taskId), fromInbox: false) switch
            {
                SubscriptionOutcome.NotFound => CreateNotFound(subscription.TaskId),
                SubscriptionOutcome.Terminal => _storage.TryGetTask(subscription.TaskId, out var subscribed) ? subscribed.Result! : null,
                _ => null,
            };
        }
        else
        {
            transport.SendSubscription(GrainId, subscription.Target, subscription.TaskId, taskId);
        }

        state = _storage.GetOrCreateTask(taskId, request: null);
        _storage.SetRequestFingerprint(taskId, state, fingerprint);
        MarkCancellationSuppressed(taskId, state, suppressCancellation);
        var handle = GetOrCreateRunningTaskHandle(taskId);
        if (response is not null)
        {
            StoreResult(taskId, state, response, subscribed: true);
            _pendingHandleResponses[taskId] = response;
        }

        await WriteStateAsync(cancellationToken);
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
        if (Subscribe(taskId, subscriber, fromInbox: true) == SubscriptionOutcome.NotFound)
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
    /// <param name="fromInbox">
    /// <see langword="true"/> when the subscription was delivered through the inbox, so it can precede the task's
    /// invocation.
    /// </param>
    /// <returns>
    /// <see cref="SubscriptionOutcome.Pending"/> when the subscriber awaits the task's result,
    /// <see cref="SubscriptionOutcome.Terminal"/> when the task has stored its result, or
    /// <see cref="SubscriptionOutcome.NotFound"/> when this grain doesn't retain the task as an invoked task. A subscriber
    /// on another grain of a terminal task receives a completion through the outbox; a subscriber on this grain reads the
    /// result directly.
    /// </returns>
    /// <remarks>
    /// A delivered subscription for a task which isn't stored, or which records only a cancellation or subscriptions that
    /// preceded its invocation, is held for that invocation, which finds it among its completion destinations. Each such
    /// subscription records when it arrived, and schedules a resume message for <c>OutboxRetryTimeout</c> later, which
    /// wakes the grain, so the subscriptions complete as not found while the grain is otherwise idle.
    /// </remarks>
    private SubscriptionOutcome Subscribe(TaskId taskId, DurableTaskSubscriber subscriber, bool fromInbox)
    {
        var found = _storage.TryGetTask(taskId, out var state);
        var awaitsInvocation = fromInbox
            && (!found || IsSubscriptionRecord(state!) || (IsCancellationRecord(state!) && !HasStarted(taskId)));
        if (!awaitsInvocation && (!found || !IsInvoked(state!)))
        {
            return SubscriptionOutcome.NotFound;
        }

        if (found && HasStoredResult(state!))
        {
            if (subscriber.GrainId == GrainId)
            {
                return SubscriptionOutcome.Terminal;
            }

            if (!IsInvoked(state!))
            {
                // A cancellation which preceded the invocation, and which recovery terminalized, is the outcome which the
                // invocation completes with when it arrives. The record doesn't retain itself for subscribers.
                MessageTransport.SendResponse(GrainId, subscriber, taskId, state!.Result!);
            }
            else if (TryRegisterCompletionDestination(taskId, state!, subscriber))
            {
                SendCompletion(taskId, subscriber);
            }

            return SubscriptionOutcome.Terminal;
        }

        state ??= _storage.GetOrCreateTask(taskId, request: null);
        _storage.AddCompletionDestination(taskId, state, subscriber);
        if (awaitsInvocation)
        {
            // The invocation arrives within the retry timeout of the latest subscription's sender, or never, so each
            // subscription extends the deadline and schedules a resume which wakes the grain then.
            _storage.RetainResult(taskId, state);
            var expiry = GetAwaitedInvocationExpiry(state);
            TrackExpiry(expiry);
            MessageTransport.SendResume(GrainId, taskId.Child(AwaitedInvocationResumeSegment), generation: 1, expiry);
        }

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
            || subscriber == new DurableTaskSubscriber(state.CallerId, taskId)
            || !state.CompletionDestinations.Contains(subscriber))
        {
            return;
        }

        var awaitsInvocation = IsSubscriptionRecord(state);
        _storage.RemoveCompletionDestination(taskId, state, subscriber);
        if (awaitsInvocation && state.CompletionDestinations.Count == 0 && !state.CancellationRequestedAt.HasValue)
        {
            _storage.RemoveTask(taskId);
        }
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

    /// <summary>
    /// Completes the subscribers of a task whose invocation didn't arrive as not found, and removes them, without writing.
    /// </summary>
    private void CompleteSubscribersAsNotFound(TaskId taskId, IDurableTaskState state)
    {
        foreach (var subscriber in state.CompletionDestinations.ToArray())
        {
            if (subscriber.GrainId == GrainId)
            {
                CompleteSubscription(subscriber.TaskId, taskId, CreateNotFound(taskId));
            }
            else
            {
                MessageTransport.SendResponse(GrainId, subscriber, taskId, CreateNotFound(taskId));
            }

            _storage.RemoveCompletionDestination(taskId, state, subscriber);
        }
    }

    private static DurableTaskResponse CreateNotFound(TaskId taskId) =>
        DurableTaskResponse.FromException(new DurableTaskNotFoundException(taskId));

    /// <summary>
    /// Returns when a task state which awaits its invocation is deleted unless the invocation arrives: the retry timeout
    /// after the state was created, or after its latest subscription arrived.
    /// </summary>
    private DateTimeOffset GetAwaitedInvocationExpiry(IDurableTaskState state)
    {
        var since = state.RetainedSince is { } subscribed && subscribed > state.CreatedAt ? subscribed : state.CreatedAt;
        return AddSaturating(since, _shared.OutboxRetryTimeout);
    }

    /// <summary>Returns whether a task state holds only subscriptions which arrived before the task's invocation.</summary>
    /// <remarks>Such a state can also record a cancellation which arrived before the invocation.</remarks>
    private static bool IsSubscriptionRecord(IDurableTaskState state) =>
        state.CompletionDestinations.Count > 0
        && !state.CompletedAt.HasValue
        && state.RequestFingerprint is null
        && state.RemoteRequestFingerprint is null
        && state.DueTime is null
        && state.Request is null;

    /// <summary>Returns whether a task is a subscription child, and the task to which it subscribes.</summary>
    private static bool TryGetSubscription(IDurableTaskState state, out GrainId target, out TaskId taskId) =>
        TryParseIdentity(SubscriptionFingerprintPrefix, state.RequestFingerprint, out target, out taskId);

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
