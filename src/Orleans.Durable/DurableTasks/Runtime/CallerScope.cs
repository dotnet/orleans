using System;
using System.Diagnostics;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Maps the tasks which grains invoke on other grains to the identifiers under which their targets store them, and the
/// nodes of a grain's execution trees to the wire identifiers which other grains address.
/// </summary>
/// <remarks>
/// <para>
/// A root task identifier names a new run each time a grain stores its invocation after deleting an earlier run, and
/// each run has its own incarnation, a nonce. A message which names a node of an execution tree, such as a call to
/// another grain or a subscription, names it by its wire identifier, <c>{incarnation:N}/{nodeId}</c>: the incarnation of
/// the node's tree, formatted as 32 hexadecimal digits, as the root segment, followed by the node's own segments, such as
/// <c>9f3c…e1/place-order/reserve</c>. Each run therefore sends its own messages, which Messaging's deduplication keeps
/// apart from an earlier run's, and a reply which names an earlier run's node is recognized as stale. The wire identifier
/// is a pure function of the run and the node, so replays and redeliveries within a run reach the same task.
/// </para>
/// <para>
/// A grain invokes another grain's task as a child of one of its own executions. Root identifiers are scoped per grain,
/// so two callers can invoke children with the same identifier on one target. The target stores each under its
/// caller's scope, <c>$caller/{callerGrainId}/{wireId}</c>, so two callers, and two runs of one caller, never share
/// state, and maps the caller's identifier at the boundary: the invocation, cancellation, and abort which the caller
/// sends address its child's wire identifier, and the completion which the target sends names that wire identifier as
/// its subscriber. A root identifier, which a detached start sends, addresses the target's root task itself.
/// </para>
/// <para>
/// The scope's root, <see cref="Root"/>, is reserved: only invocations from other grains create tasks beneath it, so no
/// other task shares an identifier with a scoped one. Root tasks, which clients and detached starts create, and the
/// operations of each execution keep their identifiers.
/// </para>
/// </remarks>
internal static class CallerScope
{
    // An incarnation's "N" format: 32 hexadecimal digits.
    private const int IncarnationLength = 32;

    /// <summary>The reserved root under which a grain stores the tasks which other grains invoke.</summary>
    public static TaskId Root { get; } = TaskId.CreateRoot("$caller");

    /// <summary>
    /// Returns the wire identifier of the node <paramref name="nodeId"/> of the execution tree whose run is
    /// <paramref name="incarnation"/>: <c>{incarnation:N}/{nodeId}</c>.
    /// </summary>
    public static TaskId GetWireId(Guid incarnation, TaskId nodeId)
    {
        Debug.Assert(incarnation != Guid.Empty, "Every run of an execution tree has an incarnation.");
        Debug.Assert(!nodeId.IsDefault, "A wire identifier names a node.");
        return TaskId.Parse($"{incarnation:N}/{nodeId}");
    }

    /// <summary>
    /// Parses a wire identifier which <see cref="GetWireId"/> formatted into its run's incarnation and its node.
    /// </summary>
    /// <returns><see langword="false"/> when <paramref name="wireId"/> isn't a wire identifier.</returns>
    public static bool TryParseWireId(TaskId wireId, out Guid incarnation, out TaskId nodeId)
    {
        incarnation = default;
        nodeId = default;
        var id = wireId.ToString();
        return id.Length > IncarnationLength + 1
            && id[IncarnationLength] == '/'
            && Guid.TryParseExact(id.AsSpan(0, IncarnationLength), "N", out incarnation)
            && TaskId.TryParse(id.AsSpan(IncarnationLength + 1), out nodeId);
    }

    /// <summary>
    /// Returns the target's identifier of the task <paramref name="taskId"/> which a message from
    /// <paramref name="caller"/> addresses: the target's root task itself, or the caller's child, whose wire identifier
    /// <paramref name="taskId"/> is, under the caller's scope.
    /// </summary>
    public static TaskId Resolve(GrainId caller, TaskId taskId) =>
        taskId.Parent().IsDefault ? taskId : GetTaskId(caller, taskId);

    /// <summary>
    /// Returns the identifier under which a target stores the child which <paramref name="caller"/> invoked, whose wire
    /// identifier is <paramref name="callerTaskId"/>.
    /// </summary>
    public static TaskId GetTaskId(GrainId caller, TaskId callerTaskId)
    {
        Debug.Assert(!caller.IsDefault, "A scoped task has a calling grain.");
        Debug.Assert(!callerTaskId.Parent().IsDefault, "A grain invokes another grain's task as a node of one of its execution trees.");
        return TaskId.Parse($"{GetPrefix(caller)}{callerTaskId}");
    }

    /// <summary>
    /// Returns the identifier which <paramref name="caller"/> addresses for the task <paramref name="taskId"/> which it
    /// invoked: its child's wire identifier, or <paramref name="taskId"/> when <paramref name="caller"/> didn't invoke it
    /// under its scope.
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

    /// <summary>
    /// Throws when <paramref name="taskId"/> is the reserved root of the caller scopes or lies beneath it, which only
    /// invocations from other grains create.
    /// </summary>
    public static void ThrowIfReserved(TaskId taskId)
    {
        if (Root.IsAncestorOf(taskId))
        {
            throw new ArgumentException(
                $"Task ID '{taskId}' is reserved: the root task ID '{Root}' holds the tasks which other grains invoke.",
                nameof(taskId));
        }
    }

    private static string GetPrefix(GrainId caller) => $"{Root.Child(caller.ToString())}/";
}
