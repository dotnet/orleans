using System.Distributed.DurableTasks;

namespace Orleans;

/// <summary>
/// Extension methods for converting between <see cref="TaskId"/> and <see cref="HierarchicalKey"/>.
/// </summary>
/// <remarks>
/// These extensions enable seamless interoperability between the <see cref="TaskId"/> type from
/// System.Distributed.DurableTasks and the <see cref="HierarchicalKey"/> type from Orleans.
/// Both types share the same string representation and hierarchical semantics, making conversion
/// straightforward via string parsing.
/// </remarks>
public static class TaskIdExtensions
{
    /// <summary>
    /// Converts a <see cref="TaskId"/> to an <see cref="HierarchicalKey"/>.
    /// </summary>
    /// <param name="taskId">The <see cref="TaskId"/> to convert.</param>
    /// <returns>
    /// A <see cref="HierarchicalKey"/> equivalent to the specified <see cref="TaskId"/>,
    /// or <c>null</c> if the <see cref="TaskId"/> is the default value.
    /// </returns>
    /// <example>
    /// <code>
    /// TaskId taskId = TaskId.Create("workflow/task-123");
    /// HierarchicalKey key = taskId.ToHierarchicalKey();
    /// // key.ToString() == "workflow/task-123"
    /// </code>
    /// </example>
    public static HierarchicalKey? ToHierarchicalKey(this TaskId taskId) =>
        taskId.IsDefault ? null : HierarchicalKey.Parse(taskId.ToString(), null);

    /// <summary>
    /// Converts a <see cref="HierarchicalKey"/> to a <see cref="TaskId"/>.
    /// </summary>
    /// <param name="key">The <see cref="HierarchicalKey"/> to convert.</param>
    /// <returns>
    /// A <see cref="TaskId"/> equivalent to the specified <see cref="HierarchicalKey"/>,
    /// or <see cref="TaskId.None"/> if the key is <c>null</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// HierarchicalKey key = HierarchicalKey.Create("workflow/task-123");
    /// TaskId taskId = key.ToTaskId();
    /// // taskId.ToString() == "workflow/task-123"
    /// </code>
    /// </example>
    public static TaskId ToTaskId(this HierarchicalKey? key) =>
        key is null ? TaskId.None : TaskId.Parse(key.ToString(), null);
}
