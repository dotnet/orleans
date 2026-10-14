using Orleans.Runtime;

using Orleans.DurableTasks;
using Orleans.Messaging;

namespace Orleans.DurableTasks.Protocol;

internal interface IDurableTaskMessageTransport
{
    void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request);

    /// <summary>Stages an invocation which starts a detached task on <paramref name="target"/>.</summary>
    void SendDetachedInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request);

    /// <summary>Stages a completion which the runtime provides from the task's stored result.</summary>
    /// <param name="messageId">The completion's message ID, from <see cref="GetCompletionMessageId"/>.</param>
    void SendCompletion(MessageId messageId);

    /// <summary>Gets the stable ID of the completion of a task sent from <paramref name="sender"/> to <paramref name="target"/>.</summary>
    MessageId GetCompletionMessageId(GrainId sender, GrainId target, TaskId taskId);

    /// <summary>Encodes a completion message addressed to <paramref name="target"/>.</summary>
    /// <param name="observerId">
    /// The observation in <paramref name="target"/>'s execution tree which the completion completes, or
    /// <see langword="default"/> for a completion addressed to the task's caller.
    /// </param>
    OutboxMessage CreateCompletionMessage(
        MessageId messageId,
        GrainId sender,
        GrainId target,
        TaskId taskId,
        DurableTaskResponse response,
        TaskId observerId = default);

    /// <summary>
    /// Gets the stable ID of the completion of <paramref name="taskId"/> which <paramref name="sender"/> sends to the
    /// observation <paramref name="observerId"/> of <paramref name="target"/>.
    /// </summary>
    MessageId GetObservationCompletionMessageId(GrainId sender, GrainId target, TaskId taskId, TaskId observerId);

    /// <summary>
    /// Stages a completion which reports to the observation <paramref name="observerId"/> of <paramref name="target"/> that
    /// <paramref name="sender"/> retains no invoked task <paramref name="taskId"/>.
    /// </summary>
    void SendObservationNotFound(GrainId sender, GrainId target, TaskId taskId, TaskId observerId);

    /// <summary>
    /// Stages a subscription of the observation <paramref name="observerId"/> of <paramref name="sender"/> to the terminal
    /// result of the invoked task <paramref name="taskId"/> of <paramref name="target"/>, or the end of that subscription.
    /// </summary>
    void SendObservation(GrainId sender, GrainId target, TaskId taskId, TaskId observerId, bool unsubscribe);

    /// <summary>Stages a cooperative cancellation, carrying its <paramref name="reason"/>, of a task which <paramref name="target"/> executes.</summary>
    void SendCancellation(GrainId sender, GrainId target, TaskId taskId, string? reason = null);

    /// <summary>Stages an administrative abort of a task which <paramref name="target"/> executes for <paramref name="sender"/>.</summary>
    void SendAbort(GrainId sender, GrainId target, TaskId taskId, string reason);

    void SendResume(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime);

    /// <summary>Stages a signal for the invoked task <paramref name="taskId"/> on <paramref name="target"/>.</summary>
    /// <param name="payloadFingerprint">The fingerprint of <paramref name="value"/>, which the message identity covers.</param>
    void SendSignal(GrainId sender, GrainId target, TaskId taskId, string name, DurableTaskResponse value, string payloadFingerprint);

}
