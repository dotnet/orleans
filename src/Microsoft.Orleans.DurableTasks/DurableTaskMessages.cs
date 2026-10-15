using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using Orleans.Messaging;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

[GenerateSerializer]
internal sealed class DurableTaskInvocationMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    /// <summary>
    /// Gets the request which the receiver schedules, or <see langword="null"/> for a subscription, which carries
    /// <see cref="SubscriberId"/> instead.
    /// </summary>
    [Id(1)]
    public IDurableTaskRequest? Request { get; init; }

    [Id(2)]
    public GrainId Sender { get; init; }

    /// <summary>
    /// Gets whether the invocation starts a detached task, which the receiver retains like a client-scheduled root and
    /// whose result it sends to no caller.
    /// </summary>
    [Id(3)]
    public bool Detached { get; init; }

    /// <summary>
    /// Gets the sender's task which subscribes to the result of the receiver's existing task <see cref="TaskId"/>, or
    /// <see langword="null"/> for an invocation which carries a request.
    /// </summary>
    /// <remarks>
    /// A subscription neither schedules nor claims the task: the receiver records no caller and validates none, and it
    /// adds the subscriber to the task's completion destinations.
    /// </remarks>
    [Id(4)]
    public TaskId? SubscriberId { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCompletionMessage
{
    /// <summary>Gets the sender's task whose terminal result the message carries.</summary>
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public required DurableTaskResponse Response { get; init; }

    [Id(2)]
    public GrainId Sender { get; init; }

    /// <summary>
    /// Gets the receiver's task which the result completes, when it differs from <see cref="TaskId"/>: a task which
    /// subscribed to the sender's task without invoking it.
    /// </summary>
    [Id(3)]
    public TaskId? SubscriberId { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCancellationMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public GrainId Sender { get; init; }

    /// <summary>
    /// Gets the reason for an administrative abort of the task, or <see langword="null"/> for a cooperative cancellation
    /// request.
    /// </summary>
    [Id(2)]
    public string? AbortReason { get; init; }

    /// <summary>
    /// Gets the reason which a cooperative cancellation request carries, or <see langword="null"/> when it carries none.
    /// </summary>
    [Id(3)]
    public string? CancellationReason { get; init; }

    /// <summary>
    /// Gets the sender's task whose subscription to <see cref="TaskId"/> the message ends, or <see langword="null"/> for a
    /// cancellation or abort of the task. Ending a subscription doesn't cancel the task.
    /// </summary>
    [Id(4)]
    public TaskId? SubscriberId { get; init; }
}

/// <summary>
/// Resumes a durable delay. The owning grain sends it to itself, scheduled for the delay's due time.
/// </summary>
[GenerateSerializer]
internal sealed class DurableTaskResumeMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public long Generation { get; init; }

    [Id(2)]
    public GrainId Sender { get; init; }
}

/// <summary>
/// Delivers a signal's value to an invoked task, which records the first value for each signal name.
/// </summary>
[GenerateSerializer]
internal sealed class DurableTaskSignalMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public required string Name { get; init; }

    /// <summary>Gets the successful response which holds the signal's value.</summary>
    [Id(2)]
    public required DurableTaskResponse Value { get; init; }

    [Id(3)]
    public GrainId Sender { get; init; }
}

/// <summary>
/// The durable grain RPC message subjects and their payload contracts.
/// </summary>
/// <remarks>
/// Messaging envelopes carry no sender identity, so each body carries the sending grain.
/// </remarks>
internal sealed class DurableTaskMessageTypes(Serializer serializer)
{
    public const string SubjectPrefix = "durable-rpc/";
    public const string InvocationSubject = SubjectPrefix + "invoke";
    public const string CompletionSubject = SubjectPrefix + "complete";
    public const string CancellationSubject = SubjectPrefix + "cancel";
    public const string ResumeSubject = SubjectPrefix + "resume";
    public const string SignalSubject = SubjectPrefix + "signal";

    public MessageType<DurableTaskInvocationMessage> Invocation { get; } = new(InvocationSubject, serializer);

    public MessageType<DurableTaskCompletionMessage> Completion { get; } = new(CompletionSubject, serializer);

    public MessageType<DurableTaskCancellationMessage> Cancellation { get; } = new(CancellationSubject, serializer);

    public MessageType<DurableTaskResumeMessage> Resume { get; } = new(ResumeSubject, serializer);

    public MessageType<DurableTaskSignalMessage> Signal { get; } = new(SignalSubject, serializer);
}
