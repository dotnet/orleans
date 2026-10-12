using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableJobs;
using Orleans.DurableTasks;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Stages durable grain RPC messages in the grain's outbox and schedules delay resumes on Durable Jobs.
/// </summary>
/// <remarks>
/// The outbox is resolved on first use so that activations which never send messages do not require messaging state.
/// Each message identity is derived from its sender, receiver, task, and subject, so replaying a send coalesces with the
/// pending message and the receiver deduplicates a redelivery. Resume jobs carry the task and delay generation;
/// the runtime derives readiness from journaled state, so duplicate and stale resumes are harmless.
/// </remarks>
internal sealed class DurableTaskMessageTransport(
    Lazy<IOutbox> outbox,
    ILocalDurableJobManager jobManager,
    DurableTaskMessageTypes messageTypes) : IDurableTaskMessageTransport
{
    internal const string MessageIdRoot = "durable-rpc";
    internal const string ResumeJobName = "orleans.durable-rpc.resume";
    internal const string ResumeTaskIdMetadata = "orleans.durable-rpc.task-id";
    internal const string ResumeGenerationMetadata = "orleans.durable-rpc.generation";

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Send(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender });

    public void SendCompletion(GrainId sender, GrainId target, TaskId taskId, DurableTaskResponse response) =>
        Send(
            messageTypes.Completion,
            sender,
            target,
            taskId,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = response, Sender = sender });

    public void SendCompletionAck(GrainId sender, GrainId target, TaskId taskId) =>
        Send(
            messageTypes.CompletionAck,
            sender,
            target,
            taskId,
            new DurableTaskCompletionAckMessage { TaskId = taskId, Sender = sender });

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) =>
        Send(
            messageTypes.Cancellation,
            sender,
            target,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender });

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

    private void Send<T>(MessageType<T> messageType, GrainId sender, GrainId target, TaskId taskId, T body)
    {
        // Validate the task id before serializing the body: the TaskId surrogate converter rejects a default TaskId
        // with a less specific exception.
        if (taskId.IsDefault)
        {
            throw new ArgumentException("The task id must not be empty.", nameof(taskId));
        }

        var message = messageType.Create(CreateMessageId(sender, target, taskId, messageType.Subject), target, body);
        outbox.Value.Send(message);
    }

    internal static HierarchicalKey CreateMessageId(GrainId sender, GrainId target, TaskId taskId, string subject)
    {
        if (!subject.StartsWith(DurableTaskMessageTypes.SubjectPrefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported durable task message subject '{subject}'.", nameof(subject));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, sender.ToString());
        Append(hash, target.ToString());
        Append(hash, taskId.ToString());
        Append(hash, subject);
        return HierarchicalKey.Create(
            MessageIdRoot,
            subject[DurableTaskMessageTypes.SubjectPrefix.Length..],
            Convert.ToHexString(hash.GetHashAndReset()));

        static void Append(IncrementalHash hash, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }
    }
}
