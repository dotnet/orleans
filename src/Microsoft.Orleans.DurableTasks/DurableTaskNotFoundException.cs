using Orleans.Serialization;

namespace Orleans.DurableTasks;

/// <summary>
/// Represents an existing durable task identifier whose state is unavailable or has expired.
/// </summary>
[GenerateSerializer]
[Alias("Orleans.DurableTasks.DurableTaskNotFoundException")]
public sealed class DurableTaskNotFoundException : Exception
{
    private DurableTaskNotFoundException()
    {
    }

    /// <summary>
    /// Initializes an exception for <paramref name="taskId"/>.
    /// </summary>
    /// <param name="taskId">The durable task identifier.</param>
    public DurableTaskNotFoundException(TaskId taskId)
        : base($"Durable task '{taskId}' was not found or its retained state has expired.")
    {
        TaskId = taskId;
    }

    /// <summary>
    /// Gets the unavailable durable task identifier.
    /// </summary>
    [Id(0)]
    public TaskId TaskId { get; private set; }

    internal static DurableTaskResponse CreateResponse(TaskId taskId) =>
        DurableTaskResponse.FromException(new DurableTaskNotFoundException(taskId));
}
