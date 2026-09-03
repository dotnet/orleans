using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableJobs;
using Orleans.DurableMessaging;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization.Session;

namespace Orleans.DurableTasks.Runtime;

internal sealed class DurableTaskMessageTransport(
    IDurableOutbox outbox,
    ILocalDurableJobManager jobManager,
    SerializerSessionPool sessionPool) : IDurableTaskMessageTransport
{
    internal const string InvocationRoute = "durable-rpc/invoke";
    internal const string CompletionRoute = "durable-rpc/complete";
    internal const string CompletionAckRoute = "durable-rpc/complete-ack";
    internal const string CancellationRoute = "durable-rpc/cancel";
    internal const string ResumeJobName = "orleans.durable-rpc.resume";
    internal const string ResumeTaskIdMetadata = "orleans.durable-rpc.task-id";
    internal const string ResumeGenerationMetadata = "orleans.durable-rpc.generation";

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Send(
            sender,
            target,
            taskId,
            InvocationRoute,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request },
            replyTo: sender,
            messageId: CreateStableMessageId(sender, target, taskId, InvocationRoute));

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) =>
        Send(
            sender,
            target,
            taskId,
            CompletionRoute,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = response },
            replyTo: null,
            messageId: CreateStableMessageId(sender, target, taskId, CompletionRoute));

    public void SendCompletionAck(GrainId sender, GrainId target, TaskId taskId) =>
        Send(
            sender,
            target,
            taskId,
            CompletionAckRoute,
            new DurableTaskCompletionAckMessage { TaskId = taskId },
            replyTo: null,
            messageId: CreateStableMessageId(sender, target, taskId, CompletionAckRoute));

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) =>
        Send(
            sender,
            target,
            taskId,
            CancellationRoute,
            new DurableTaskCancellationMessage { TaskId = taskId },
            replyTo: null,
            messageId: CreateStableMessageId(sender, target, taskId, CancellationRoute));

    public async ValueTask ScheduleResumeAsync(
        GrainId target,
        TaskId taskId,
        long generation,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        await jobManager.ScheduleJobAsync(
            new ScheduleJobRequest
            {
                Target = target,
                JobName = ResumeJobName,
                DueTime = dueTime,
                Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ResumeTaskIdMetadata] = taskId.ToString(),
                    [ResumeGenerationMetadata] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            },
            cancellationToken);
    }

    private void Send<T>(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string route,
        T body,
        GrainId? replyTo,
        Guid? messageId = null)
    {
        outbox.Send(CreateEnvelope(sender, target, taskId, route, body, replyTo, messageId));
    }

    private DurableEnvelope CreateEnvelope<T>(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string route,
        T body,
        GrainId? replyTo,
        Guid? messageId)
    {
        // Validate the task id and compute the correlation key before serializing the body: several message body
        // types (see DurableTaskMessages.cs) embed the same TaskId, whose surrogate converter throws
        // ArgumentOutOfRangeException for a default TaskId. Without this ordering, that lower-level exception would
        // pre-empt the more specific ArgumentException below, since WithBody() executes eagerly.
        var correlationKey = taskId.ToHierarchicalKey() ?? throw new ArgumentException("The task id must not be empty.", nameof(taskId));
        var builder = new DurableEnvelopeBuilder(sessionPool, sender)
            .To(target, route)
            .WithCorrelationKey(correlationKey)
            .WithBody(body);

        if (replyTo is { } address)
        {
            builder.WithReplyTo(address);
        }

        var envelope = builder.Build();
        return messageId is not { } id
            ? envelope
            : new DurableEnvelope
            {
                MessageId = id,
                SenderId = envelope.SenderId,
                ReceiverId = envelope.ReceiverId,
                RouteKey = envelope.RouteKey,
                CorrelationKey = envelope.CorrelationKey,
                ReplyTo = envelope.ReplyTo,
                Data = envelope.Data,
                CreatedAt = DateTimeOffset.UnixEpoch,
            };
    }

    private static Guid CreateStableMessageId(GrainId sender, GrainId target, TaskId taskId, string route)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(sender.ToString());
        Append(target.ToString());
        Append(taskId.ToString());
        Append(route);
        return new Guid(hash.GetHashAndReset().AsSpan(0, 16));

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
