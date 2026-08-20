using System.Distributed.DurableTasks;

namespace Orleans.DurableTasks;

[GenerateSerializer]
internal sealed class DurableTaskInvocationMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public required IDurableTaskRequest Request { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCompletionMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }

    [Id(1)]
    public required DurableTaskResponse Response { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCompletionAckMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }
}

[GenerateSerializer]
internal sealed class DurableTaskCancellationMessage
{
    [Id(0)]
    public TaskId TaskId { get; init; }
}
