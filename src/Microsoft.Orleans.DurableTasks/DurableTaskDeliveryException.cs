using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents the start of a durable task whose invocation its grain's outbox dead-lettered, because it wasn't delivered
/// to the task's grain before Messaging's <c>OutboxRetryTimeout</c> elapsed.
/// </summary>
/// <remarks>
/// The task's grain never acknowledged the invocation, so the starter's record of the task fails with this exception:
/// awaiting the task, or a <c>ScheduleAsync</c> which awaits its acceptance, throws it. The outbox's dead letters hold the
/// invocation for inspection.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.DurableTaskDeliveryException")]
public sealed class DurableTaskDeliveryException : Exception
{
    private DurableTaskDeliveryException()
    {
    }

    /// <summary>Initializes an exception for the invocation of <paramref name="taskId"/> on <paramref name="target"/>.</summary>
    /// <param name="taskId">The task which the invocation starts.</param>
    /// <param name="target">The grain to which the invocation is addressed.</param>
    /// <param name="reason">The dead letter's reason.</param>
    public DurableTaskDeliveryException(TaskId taskId, GrainId target, string reason)
        : base($"The invocation of durable task '{taskId}' wasn't delivered to grain '{target}': {reason}")
    {
        TaskId = taskId;
        Target = target;
    }

    /// <summary>Gets the task which the invocation starts.</summary>
    [Id(0)]
    public TaskId TaskId { get; private set; }

    /// <summary>Gets the grain to which the invocation is addressed.</summary>
    [Id(1)]
    public GrainId Target { get; private set; }
}
