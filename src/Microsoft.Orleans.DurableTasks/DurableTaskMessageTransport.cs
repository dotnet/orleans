using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Stages durable grain RPC messages, including delay resumes, through the grain's outbox.
/// </summary>
/// <remarks>
/// The outbox is resolved on first use so that activations which never send messages do not require messaging state.
/// Each message identity is derived from its sender, receiver, task, and subject, so replaying a send coalesces with the
/// pending message and the receiver deduplicates a redelivery. A completion is a reference send: the runtime provides
/// the message from the task's stored result as an <see cref="IOutboxMessageSource"/>, so the result is journaled once.
/// A delay resume is a message the grain sends to itself
/// with <see cref="Envelope.DeliverAt"/> set to the delay's due time, which the grain's own inbox accepts in the delay's
/// journal write; its identity also covers the delay generation and due time. The runtime derives readiness from
/// journaled state, so duplicate and stale resumes are harmless.
/// </remarks>
internal sealed class DurableTaskMessageTransport(
    Lazy<IOutbox> outbox,
    DurableTaskMessageTypes messageTypes) : IDurableTaskMessageTransport
{
    internal const string MessageIdRoot = "durable-rpc";

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Send(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender });

    public void SendCompletion(HierarchicalKey messageId) => outbox.Value.Send(messageId);

    public HierarchicalKey GetCompletionMessageId(GrainId sender, GrainId target, TaskId taskId) =>
        CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CompletionSubject);

    public OutboxMessage CreateCompletionMessage(
        HierarchicalKey messageId,
        GrainId sender,
        GrainId target,
        TaskId taskId,
        DurableTaskResponse response) =>
        messageTypes.Completion.Create(
            messageId,
            target,
            new DurableTaskCompletionMessage { TaskId = taskId, Response = response, Sender = sender });

    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId) =>
        Send(
            messageTypes.Cancellation,
            sender,
            target,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender });

    public void SendResume(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Send(
            messageTypes.Resume,
            grainId,
            grainId,
            taskId,
            new DurableTaskResumeMessage { TaskId = taskId, Generation = generation, Sender = grainId },
            CreateResumeMessageId(grainId, taskId, generation, dueTime),
            dueTime);
    }

    /// <summary>Creates the identity of one delay resume.</summary>
    /// <remarks>
    /// A delay's generation and due time are stable across replays, so a replayed resume coalesces. Covering the due
    /// time keeps the identity distinct if a removed delay's task ID is reused while its earlier resume is still
    /// recorded as processed.
    /// </remarks>
    internal static HierarchicalKey CreateResumeMessageId(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime) =>
        CreateMessageId(
            grainId,
            grainId,
            taskId,
            DurableTaskMessageTypes.ResumeSubject,
            dueTime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .CreateChildKey(generation.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void Send<T>(MessageType<T> messageType, GrainId sender, GrainId target, TaskId taskId, T body) =>
        Send(messageType, sender, target, taskId, body, messageId: default, deliverAt: null);

    private void Send<T>(
        MessageType<T> messageType,
        GrainId sender,
        GrainId target,
        TaskId taskId,
        T body,
        HierarchicalKey messageId,
        DateTimeOffset? deliverAt)
    {
        // Validate the task id before serializing the body: the TaskId surrogate converter rejects a default TaskId
        // with a less specific exception.
        if (taskId.IsDefault)
        {
            throw new ArgumentException("The task id must not be empty.", nameof(taskId));
        }

        if (messageId.IsDefault)
        {
            messageId = CreateMessageId(sender, target, taskId, messageType.Subject);
        }

        outbox.Value.Send(messageType.Create(messageId, target, body, deliverAt));
    }

    internal static HierarchicalKey CreateMessageId(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string subject,
        string? discriminator = null)
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
        if (discriminator is not null)
        {
            Append(hash, discriminator);
        }

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
