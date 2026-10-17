using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Maps the tasks which grains invoke on other grains to the identifiers under which their targets store them.
/// </summary>
/// <remarks>
/// <para>
/// A grain invokes another grain's task as a child of one of its own executions, under the child's identifier in the
/// caller's tree, such as <c>place-order/reserve</c>. Root identifiers are scoped per grain, so two callers can invoke
/// children with the same identifier on one target. The target stores each under its caller's scope,
/// <c>$caller/{callerGrainId}/{callerTaskId}</c>, so the two never share state, and maps the caller's identifier at the
/// boundary: the invocation, cancellation, and abort which the caller sends address its own child, and the completion
/// which the target sends names the caller's child as its subscriber. A root identifier, which a detached start sends,
/// addresses the target's root task itself.
/// </para>
/// <para>
/// The mapping is a pure function of the caller and its child's identifier, so replays and redeliveries reach the same
/// task. The scope's root, <see cref="Root"/>, is reserved: no task is scheduled under it, so no other task shares an
/// identifier with a scoped one. Root tasks, which clients and detached starts create, and the operations of each
/// execution keep their identifiers.
/// </para>
/// </remarks>
internal static class CallerScope
{
    /// <summary>The reserved root under which a grain stores the tasks which other grains invoke.</summary>
    public static TaskId Root { get; } = TaskId.CreateRoot("$caller");

    /// <summary>
    /// Returns the target's identifier of the task <paramref name="taskId"/> which a message from
    /// <paramref name="caller"/> addresses: the target's root task itself, or the caller's child, under the caller's scope.
    /// </summary>
    public static TaskId Resolve(GrainId caller, TaskId taskId) =>
        taskId.Parent().IsDefault ? taskId : GetTaskId(caller, taskId);

    /// <summary>
    /// Returns the identifier under which a target stores the child <paramref name="callerTaskId"/> which
    /// <paramref name="caller"/> invoked.
    /// </summary>
    public static TaskId GetTaskId(GrainId caller, TaskId callerTaskId)
    {
        Debug.Assert(!caller.IsDefault, "A scoped task has a calling grain.");
        Debug.Assert(!callerTaskId.Parent().IsDefault, "A grain invokes another grain's task as a child of one of its executions.");
        return TaskId.Parse($"{GetPrefix(caller)}{callerTaskId}");
    }

    /// <summary>
    /// Returns the identifier in <paramref name="caller"/>'s tree of the task <paramref name="taskId"/> which it invoked, or
    /// <paramref name="taskId"/> when <paramref name="caller"/> didn't invoke it under its scope.
    /// </summary>
    public static TaskId GetCallerTaskId(GrainId caller, TaskId taskId)
    {
        if (caller.IsDefault)
        {
            return taskId;
        }

        var prefix = GetPrefix(caller);
        var id = taskId.ToString();
        return id.StartsWith(prefix, StringComparison.Ordinal) ? TaskId.Parse(id.AsSpan(prefix.Length)) : taskId;
    }

    /// <summary>Throws when <paramref name="rootId"/> is the reserved root of the caller scopes.</summary>
    public static void ThrowIfReserved(TaskId rootId)
    {
        if (rootId == Root)
        {
            throw new ArgumentException(
                $"The root task ID '{Root}' is reserved for the tasks which other grains invoke.",
                nameof(rootId));
        }
    }

    private static string GetPrefix(GrainId caller) => $"{Root.Child(caller.ToString())}/";
}
