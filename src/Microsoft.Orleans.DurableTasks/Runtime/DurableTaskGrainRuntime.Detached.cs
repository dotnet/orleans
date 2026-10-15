using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// A durable execution starts a detached task by recording a subscription child, whose fingerprint names the task's grain,
/// root identifier, and request, and staging the task's invocation in the same journal write. The invocation is a full
/// outbox message which carries the request and names the child as a subscriber, so the start survives deletion of the
/// execution's tree, and the task's grain adds the child to the task's completion destinations when it stores the task,
/// without a separate subscription. The child observes the task as other subscriptions do: its cancellation ends only the
/// subscription. A detached task of the same grain is staged directly in task state.
/// </para>
/// <para>
/// A start which awaits acceptance asks the task's grain to acknowledge the invocation: the grain replies with an
/// acceptance message in the write which stores the task, and with a rejection instead when the task's identifier
/// already identifies another request. Every invocation which names a caller or a subscriber receives the rejection, which
/// the starting record stores as its result. The starting record stores which reply arrived, so a replay observes the
/// same outcome.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // A detached task's record is a subscription child whose fingerprint also covers the detached request, so a replay
    // which starts another request under the same root identifier diverges.
    private const string DetachedFingerprintPrefix = "$detach:";

    // The starts which await their target's reply, by the identifier of their record. Each is signaled when the reply
    // arrives or when the record is deleted.
    private readonly ConcurrentDictionary<TaskId, TaskCompletionSource> _acceptanceWaiters = [];

    /// <summary>
    /// Starts <paramref name="request"/> as the detached root task <paramref name="rootId"/>, recorded and subscribed under
    /// <paramref name="recordId"/>, and waits for the write which commits the start.
    /// </summary>
    /// <param name="waitForAcceptance">Whether the start also waits for the task's grain to accept the task.</param>
    public async ValueTask<IScheduledTaskHandle> StartDetachedAsync(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        var handle = StageDetached(recordId, rootId, request, shielded, acknowledge: waitForAcceptance);

        // The record commits with the start, possibly in a write which an earlier attempt is still awaiting.
        await WriteStateAsync(cancellationToken);
        if (waitForAcceptance)
        {
            await WaitForAcceptanceAsync(recordId, cancellationToken);
        }

        return handle;
    }

    /// <summary>
    /// Stages the start of the detached root task <paramref name="rootId"/>, recorded and subscribed under
    /// <paramref name="recordId"/>, without writing.
    /// </summary>
    public IScheduledTaskHandle PostDetached(TaskId recordId, TaskId rootId, IDurableTaskRequest request, bool shielded)
    {
        ThrowIfStopping();
        return StageDetached(recordId, rootId, request, shielded, acknowledge: false);
    }

    /// <summary>
    /// Stages the start of a detached root task without a subscriber, from code which runs outside a durable execution.
    /// </summary>
    /// <remarks>
    /// The start commits with this grain's next journal write, together with the state changes which the calling work item
    /// made. A task of this grain starts after that write.
    /// </remarks>
    public void PostRoot(TaskId rootId, IDurableTaskRequest request)
    {
        ThrowIfStopping();
        Debug.Assert(rootId.Parent().IsDefault, "A posted task is a root task.");
        var target = request.Context?.TargetId ?? throw new InvalidOperationException("The durable task request has no target.");
        if (target == GrainId)
        {
            StageScheduleWithCommitEffects(rootId, request, fromInbox: false);
            return;
        }

        PrepareDetachedInvocation(request);
        MessageTransport.SendDetachedInvocation(GrainId, target, rootId, request, subscriberId: null, acknowledge: false);
    }

    private IScheduledTaskHandle StageDetached(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool acknowledge)
    {
        Debug.Assert(rootId.Parent().IsDefault, "A detached task is a root task.");
        var target = request.Context?.TargetId ?? throw new InvalidOperationException("The durable task request has no target.");
        var fingerprint = FormatIdentity(
            $"{DetachedFingerprintPrefix}{IDurableTaskRequest.GetFingerprint(request, _shared.Serializer)}:",
            target,
            rootId);
        if (TryGetRecordedSubscription(recordId, fingerprint, DescribeDetachedStart(target, rootId)) is { } recorded)
        {
            return recorded.Handle;
        }

        DurableTaskResponse? response = null;
        if (target == GrainId)
        {
            // The task is staged before its record, so a start which conflicts with the task fails without a record.
            StageScheduleWithCommitEffects(rootId, request, fromInbox: false);
            response = SubscribeLocal(rootId, recordId);
        }
        else
        {
            PrepareDetachedInvocation(request);
            MessageTransport.SendDetachedInvocation(GrainId, target, rootId, request, recordId, acknowledge);
        }

        var handle = StageSubscriptionRecord(recordId, fingerprint, shielded, response);

        // A canceled starter still starts the task, which its cancellation doesn't reach, and ends only its subscription.
        if (response is null && IsAncestorCancellationRequested(recordId, state: null, shielded))
        {
            _pendingHandleResponses[recordId] = StageResponse(recordId, DurableTaskResponse.Canceled);
        }

        return handle;
    }

    /// <summary>Clears the caller of a detached request, which no caller claims.</summary>
    private static void PrepareDetachedInvocation(IDurableTaskRequest request)
    {
        var context = request.Context!;
        context.CallerId = default;
        context.SupportsDurableCompletion = false;
    }

    /// <summary>Returns whether a task is the record of a detached task, and the task which it started.</summary>
    private static bool TryGetDetachedTask(IDurableTaskState state, out GrainId target, out TaskId rootId)
    {
        target = default;
        rootId = default;
        if (state.RequestFingerprint is not { } fingerprint
            || !fingerprint.StartsWith(DetachedFingerprintPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // The request fingerprint, which contains no ':', precedes the identity.
        var separator = fingerprint.IndexOf(':', DetachedFingerprintPrefix.Length);
        return separator > 0 && TryParseIdentity(fingerprint[..(separator + 1)], fingerprint, out target, out rootId);
    }

    private static string DescribeDetachedStart(GrainId target, TaskId rootId) =>
        $"a start of detached task '{rootId}' on grain '{target}'";

    /// <summary>Stages an invocation which the inbox delivered, and replies to its sender when the sender awaits it.</summary>
    /// <param name="taskId">The invoked task: the caller's child, or a detached root task.</param>
    /// <param name="request">The request which the task runs.</param>
    /// <param name="sender">The grain which sent the invocation.</param>
    /// <param name="detached">Whether the invocation starts a detached task, which has no caller.</param>
    /// <param name="subscriberId">The sender's task which subscribes to a detached task, if any.</param>
    /// <param name="acknowledge">Whether the sender awaits an acceptance.</param>
    /// <remarks>
    /// A start which conflicts with the stored task, such as another request under the same identifier, stages nothing for
    /// the task, and its sender receives the rejection when it awaits the task's result. A detached task's subscriber is
    /// added to the task's completion destinations in the same write which stores the task.
    /// </remarks>
    internal void InvokeFromInbox(
        TaskId taskId,
        IDurableTaskRequest request,
        GrainId sender,
        bool detached,
        TaskId? subscriberId,
        bool acknowledge)
    {
        ThrowIfStopping();
        var requestContext = request.Context
            ?? throw new InvalidOperationException("The durable task invocation request has no context.");
        DurableTaskSubscriber? replyTo;
        if (detached)
        {
            PrepareDetachedInvocation(request);
            replyTo = subscriberId is { } subscriber ? new(sender, subscriber) : null;
        }
        else
        {
            if (subscriberId is not null)
            {
                throw new InvalidOperationException("An attached durable task invocation is addressed to its caller's task.");
            }

            requestContext.CallerId = sender;
            requestContext.SupportsDurableCompletion = true;
            replyTo = new(sender, taskId);
        }

        try
        {
            StageScheduleWithCommitEffects(taskId, request, fromInbox: true);
        }
        catch (DurableTaskConflictException conflict) when (replyTo is { } rejected)
        {
            MessageTransport.SendAcceptance(GrainId, rejected, taskId, DurableTaskResponse.FromException(conflict));
            return;
        }

        if (detached && replyTo is { } detachedSubscriber)
        {
            var outcome = Subscribe(taskId, detachedSubscriber);
            Debug.Assert(outcome != SubscriptionOutcome.NotFound, "A staged invocation is retained as an invoked task.");
        }

        if (acknowledge && replyTo is { } acceptedBy)
        {
            MessageTransport.SendAcceptance(GrainId, acceptedBy, taskId, rejection: null);
        }
    }

    /// <summary>
    /// Stages the reply of <paramref name="sender"/> to the start which <paramref name="recordId"/> records: an acceptance
    /// of <paramref name="sourceTaskId"/>, or the <paramref name="rejection"/> which fails the start.
    /// </summary>
    /// <remarks>
    /// A rejection is stored as the record's result, unless the record has one already. A reply for a record which was
    /// deleted with its tree changes nothing, and so does a redelivered reply.
    /// </remarks>
    internal void AcceptAcceptanceFromInbox(TaskId recordId, TaskId sourceTaskId, DurableTaskResponse? rejection, GrainId sender)
    {
        ThrowIfStopping();
        if (rejection is { Exception: null })
        {
            throw new InvalidOperationException("A durable task rejection carries a failure.");
        }

        if (!_storage.TryGetTask(recordId, out var state))
        {
            return;
        }

        ThrowIfNotStartedBy(recordId, sourceTaskId, state, sender);
        if (state.Accepted.HasValue)
        {
            return;
        }

        _storage.SetAccepted(recordId, state, accepted: rejection is null);
        if (rejection is not null && !HasStoredResult(state))
        {
            StoreResult(recordId, state, rejection, subscribed: true);
            _pendingHandleResponses[recordId] = rejection;
        }

        SignalAcceptance(recordId);
    }

    /// <summary>
    /// Throws unless <paramref name="recordId"/> started <paramref name="sourceTaskId"/> of <paramref name="sender"/>: it
    /// is that grain's remote child, or it subscribes to that task.
    /// </summary>
    private static void ThrowIfNotStartedBy(TaskId recordId, TaskId sourceTaskId, IDurableTaskState state, GrainId sender)
    {
        var matches = TryGetSubscription(state, out var target, out var subscribedTaskId)
            ? target == sender && subscribedTaskId == sourceTaskId
            : !state.RemoteTarget.IsDefault
                && state.RemoteRequestFingerprint is not null
                && state.RemoteTarget == sender
                && sourceTaskId == recordId;
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Durable task '{recordId}' doesn't observe task '{sourceTaskId}' of grain '{sender}'.");
        }
    }

    /// <summary>
    /// Waits until the target of the start which <paramref name="recordId"/> records has accepted the task and the
    /// acceptance is durable, and throws the rejection of a rejected start.
    /// </summary>
    /// <remarks>
    /// A start on this grain is accepted once its write commits. A start on another grain completes with its target's
    /// reply, even when the task's result arrived first, so every replay observes the same outcome.
    /// </remarks>
    private async ValueTask WaitForAcceptanceAsync(TaskId recordId, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!_storage.TryGetTask(recordId, out var state))
            {
                throw new OperationCanceledException($"Durable task '{recordId}' was deleted with its execution tree.");
            }

            if (!AwaitsAcceptance(state))
            {
                return;
            }

            if (state.Accepted is { } accepted)
            {
                await WriteStateAsync(cancellationToken);
                if (!accepted && state.Result?.Exception is { } rejection)
                {
                    ExceptionDispatchInfo.Throw(rejection);
                }

                return;
            }

            var waiter = _acceptanceWaiters.GetOrAdd(
                recordId,
                static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            await waiter.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Returns whether a record starts a task on another grain, whose reply it awaits.</summary>
    private bool AwaitsAcceptance(IDurableTaskState state) =>
        !state.RemoteTarget.IsDefault || (TryGetDetachedTask(state, out var target, out _) && target != GrainId);

    /// <summary>Wakes the start which awaits the reply for <paramref name="recordId"/>, if any.</summary>
    private void SignalAcceptance(TaskId recordId)
    {
        if (_acceptanceWaiters.TryRemove(recordId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }
}
