using Orleans.DurableTasks;

namespace Benchmarks.Journaling;

/// <summary>An attempt of a durable task whose body accesses only journaled state.</summary>
internal sealed class BenchmarkAttempt(TaskId taskId) : DurableTaskContext(taskId)
{
    public override DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

    public override Guid Incarnation => Guid.Empty;

    protected override ValueTask<IScheduledTaskHandle> StartTaskAsync(
        TaskId taskId,
        DurableTask taskDefinition,
        DurableTaskStartOptions options,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    protected override ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    protected override ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
