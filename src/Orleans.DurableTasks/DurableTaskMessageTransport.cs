using System;
using System.Distributed.DurableTasks;
using Orleans;
using Orleans.DurableMessaging;
using Orleans.Journaling;
using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.DurableTasks;

internal sealed class DurableTaskMessageTransport(
    IDurableOutbox outbox,
    IDurableMessageScheduler scheduler,
    IJournaledStateManager stateManager,
    SerializerSessionPool sessionPool) : IDurableTaskMessageTransport
{
    internal const string InvocationRoute = "durable-rpc/invoke";
    internal const string CompletionRoute = "durable-rpc/complete";
    internal const string CancellationRoute = "durable-rpc/cancel";
    internal const string ResumeRoute = "durable-rpc/resume";

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Send(sender, target, taskId, InvocationRoute, new DurableTaskInvocationMessage { TaskId = taskId, Request = request }, replyTo: sender);

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) =>
        Send(sender, target, taskId, CompletionRoute, new DurableTaskCompletionMessage { TaskId = taskId, Response = response }, replyTo: null);

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) =>
        Send(sender, target, taskId, CancellationRoute, new DurableTaskCancellationMessage { TaskId = taskId }, replyTo: null);

    public ValueTask ScheduleResumeAsync(
        GrainId target,
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken) =>
        scheduler.ScheduleAsync(
            CreateEnvelope(
                target,
                target,
                taskId,
                ResumeRoute,
                new DurableTaskResumeMessage { TaskId = taskId },
                replyTo: null),
            dueTime,
            cancellationToken);

    public ValueTask CommitAsync(CancellationToken cancellationToken) =>
        stateManager.WriteStateAsync(cancellationToken);

    private void Send<T>(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string route,
        T body,
        GrainId? replyTo)
    {
        outbox.Send(CreateEnvelope(sender, target, taskId, route, body, replyTo));
    }

    private DurableEnvelope CreateEnvelope<T>(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string route,
        T body,
        GrainId? replyTo)
    {
        // Validate the task id and compute the correlation key before serializing the body: several message body
        // types (see DurableTaskMessages.cs) embed the same TaskId, whose surrogate converter throws
        // ArgumentOutOfRangeException for a default TaskId. Without this ordering, that lower-level exception would
        // pre-empt the more specific ArgumentException below, since WithBody() executes eagerly.
        var correlationKey = taskId.ToHierarchicalKey() ?? throw new ArgumentException("The task id must not be empty.", nameof(taskId));
        var builder = new DurableEnvelopeBuilder(sessionPool, sender)
            .To(target, route)
            .WithCorrelationKey(correlationKey)
            .WithCurrentRequestContext()
            .WithBody(body);

        if (replyTo is { } address)
        {
            builder.WithReplyTo(address);
        }

        return builder.Build();
    }
}
