using Orleans.Runtime;

namespace Orleans.DurableTasks.Protocol;

/// <summary>
/// A task of a grain which receives a durable task's terminal result: the task's invoker, under the task's own
/// identifier, or a durable execution's child which subscribed to an existing task with
/// <see cref="DurableTaskGrainExtensions.GetDurableTask{TResult}(IAddressable, TaskId)"/>.
/// </summary>
/// <param name="GrainId">The grain which receives the result.</param>
/// <param name="TaskId">The receiving grain's task which the result completes.</param>
[GenerateSerializer, Immutable]
[Alias("Orleans.DurableTasks.Protocol.DurableTaskSubscriber")]
public readonly record struct DurableTaskSubscriber(
    [property: Id(0)] GrainId GrainId,
    [property: Id(1)] TaskId TaskId);
