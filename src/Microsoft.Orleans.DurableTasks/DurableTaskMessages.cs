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

    [Id(1)]
    public required IDurableTaskRequest Request { get; init; }

    [Id(2)]
    public GrainId Sender { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCompletionMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public required DurableTaskResponse Response { get; init; }

    [Id(2)]
    public GrainId Sender { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCompletionAckMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public GrainId Sender { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCancellationMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
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
    public const string CompletionAckSubject = SubjectPrefix + "complete-ack";
    public const string CancellationSubject = SubjectPrefix + "cancel";

    public MessageType<DurableTaskInvocationMessage> Invocation { get; } = new(InvocationSubject, serializer);

    public MessageType<DurableTaskCompletionMessage> Completion { get; } = new(CompletionSubject, serializer);

    public MessageType<DurableTaskCompletionAckMessage> CompletionAck { get; } = new(CompletionAckSubject, serializer);

    public MessageType<DurableTaskCancellationMessage> Cancellation { get; } = new(CancellationSubject, serializer);
}
