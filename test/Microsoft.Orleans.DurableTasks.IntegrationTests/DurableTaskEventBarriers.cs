using Orleans.DurableTasks;
using Orleans.DurableTasks.Diagnostics;
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

    private static Task<DiagnosticEvent> Wait<TEvent>(DiagnosticEventCollector events, GrainId grainId, TaskId taskId)
        where TEvent : DurableTaskEvents.DurableTaskEvent =>
        events.WaitForEventAsync(
            typeof(TEvent).Name,
            evt => evt.Payload is TEvent payload && payload.GrainContext.GrainId == grainId && payload.TaskId == taskId,
            Timeout,
            TestContext.Current.CancellationToken);
}
