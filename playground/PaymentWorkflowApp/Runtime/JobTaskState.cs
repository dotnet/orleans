using System.Distributed.DurableTasks;
namespace PaymentWorkflowApp.Runtime;

[GenerateSerializer]
[Alias("JobTaskState")]
public class JobTaskState
{
    /// <summary>
    /// Gets or sets the result of this task.
    /// </summary>
    [Id(0)]
    public DurableTaskResponse? Result { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(1)]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the invokable request.
    /// </summary>
    [Id(2)]
    public string[]? Arguments { get; set; }

    /// <summary>
    /// Gets or sets the time that the task completed.
    /// </summary>
    [Id(3)]
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Gets or sets the time that the task was created.
    /// </summary>
    [Id(4)]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the tasks which 
    /// </summary>
    [Id(5)]
    public TaskId[]? CanceledAfter { get; set; }
}
