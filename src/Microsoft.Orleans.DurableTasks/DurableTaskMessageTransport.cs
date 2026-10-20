using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using Orleans.Journaling;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Stages durable grain RPC messages, including delay resumes, through the grain's outbox.
/// </summary>
/// <remarks>
/// The outbox is resolved on first use so that activations which never send messages do not require messaging state.
/// Each message identity is derived from its sender, receiver, task, and subject, so replaying a send coalesces with the
/// pending message and the receiver deduplicates a redelivery. A message which names a node of the sender's execution
/// tree, such as an attached child or a subscriber, names it by its wire identifier, which covers the run's incarnation
/// (see <see cref="CallerScope"/>), so each run of a root task ID sends messages with their own identities, which
/// Messaging's deduplication keeps apart from an earlier run's. Completions and invocations are reference sends: the
/// runtime provides a completion from the task's stored result, and an invocation from its journaled table of pending
/// invocations, as an <see cref="IOutboxMessageSource"/>, so each payload is journaled once, and the outbox reports each
/// one's retirement to the runtime. A delay resume is a message the grain sends to itself
/// with <see cref="Envelope.DeliverAt"/> set to the delay's due time, which the grain's own inbox accepts in the delay's
/// journal write; its identity also covers the delay's incarnation, generation, and due time. A signal's identity also
/// covers its name, its value, and the sending run's incarnation. A subscription's, an unsubscription's, and a
/// subscriber's completion's identities also cover the subscribing task, so each subscription of a task has its own
/// messages. The runtime derives readiness from journaled state, so duplicate and stale resumes are harmless. The
/// runtime's sends are its own work, even when a durable task body's operation requests them, so they run as the
/// journaled state host's: no context records them as a body's accesses.
/// </remarks>
internal sealed class DurableTaskMessageTransport(
    Lazy<IOutbox> outbox,
    DurableTaskMessageTypes messageTypes,
    IJournaledStateContextHost stateHost) : IDurableTaskMessageTransport
{
    internal const string MessageIdRoot = "durable-rpc";

    public OutboxMessage CreateInvocation(GrainId sender, GrainId target, TaskId taskId, IDurableTaskRequest request) =>
        Create(
            messageTypes.Invocation,
            sender,
            target,
            taskId,
            new DurableTaskInvocationMessage { TaskId = taskId, Request = request, Sender = sender },
            messageId: default,
            deliverAt: null);

    /// <remarks>
    /// The identity of an invocation with a subscriber also covers the subscriber, so each record which starts the same
    /// detached task subscribes to it.
    /// </remarks>
    public OutboxMessage CreateDetachedInvocation(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        IDurableTaskRequest request,
        TaskId? subscriberId) =>
        Create(
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
            },
            CreateMessageId(sender, target, taskId, DurableTaskMessageTypes.InvocationSubject, subscriberId?.ToString()),
            deliverAt: null);

    public OutboxMessage CreateInvocation(MessageId messageId, GrainId target, byte[] payload) =>
        new()
        {
            Envelope = new()
            {
                MessageId = messageId,
                Subject = DurableTaskMessageTypes.InvocationSubject,
                Payload = payload,
            },
            ReceiverId = target,
        };

    public void Send(MessageId messageId)
    {
        stateHost.EnterHostAccess();
        try
        {
            outbox.Value.Send(messageId);
        }
        finally
        {
            stateHost.ExitHostAccess();
        }
    }

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

    public void SendResume(GrainId grainId, TaskId taskId, Guid incarnation, long generation, DateTimeOffset dueTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        Send(
            messageTypes.Resume,
            grainId,
            grainId,
            taskId,
            new DurableTaskResumeMessage { TaskId = taskId, Incarnation = incarnation, Generation = generation, Sender = grainId },
            CreateResumeMessageId(grainId, taskId, incarnation, generation, dueTime),
            dueTime);
    }

    /// <summary>Creates the identity of one delay resume.</summary>
    /// <remarks>
    /// A delay's incarnation, generation, and due time are stable across replays, so a replayed resume coalesces. Covering
    /// the incarnation keeps the identity distinct when a later run of the delay's tree records a delay with the same task
    /// ID and generation, and covering the due time keeps it distinct if a removed delay's task ID is reused while its
    /// earlier resume is still recorded as processed.
    /// </remarks>
    internal static MessageId CreateResumeMessageId(
        GrainId grainId,
        TaskId taskId,
        Guid incarnation,
        long generation,
        DateTimeOffset dueTime) =>
        new(CreateMessageId(
                grainId,
                grainId,
                taskId,
                DurableTaskMessageTypes.ResumeSubject,
                dueTime.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                incarnation.ToString("N"))
            .Key.CreateChildKey(generation.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public void SendSignal(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        string payloadFingerprint,
        Guid incarnation) =>
        Send(
            messageTypes.Signal,
            sender,
            target,
            taskId,
            new DurableTaskSignalMessage { TaskId = taskId, Name = name, Value = value, Sender = sender },
            CreateSignalMessageId(sender, target, taskId, name, payloadFingerprint, incarnation),
            deliverAt: null);

    /// <summary>Creates the identity of one signal value which a run of an execution tree sends.</summary>
    /// <remarks>
    /// A replayed send of the same value coalesces, and the receiver deduplicates its redelivery. Covering the value
    /// gives a conflicting value its own message, which the receiver rejects, and covering the sending run's
    /// <paramref name="incarnation"/> gives each run of the sending tree its own message. The fingerprint and the
    /// incarnation have fixed lengths, so they precede the name unambiguously.
    /// </remarks>
    internal static MessageId CreateSignalMessageId(
        GrainId sender,
        GrainId target,
        TaskId taskId,
        string name,
        string payloadFingerprint,
        Guid incarnation) =>
        CreateMessageId(
            sender,
            target,
            taskId,
            DurableTaskMessageTypes.SignalSubject,
            $"{incarnation:N}:{payloadFingerprint}:{name}");

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
        var message = Create(messageType, sender, target, taskId, body, messageId, deliverAt);
        stateHost.EnterHostAccess();
        try
        {
            outbox.Value.Send(message);
        }
        finally
        {
            stateHost.ExitHostAccess();
        }
    }

    private static OutboxMessage Create<T>(
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

        return messageType.Create(messageId, target, body, deliverAt);
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
