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
    OutboxMessage CreateCompletionMessage(MessageId messageId, GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response);

    void SendCancellation(GrainId sender, GrainId target, TaskId taskId);

    void SendResume(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime);

}
