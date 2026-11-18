using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents the start of a durable task whose invocation the starter's outbox dead-lettered without a confirmed
/// outcome, so whether the task's grain ran the task is unknown.
/// </summary>
/// <remarks>
/// <para>
/// The outbox dead-letters an invocation which it couldn't confirm before Messaging's <c>OutboxRetryTimeout</c> elapsed, or
/// which the task's grain fenced because its admission cutoff had passed. In either case an earlier attempt may have
/// reached the grain, which may have stored and run the task, and lost only its acknowledgement. The starter's record of
/// the task fails with this exception: awaiting the task, or a <c>ScheduleAsync</c> which awaits its acceptance, throws it.
/// The outbox's dead letters hold the invocation for inspection.
/// </para>
/// <para>
/// Treat the outcome as unknown. Before starting the work again under another task identifier, establish whether it ran,
/// for example by polling the task on its grain or by checking the effects which it makes, so its side effects aren't
/// duplicated.
/// </para>
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
        : base($"The delivery of durable task '{taskId}' to grain '{target}' wasn't confirmed, so whether the grain ran it is unknown: {reason}")
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
