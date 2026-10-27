using Orleans.DurableTasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Benchmarks.Journaling;

/// <summary>
/// The durable task runtime of the attempts which <see cref="AccessRecordingFixture"/> runs: it creates each attempt's
/// access log over <see cref="Storage"/>, as the grain runtime does over its grain storage, and supports no durable
/// operation besides the body's accesses to journaled state.
/// </summary>
internal sealed class AccessRecordingRuntime(Serializer serializer) : IDurableTaskGrainRuntime
{
    /// <summary>Gets or sets the storage of the access log of the next attempt, or <see langword="null"/> to record nothing.</summary>
    public IDurableTaskAccessLogStorage? Storage { get; set; }

    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;

    public GrainId GrainId => default;

    public DurableTaskAccessLog? CreateAccessLog(
        DurableTaskContext attempt,
        TaskScheduler scheduler,
        Func<DurableTaskDivergenceException, Exception> diverge) =>
        Storage is { } storage ? new(attempt, storage, serializer, scheduler, diverge) : null;

    public Guid GetIncarnation(TaskId executionId) => Guid.Empty;

    public DateTimeOffset? GetCompletedAt(TaskId taskId) => null;

    public ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask taskDefinition,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public IScheduledTaskHandle PostChild(TaskId taskId, DurableTask taskDefinition, bool shielded) => throw new NotSupportedException();

    public ValueTask<IScheduledTaskHandle> StartDetachedAsync(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public IScheduledTaskHandle PostDetached(TaskId recordId, TaskId rootId, IDurableTaskRequest request, bool shielded) =>
        throw new NotSupportedException();

    public void PostRoot(TaskId rootId, IDurableTaskRequest request) => throw new NotSupportedException();

    public ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, string? reason, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<TaskId> SelectCompletionAsync(
        GrainDurableTaskContext attempt,
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId) => throw new NotSupportedException();

    public TaskId GetTargetTaskId(TaskId taskId, out GrainId addressedHost) => throw new NotSupportedException();
}
