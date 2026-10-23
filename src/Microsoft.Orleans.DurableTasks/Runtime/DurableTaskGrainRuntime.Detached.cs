using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <remarks>
/// <para>
/// A durable execution starts a detached task by recording a subscription child, whose fingerprint names the task's grain
/// and root identifier, and whose method names the request's method, and staging the task's invocation in the same journal write. The invocation names the
/// child as a subscriber, so the task's grain adds the child to the task's completion destinations when it stores the
/// task, without a separate subscription. The child observes the task as other subscriptions do: its cancellation ends
/// only the subscription. A detached task of the same grain is staged directly in task state.
/// </para>
/// <para>
/// Every invocation of a task on another grain is a reference send, which the runtime provides from its journaled table of
/// pending invocations. The table is separate from the execution trees, so a start survives deletion of the tree which
/// made it, and the outbox reports the invocation's retirement to the runtime, which removes it from the table in the
/// retirement's journal write and records the outcome on the starting record: the record of a call to another grain, or
/// of a detached task. The outbox retires an invocation as delivered once the task's grain has durably stored the task,
/// which is the target's acceptance. A grain which can't store the task, because its identifier already identifies
/// another request or caller, dead-letters the invocation with the <see cref="DurableTaskRejectionCodes.Conflict"/> code,
/// and the outbox retires it as rejected, with the grain's reason, which fails the record with a
/// <see cref="DurableTaskConflictException"/>. A grain which receives an invocation after its admission window, Messaging's
/// <c>DeduplicationWindow</c> since the invocation was first staged, dead-letters it with the
/// <see cref="DurableTaskRejectionCodes.Expired"/> code. Another rejection, with another code or none, fails the record with
/// a <see cref="DurableTaskRejectedException"/>, which reports the code. An invocation whose delivery the outbox can't
/// confirm before Messaging's <c>OutboxRetryTimeout</c>, including one which the grain fails to accept, such as one which
/// doesn't decode, or which the grain fences after its admission cutoff, retires as dead-lettered, which fails the record
/// with a <see cref="DurableTaskDeliveryException"/>: an earlier attempt may have reached the grain, so the outcome is
/// unknown. Awaiting the task, or a start which awaits its acceptance, throws the
/// failure, in every replay.
/// </para>
/// </remarks>
internal sealed partial class DurableTaskGrainRuntime
{
    // A detached task's record is a subscription child whose fingerprint names the task's grain and root identifier, and
    // the diagnostic fingerprint of the detached request. The record's method identifies the request, so a replay which
    // starts another method under the same root identifier diverges, and one whose arguments serialize differently doesn't.
    private const string DetachedFingerprintPrefix = "$detach:";

    // The starts which await their target's acceptance, by the identifier of their record. Each is signaled when the record
    // records the acceptance or the failed start, or when the record is deleted.
    private readonly Dictionary<TaskId, TaskCompletionSource> _acceptanceWaiters = [];

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
        var handle = StageDetached(recordId, rootId, request, shielded);

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
        return StageDetached(recordId, rootId, request, shielded);
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
        CallerScope.ThrowIfReserved(rootId);
        var target = request.Context?.TargetId ?? throw new InvalidOperationException("The durable task request has no target.");
        if (target == GrainId)
        {
            StageScheduleWithCommitEffects(rootId, request, fromInbox: false);
            return;
        }

        PrepareDetachedInvocation(request);
        SendInvocation(MessageTransport.CreateDetachedInvocation(GrainId, target, rootId, request, subscriberId: null), request, recordId: null);
    }

    private IScheduledTaskHandle StageDetached(TaskId recordId, TaskId rootId, IDurableTaskRequest request, bool shielded)
    {
        Debug.Assert(rootId.Parent().IsDefault, "A detached task is a root task.");
        var target = request.Context?.TargetId ?? throw new InvalidOperationException("The durable task request has no target.");
        var method = IDurableTaskRequest.GetMethod(request);
        if (TryGetRecordedSubscription(recordId, DurableTaskRecordKind.DetachedStart, target, rootId, method, DescribeDetachedStart(target, rootId, method)) is { } recorded)
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
            var recordWireId = GetWireId(recordId);
            SendInvocation(
                MessageTransport.CreateDetachedInvocation(GrainId, target, rootId, request, recordWireId),
                request,
                recordWireId);
        }

        var fingerprint = FormatIdentity(
            $"{DetachedFingerprintPrefix}{IDurableTaskRequest.GetFingerprint(request, _shared.Serializer)}:",
            target,
            rootId);
        var handle = StageSubscriptionRecord(recordId, DurableTaskRecordKind.DetachedStart, fingerprint, shielded, response, method);

        // A canceled starter still starts the task, which its cancellation doesn't reach, and ends only its subscription.
        if (response is null && IsStartRefusedByAncestorCancellation(recordId, state: null, shielded))
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

    private static string DescribeDetachedStart(GrainId target, TaskId rootId, string? method) =>
        $"a start of detached task '{rootId}' on grain '{target}' for {DescribeMethod(method)}";

    /// <summary>
    /// Returns why the grain rejects an invocation of <paramref name="taskId"/> which its sender first staged at
    /// <paramref name="sentAt"/>, or <see langword="null"/> when the grain admits it.
    /// </summary>
    /// <remarks>
    /// The grain admits an invocation before <see cref="DurableTaskGrainRuntimeShared.InvocationAdmissionWindow"/> has
    /// elapsed since <paramref name="sentAt"/>. A cancellation or signal which its sender staged with or after the
    /// invocation is recorded for <see cref="DurableTaskGrainRuntimeShared.EarlyRecordRetention"/> after it arrives, which
    /// covers the skew between the sender's clock and the grain's, so an admitted invocation finds it. Like Messaging's
    /// <c>DeliverAt</c>, the check compares the sender's clock with the grain's. A
    /// redelivery of an invocation which the grain stores stages nothing new, so the grain admits it at any time.
    /// </remarks>
    internal string? GetInvocationAdmissionRejection(TaskId taskId, DateTimeOffset sentAt)
    {
        var deadline = AddSaturating(sentAt, _shared.InvocationAdmissionWindow);
        if (UtcNow < deadline || (_storage.TryGetTask(taskId, out var state) && IsInvoked(state)))
        {
            return null;
        }

        return $"The invocation of durable task '{taskId}' arrived at or after its admission deadline, {deadline:O}.";
    }

    /// <summary>Stages an invocation which the inbox delivered.</summary>
    /// <param name="taskId">The invoked task: the caller's child, under the caller's scope, or a detached root task.</param>
    /// <param name="request">The request which the task runs.</param>
    /// <param name="sender">The grain which sent the invocation.</param>
    /// <param name="detached">Whether the invocation starts a detached task, which has no caller.</param>
    /// <param name="subscriberId">The wire identifier of the sender's task which subscribes to a detached task, if any.</param>
    /// <remarks>
    /// A detached task's subscriber is added to the task's completion destinations in the same write which stores the
    /// task. An invocation which conflicts with the stored task, such as another request under the same identifier,
    /// throws before staging anything, so the inbox dead-letters it with the conflict's message.
    /// </remarks>
    internal void InvokeFromInbox(TaskId taskId, IDurableTaskRequest request, GrainId sender, bool detached, TaskId? subscriberId)
    {
        ThrowIfStopping();
        var requestContext = request.Context
            ?? throw new InvalidOperationException("The durable task invocation request has no context.");
        if (detached)
        {
            // A detached task is a root task, which no other grain's invocation creates beneath the reserved scope root.
            CallerScope.ThrowIfReserved(taskId);
            PrepareDetachedInvocation(request);
        }
        else if (subscriberId is not null)
        {
            throw new InvalidOperationException("An attached durable task invocation is addressed to its caller's task.");
        }
        else
        {
            requestContext.CallerId = sender;
            requestContext.SupportsDurableCompletion = true;
        }

        StageScheduleWithCommitEffects(taskId, request, fromInbox: true);
        if (subscriberId is { } subscriber)
        {
            var outcome = Subscribe(taskId, new(sender, subscriber));
            Debug.Assert(outcome != SubscriptionOutcome.NotFound, "A staged invocation is retained as an invoked task.");
        }
    }

    /// <summary>
    /// Stages <paramref name="invocation"/> in the outbox by reference, and journals it in the table of pending invocations
    /// until it retires, with <paramref name="recordId"/>, the wire identifier of the task which records the start's
    /// outcome, if any.
    /// </summary>
    /// <remarks>
    /// A pending invocation with the same identity coalesces: the replay of a start which recovery restored stages nothing.
    /// The identity of a start which a record makes covers the record's wire identifier, so each run of a tree sends its
    /// own. The pending invocation keeps the request which it journaled, so a replay whose arguments serialize differently
    /// still coalesces. A pending invocation which invokes another method under the same identity, which an earlier post
    /// of the same root task sent, fails the start.
    /// </remarks>
    private void SendInvocation(OutboxMessage invocation, IDurableTaskRequest request, TaskId? recordId)
    {
        var messageId = invocation.Envelope.MessageId;
        if (_invocations.TryGetValue(messageId, out var pending))
        {
            if (!pending.Payload.AsSpan().SequenceEqual(invocation.Envelope.Payload)
                && !IsSameMethod(pending, request))
            {
                throw new InvalidOperationException(
                    $"An invocation of grain '{pending.Target}' with message ID '{messageId}' is still pending with a request for another method than {DescribeMethod(IDurableTaskRequest.GetMethod(request))}.");
            }

            return;
        }

        _invocations.Add(messageId, new(invocation.ReceiverId, invocation.Envelope.Payload, recordId));
        MessageTransport.Send(messageId);
    }

    /// <summary>Returns whether the pending invocation invokes the method which <paramref name="request"/> invokes.</summary>
    /// <remarks>A pending invocation whose request no longer decodes can't be shown to invoke the same method.</remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A request which doesn't decode isn't the same method.")]
    private bool IsSameMethod(PendingInvocation pending, IDurableTaskRequest request)
    {
        try
        {
            return _shared.Serializer.Deserialize<DurableTaskInvocationMessage>(pending.Payload)?.Request is { } pendingRequest
                && string.Equals(IDurableTaskRequest.GetMethod(pendingRequest), IDurableTaskRequest.GetMethod(request), StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Records how <paramref name="invocation"/> retired on the record which started it: its target's acceptance, or the
    /// failed start, whose failure ends the record, without writing.
    /// </summary>
    /// <remarks>
    /// The outbox stages the invocation's removal in the same journal write. A record which the task's result already
    /// reached recorded the acceptance then. A record which already stores its own outcome, such as its cancellation or
    /// abort, keeps it, so every replay observes the outcome which it observed first. A posted root task has no record,
    /// and a record which was deleted with its tree ignores the retirement, as does a record which a later run of the tree
    /// recreated under the same identifier.
    /// </remarks>
    private void RetireInvocation(PendingInvocation invocation, OutboxMessageRetirement retirement)
    {
        if (invocation.RecordId is not { } recordWireId
            || !TryGetNode(recordWireId, out var recordId, out var state)
            || state.Accepted.HasValue)
        {
            return;
        }

        var delivered = retirement.Status == OutboxMessageRetirementStatus.Delivered;
        _storage.SetAccepted(recordId, state, delivered);
        if (!delivered && !HasStoredResult(state))
        {
            var invokedId = TryGetDetachedTask(state, out _, out var rootId) ? rootId : recordId;
            Exception failure = retirement.Status switch
            {
                OutboxMessageRetirementStatus.Rejected when retirement.Code == DurableTaskRejectionCodes.Conflict
                    => new DurableTaskConflictException(invokedId, retirement.Reason!),
                OutboxMessageRetirementStatus.Rejected
                    => new DurableTaskRejectedException(invokedId, invocation.Target, retirement.Code, retirement.Reason!),
                _ => new DurableTaskDeliveryException(invokedId, invocation.Target, retirement.Reason!),
            };
            var response = DurableTaskResponse.FromException(failure);
            StoreResult(recordId, state, response, subscribed: true);
            _pendingHandleResponses[recordId] = response;
        }

        SignalAcceptance(recordId);
    }

    /// <summary>
    /// Throws unless <paramref name="recordId"/>, whose wire identifier is <paramref name="wireId"/>, started
    /// <paramref name="sourceTaskId"/> of <paramref name="sender"/>: it is the remote child which that grain stores under
    /// this grain's caller scope, or it subscribes to that task.
    /// </summary>
    private void ThrowIfNotStartedBy(TaskId recordId, TaskId wireId, TaskId sourceTaskId, IDurableTaskState state, GrainId sender)
    {
        var matches = TryGetSubscription(state, out var target, out var subscribedTaskId)
            ? target == sender && subscribedTaskId == sourceTaskId
            : !state.RemoteTarget.IsDefault
                && state.RemoteRequestFingerprint is not null
                && state.RemoteTarget == sender
                && sourceTaskId == CallerScope.GetTaskId(GrainId, wireId);
        if (!matches)
        {
            throw new InvalidOperationException(
                $"Durable task '{recordId}' doesn't observe task '{sourceTaskId}' of grain '{sender}'.");
        }
    }

    /// <summary>
    /// Waits until the record <paramref name="recordId"/> durably records whether the target of its start accepted the
    /// task, and throws the outcome of a start which its target didn't accept.
    /// </summary>
    /// <remarks>
    /// A start on this grain is accepted once its write commits. A start on another grain is accepted when its invocation
    /// retires as delivered, or when the task's result reaches the record first. A failed start ends the record with its
    /// failure, or with the record's own cancellation or abort, which it stored first.
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
                if (!accepted)
                {
                    Debug.Assert(state.Result?.Exception is not null, "A failed start ends its record with a failure or a cancellation.");
                    ExceptionDispatchInfo.Throw(state.Result!.Exception!);
                }

                return;
            }

            if (!_acceptanceWaiters.TryGetValue(recordId, out var waiter))
            {
                _acceptanceWaiters[recordId] = waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            await waiter.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Returns whether a record starts a task on another grain, whose acceptance it awaits.</summary>
    private bool AwaitsAcceptance(IDurableTaskState state) =>
        !state.RemoteTarget.IsDefault || (TryGetDetachedTask(state, out var target, out _) && target != GrainId);

    /// <summary>Wakes the start which awaits the reply for <paramref name="recordId"/>, if any.</summary>
    private void SignalAcceptance(TaskId recordId)
    {
        if (_acceptanceWaiters.Remove(recordId, out var waiter))
        {
            waiter.TrySetResult();
        }
    }
}
