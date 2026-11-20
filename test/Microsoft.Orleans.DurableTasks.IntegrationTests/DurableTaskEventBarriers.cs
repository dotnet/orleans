using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Orleans.TestingHost.Diagnostics;
using Xunit;

namespace Microsoft.Orleans.DurableTasks.IntegrationTests;

/// <summary>Waits for durable task runtime transitions which a <see cref="DiagnosticEventCollector"/> captured.</summary>
/// <remarks>Arm the collector before the action which causes the transition, so it captures the event.</remarks>
internal static class DurableTaskEventBarriers
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates a collector for the durable task runtime's events.</summary>
    public static DiagnosticEventCollector Collect() => new(DurableTaskEvents.ListenerName);

    /// <summary>Waits until <paramref name="grainId"/>'s runtime staged the terminal result of <paramref name="taskId"/>.</summary>
    public static async Task<DurableTaskResponse> WaitForCompletedAsync(this DiagnosticEventCollector events, GrainId grainId, TaskId taskId) =>
        ((DurableTaskEvents.TaskCompleted)(await Wait<DurableTaskEvents.TaskCompleted>(events, grainId, taskId)).Payload!).Response;

    /// <summary>Waits until <paramref name="grainId"/>'s runtime staged the deletion of <paramref name="taskId"/>.</summary>
    public static Task WaitForDeletedAsync(this DiagnosticEventCollector events, GrainId grainId, TaskId taskId) =>
        Wait<DurableTaskEvents.TaskDeleted>(events, grainId, taskId);

    /// <summary>
    /// Waits until <paramref name="calleeId"/>'s runtime created the task which it stores for the node
    /// <paramref name="nodeId"/> which <paramref name="callerId"/> invoked, and returns that task's ID.
    /// </summary>
    /// <remarks>
    /// The callee stores the node under the caller's scope by its wire ID, which covers the incarnation of the caller's
    /// run, so a test finds the ID which the run uses rather than deriving it.
    /// </remarks>
    public static async Task<TaskId> WaitForCallerChildAsync(this DiagnosticEventCollector events, GrainId calleeId, GrainId callerId, TaskId nodeId)
    {
        var created = await events.WaitForEventAsync(
            nameof(DurableTaskEvents.TaskCreated),
            evt => evt.Payload is DurableTaskEvents.TaskCreated payload
                && payload.GrainContext.GrainId == calleeId
                && IsCallerChild(callerId, nodeId, payload.TaskId),
            Timeout,
            TestContext.Current.CancellationToken);
        return ((DurableTaskEvents.TaskCreated)created.Payload!).TaskId;
    }

    /// <summary>
    /// Returns whether a callee stores <paramref name="taskId"/> for the node <paramref name="nodeId"/> which
    /// <paramref name="callerId"/> invoked, in any run of the node's tree.
    /// </summary>
    public static bool IsCallerChild(GrainId callerId, TaskId nodeId, TaskId taskId) =>
        CallerScope.Root.Child(callerId.ToString()).IsAncestorOf(taskId)
        && CallerScope.TryParseWireId(CallerScope.GetCallerTaskId(callerId, taskId), out _, out var callerNodeId)
        && callerNodeId == nodeId;

    private static Task<DiagnosticEvent> Wait<TEvent>(DiagnosticEventCollector events, GrainId grainId, TaskId taskId)
        where TEvent : DurableTaskEvents.DurableTaskEvent =>
        events.WaitForEventAsync(
            typeof(TEvent).Name,
            evt => evt.Payload is TEvent payload && payload.GrainContext.GrainId == grainId && payload.TaskId == taskId,
            Timeout,
            TestContext.Current.CancellationToken);
}
