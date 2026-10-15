using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents the start of a durable task whose grain permanently rejected the invocation for a reason other than a
/// conflict, which it reports with a rejection code of its own, or without one.
/// </summary>
/// <remarks>
/// The task's grain dead-lettered the invocation and stored nothing, so the starter's record of the task fails with this
/// exception: awaiting the task, or a <c>ScheduleAsync</c> which awaits its acceptance, throws it, in every replay. A
/// rejection for a conflicting task identifier fails with <see cref="DurableTaskConflictException"/> instead.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.DurableTaskRejectedException")]
public sealed class DurableTaskRejectedException : Exception
{
    private DurableTaskRejectedException()
    {
    }

    /// <summary>Initializes an exception for the rejected invocation of <paramref name="taskId"/> on <paramref name="target"/>.</summary>
    /// <param name="taskId">The task which the invocation starts.</param>
    /// <param name="target">The grain which rejected the invocation.</param>
    /// <param name="code">
    /// The rejection code which the grain reported, or <see langword="null"/> for none.
    /// </param>
    /// <param name="reason">The grain's reason.</param>
    public DurableTaskRejectedException(TaskId taskId, GrainId target, string? code, string reason)
        : base(code is null
            ? $"Grain '{target}' rejected the invocation of durable task '{taskId}': {reason}"
            : $"Grain '{target}' rejected the invocation of durable task '{taskId}' ({code}): {reason}")
    {
        TaskId = taskId;
        Target = target;
        Code = code;
    }

    /// <summary>Gets the task which the invocation starts.</summary>
    [Id(0)]
    public TaskId TaskId { get; private set; }

    /// <summary>Gets the grain which rejected the invocation.</summary>
    [Id(1)]
    public GrainId Target { get; private set; }

    /// <summary>Gets the rejection code which the grain reported, or <see langword="null"/> for none.</summary>
    [Id(2)]
    public string? Code { get; private set; }
}
