using System;
using System.Buffers.Binary;
using System.Diagnostics;
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
/// journal write; its identity also covers the delay generation and due time. A signal's identity also covers its name
/// and value. A subscription's, an unsubscription's, and a subscriber's completion's identities also cover the subscribing
/// task, so each subscription of a task has its own messages, and an acceptance's identity covers the task which started
/// the accepted task. The runtime derives readiness from journaled state, so
/// duplicate and stale resumes are harmless.
/// </remarks>
internal sealed class DurableTaskMessageTransport(
    Lazy<IOutbox> outbox,
    DurableTaskMessageTypes messageTypes) : IDurableTaskMessageTransport
{
    internal const string MessageIdRoot = "durable-rpc";

    public void SendInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request, bool acknowledge) =>
        Send(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender, Acknowledge = acknowledge });

    /// <remarks>
    /// The identity of an invocation with a subscriber also covers the subscriber, so each record which starts the same
    /// detached task subscribes to it.
    /// </remarks>
    public void SendDetachedInvocation(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        IDurableTaskRequest request,
        TaskId? subscriberId,
        bool acknowledge) =>
        Send(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage
            {
                TaskId = taskId,
                Request = request,
                Sender = sender,
                Detached = true,
                SubscriberId = subscriberId,
                Acknowledge = acknowledge,
            },
            CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.InvocationSubject, subscriberId?.ToString()),
            deliverAt: null);

    /// <remarks>The identity covers the destination's task, so each start of a task receives its own reply.</remarks>
    public void SendAcceptance(GrainId sender, DurableTaskSubscriber destination, TaskId taskId, DurableTaskResponse? rejection) =>
        Send(
            messageTypes.Acceptance,
            sender,
            destination.GrainId,
            taskId,
            new DurableTaskAcceptanceMessage
            {
                TaskId = taskId,
                Sender = sender,
                SubscriberId = destination.TaskId == taskId ? null : destination.TaskId,
                Rejection = rejection,
            },
            CreateMessageId(sender, destination.GrainId, taskId, DurableTaskMessageTypes.AcceptanceSubject, destination.TaskId.ToString()),
            deliverAt: null);

    public void SendCompletion(MessageId messageId) => outbox.Value.Send(messageId);

    /// <remarks>
    /// The identity of a completion to a subscriber whose task differs from <paramref name="taskId"/> also covers that
    /// task, so each subscription of a task, including one which follows a delivered completion of that task to the same
    /// grain, receives its own completion.
    /// </remarks>
    public MessageId GetCompletionMessageId(GrainId sender, DurableTaskSubscriber destination, TaskId taskId) =>
        CreateMessageId(
            sender,
            destination.GrainId,
            taskId,
            DurableTaskMessageTypes.CompletionSubject,
            destination.TaskId == taskId ? null : destination.TaskId.ToString());

    public OutboxMessage CreateCompletionMessage(
        MessageId messageId,
        GrainId sender,
        DurableTaskSubscriber destination,
        TaskId taskId,
        DurableTaskResponse response) =>
        messageTypes.Completion.Create(
            messageId,
            destination.GrainId,
            CreateCompletion(sender, destination, taskId, response));

    /// <remarks>
    /// The message carries <paramref name="response"/> in its body. Its identity differs from the reference completion of
    /// the same subscriber, and each subscription receives at most one such response.
    /// </remarks>
    public void SendResponse(GrainId sender, DurableTaskSubscriber destination, TaskId taskId, DurableTaskResponse response) =>
        Send(
            messageTypes.Completion,
            sender,
            destination.GrainId,
            taskId,
            CreateCompletion(sender, destination, taskId, response),
            CreateMessageId(
                sender,
                destination.GrainId,
                taskId,
                DurableTaskMessageTypes.CompletionSubject,
                destination.TaskId.ToString(),
                ResponseQualifier),
            deliverAt: null);

    private const string ResponseQualifier = "response";

    private static DurableTaskCompletionMessage CreateCompletion(
        GrainId sender,
        DurableTaskSubscriber destination,
        TaskId taskId,
        DurableTaskResponse response) =>
        new()
        {
            TaskId = taskId,
            Response = response,
            Sender = sender,
            SubscriberId = destination.TaskId == taskId ? null : destination.TaskId,
        };

    /// <remarks>
    /// The identity covers the subscriber, so it differs from an invocation of the task and from other subscriptions.
    /// </remarks>
    public void SendSubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId) =>
        Send(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Sender = sender, SubscriberId = ValidateSubscriber(subscriberId) },
            CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.InvocationSubject, subscriberId.ToString()),
            deliverAt: null);

    /// <remarks>
    /// The identity covers the subscriber, so it differs from a cancellation of the task and from other subscriptions.
    /// </remarks>
    public void SendUnsubscription(GrainId sender, GrainId target, TaskId taskId, TaskId subscriberId) =>
        Send(
            messageTypes.Cancellation,
            sender,
            target,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender, SubscriberId = ValidateSubscriber(subscriberId) },
            CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CancellationSubject, subscriberId.ToString()),
            deliverAt: null);

    private static TaskId ValidateSubscriber(TaskId subscriberId) =>
        subscriberId.IsDefault ? throw new ArgumentException("The subscriber id must not be empty.", nameof(subscriberId)) : subscriberId;

    /// <remarks>
    /// The message carries the cancellation's <paramref name="reason"/>, if any. Its identity covers only the sender,
    /// target, task, and subject: the first cancellation request determines the reason, so every send for a task carries
    /// the same one.
    /// </remarks>
    public void SendCancellation(GrainId sender, GrainId target, TaskId taskId, string? reason = null) =>
        Send(
            messageTypes.Cancellation,
            sender,
            target,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender, CancellationReason = reason });

    /// <remarks>
    /// An abort is a cancellation message with its reason. Its identity differs from the task's cancellation, so an abort
    /// which follows a cancellation is delivered rather than deduplicated against it.
    /// </remarks>
    public void SendAbort(GrainId sender, GrainId target, TaskId taskId, string reason) =>
        Send(
            messageTypes.Cancellation,
            sender,
            target,
            taskId,
            new DurableTaskCancellationMessage { TaskId = taskId, Sender = sender, AbortReason = reason },
            CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.CancellationSubject, AbortDiscriminator),
            deliverAt: null);

    private const string AbortDiscriminator = "abort";

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
    internal static MessageId CreateResumeMessageId(GrainId grainId, TaskId taskId, long generation, DateTimeOffset dueTime) =>
        new(CreateMessageId(
                grainId,
                grainId,
                taskId,
                DurableTaskMessageTypes.ResumeSubject,
                dueTime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Key.CreateChildKey(generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public void SendSignal(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        string payloadFingerprint) =>
        Send(
            messageTypes.Signal,
            sender,
            target,
            taskId,
            new DurableTaskSignalMessage { TaskId = taskId, Name = name, Value = value, Sender = sender },
            CreateSignalMessageId(sender, target, taskId, name, payloadFingerprint),
            deliverAt: null);

    /// <summary>Creates the identity of one signal value.</summary>
    /// <remarks>
    /// A replayed send of the same value coalesces, and the receiver deduplicates its redelivery. Covering the value
    /// gives a conflicting value its own message, which the receiver rejects. The fingerprint has a fixed length, so it
    /// precedes the name unambiguously.
    /// </remarks>
    internal static MessageId CreateSignalMessageId(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string name,
        string payloadFingerprint) =>
        CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.SignalSubject, $"{payloadFingerprint}:{name}");

    private void Send<T>(MessageType<T> messageType, GrainId sender, GrainId target, TaskId taskId, T body) =>
        Send(messageType, sender, target, taskId, body, messageId: default, deliverAt: null);

    private void Send<T>(
        MessageType<T> messageType,
        GrainId sender,
        GrainId target,
        TaskId taskId,
        T body,
        MessageId messageId,
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

    internal static MessageId CreateMessageId(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string subject,
        string? discriminator = null,
        string? qualifier = null)
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

        if (qualifier is not null)
        {
            Debug.Assert(discriminator is not null, "A qualifier refines a discriminated identity.");
            Append(hash, qualifier);
        }

        return MessageId.Create(
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
