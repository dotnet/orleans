using Orleans.DurableTasks;
using MessagingHierarchicalKey = Orleans.DurableMessaging.HierarchicalKey;

namespace Orleans;

/// <summary>
/// Extension methods for converting between <see cref="TaskId"/> and <see cref="MessagingHierarchicalKey"/>.
/// </summary>
/// <remarks>
/// These extensions enable seamless interoperability between the <see cref="TaskId"/> type from
/// Orleans.DurableTasks and the <see cref="MessagingHierarchicalKey"/> type from Orleans Durable Messaging.
/// Both types share the same string representation and hierarchical semantics, making conversion
/// straightforward via string parsing.
/// </remarks>
public static class TaskIdExtensions
{
    /// <summary>
    /// Converts a <see cref="TaskId"/> to an <see cref="MessagingHierarchicalKey"/>.
    /// </summary>
    /// <param name="taskId">The <see cref="TaskId"/> to convert.</param>
    /// <returns>
    /// A <see cref="MessagingHierarchicalKey"/> equivalent to the specified <see cref="TaskId"/>,
    /// or <c>null</c> if the <see cref="TaskId"/> is the default value.
    /// </returns>
    /// <example>
    /// <code>
    /// TaskId taskId = TaskId.Parse("workflow/task-123");
    /// Orleans.DurableMessaging.HierarchicalKey key = taskId.ToHierarchicalKey();
    /// // key.ToString() == "workflow/task-123"
    /// </code>
    /// </example>
    public static MessagingHierarchicalKey? ToHierarchicalKey(this TaskId taskId) =>
        taskId.IsDefault ? null : MessagingHierarchicalKey.Parse(taskId.ToString(), null);

    /// <summary>
    /// Converts a <see cref="MessagingHierarchicalKey"/> to a <see cref="TaskId"/>.
    /// </summary>
    /// <param name="key">The <see cref="MessagingHierarchicalKey"/> to convert.</param>
    /// <returns>
    /// A <see cref="TaskId"/> equivalent to the specified <see cref="MessagingHierarchicalKey"/>,
    /// or <see cref="TaskId.None"/> if the key is <c>null</c>.
    /// </returns>
    /// <example>
    /// <code>
    /// Orleans.DurableMessaging.HierarchicalKey key =
    ///     Orleans.DurableMessaging.HierarchicalKey.Parse("workflow/task-123");
    /// TaskId taskId = key.ToTaskId();
    /// // taskId.ToString() == "workflow/task-123"
    /// </code>
    /// </example>
    public static TaskId ToTaskId(this MessagingHierarchicalKey? key) =>
        key is null ? TaskId.None : TaskId.Parse(key.ToString(), null);
}
