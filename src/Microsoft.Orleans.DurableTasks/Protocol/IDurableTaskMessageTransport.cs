using Orleans.Runtime;

using Orleans.DurableTasks;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Protocol;

internal interface IDurableTaskMessageTransport
{
    /// <summary>Stages an invocation of the attached child <paramref name="taskId"/> of <paramref name="sender"/>.</summary>
    /// <param name="acknowledge">Whether <paramref name="target"/> replies with an acceptance once it stores the task.</param>
    void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request, bool acknowledge);

    /// <summary>
    /// Stages an invocation which starts the detached task <paramref name="taskId"/> on <paramref name="target"/>, and
    /// subscribes <paramref name="sender"/>'s task <paramref name="subscriberId"/>, if any, to its result.
    /// </summary>
    /// <param name="acknowledge">Whether <paramref name="target"/> replies with an acceptance once it stores the task.</param>
    void SendDetachedInvocation(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        IDurableTaskRequest request,
        TaskId? subscriberId,
        bool acknowledge);

    /// <summary>
    /// Stages the reply to an invocation of <paramref name="taskId"/>, which <paramref name="destination"/> started: an
    /// acceptance, or the <paramref name="rejection"/> which fails the start.
    /// </summary>
    void SendAcceptance(GrainId sender, DurableTaskSubscriber destination, TaskId taskId, DurableTaskResponse? rejection);

    /// <summary>Stages a completion which the runtime provides from the task's stored result.</summary>
    /// <param name="messageId">The completion's message ID, from <see cref="GetCompletionMessageId"/>.</param>
    void SendCompletion(MessageId messageId);

    /// <summary>Gets the stable ID of the completion of <paramref name="taskId"/> which <paramref name="sender"/> sends to <paramref name="destination"/>.</summary>
    MessageId GetCompletionMessageId(GrainId sender, DurableTaskSubscriber destination, TaskId taskId);

    /// <summary>Encodes the completion of <paramref name="taskId"/> which <paramref name="sender"/> sends to <paramref name="destination"/>.</summary>
    OutboxMessage CreateCompletionMessage(
        MessageId messageId,
        GrainId sender,
        DurableTaskSubscriber destination,
        TaskId taskId,
        DurableTaskResponse response);

    /// <summary>
    /// Stages a completion which carries <paramref name="response"/> for <paramref name="taskId"/> to
    /// <paramref name="destination"/>, for a task which doesn't retain itself for the subscriber, such as a task which
    /// <paramref name="sender"/> doesn't retain as an invoked task, which completes the subscriber as not found.
    /// </summary>
    void SendResponse(GrainId sender, DurableTaskSubscriber destination, TaskId taskId, DurableTaskResponse response);

    /// <summary>
    /// Stages a subscription of <paramref name="sender"/>'s task <paramref name="subscriberId"/> to the result of the
    /// existing task <paramref name="taskId"/> of <paramref name="target"/>, as an invocation which carries no request.
    /// </summary>
    void SendSubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId);

    /// <summary>
    /// Stages the end of <paramref name="sender"/>'s task <paramref name="subscriberId"/>'s subscription to
    /// <paramref name="taskId"/> of <paramref name="target"/>, as a cancellation message which names the subscriber.
    /// </summary>
    void SendUnsubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId);

    /// <summary>Stages a cooperative cancellation, carrying its <paramref name="reason"/>, of a task which <paramref name="target"/> executes.</summary>
    void SendCancellation(GrainId sender, GrainId target, TaskId taskId, string? reason = null);

    /// <summary>Stages an administrative abort of a task which <paramref name="target"/> executes for <paramref name="sender"/>.</summary>
    void SendAbort(GrainId sender, GrainId target, TaskId taskId, string reason);

    void SendResume(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime);

    /// <summary>Stages a signal for the invoked task <paramref name="taskId"/> on <paramref name="target"/>.</summary>
    /// <param name="payloadFingerprint">The fingerprint of <paramref name="value"/>, which the message identity covers.</param>
    void SendSignal(GrainId sender, GrainId target, TaskId taskId, string name, DurableTaskResponse value, string payloadFingerprint);

}
