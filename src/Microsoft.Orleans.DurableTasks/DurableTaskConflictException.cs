using Orleans.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents a schedule of a request under a task identifier which the target grain already associates with a different
/// operation, such as a different method or caller.
/// </summary>
/// <remarks>
/// A task identifier deduplicates schedules of the same method and caller. Repeated starts of that operation return the
/// stored task, including when the arguments differ. A start with a different method or caller receives this exception,
/// and the existing task is unchanged.
/// </remarks>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.DurableTaskConflictException")]
public sealed class DurableTaskConflictException : InvalidOperationException
{
    private DurableTaskConflictException()
    {
    }

    /// <summary>Initializes an exception for <paramref name="taskId"/>.</summary>
    /// <param name="taskId">The task identifier which the target associates with another operation.</param>
    /// <param name="message">A description of the recorded and the requested operations.</param>
    public DurableTaskConflictException(TaskId taskId, string message)
        : base(message)
    {
        TaskId = taskId;
    }

    /// <summary>Gets the task identifier which the target associates with another operation.</summary>
    [Id(0)]
    public TaskId TaskId { get; private set; }
}
