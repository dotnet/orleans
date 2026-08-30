#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Protocol;

[Alias("IDurableTaskObserverGrainExtension")]
public interface IDurableTaskObserver : IGrainExtension
{
    [Alias("OnResponse")]
    ValueTask OnResponseAsync(TaskId taskId, DurableTaskResponse response, CancellationToken cancellationToken = default);
}

[Alias("IDurableTaskServerGrainExtension")]
public interface IDurableTaskServer : IGrainExtension
{
    // Called by DurableTaskRequest.Invoke to ensure that a task is scheduled
    [Alias("ScheduleAsync")]
    ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken = default);

    // API used by ScheduledTask/<T> to check for a result for a task.
    // The ScheduledTask does not have access to the original request, so it cannot submit a sensible IDurableTaskRequest.
    [Alias("SubscribeOrPollAsync")]
    ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken = default);

    [Alias("CancelAsync")]
    ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken = default);
}

[GenerateSerializer, Immutable, Alias(nameof(SubscribeOrPollOptions))]
public readonly struct SubscribeOrPollOptions
{
    [Id(0)]
    public TimeSpan PollTimeout { get; init; }

}

[Alias("IDurableTaskGrainExtension")]
public interface IDurableTaskGrainExtension : IGrainExtension, IDurableTaskServer
{
    [Alias("GetTasksAsync")]
    IAsyncEnumerable<(TaskId TaskId, DurableTaskDiagnosticState State)> GetTasksAsync(CancellationToken cancellationToken = default);

    [Alias("GetRunningTasksAsync")]
    IAsyncEnumerable<TaskId> GetRunningTasksAsync(CancellationToken cancellationToken = default);
}

[GenerateSerializer]
[Alias("DurableTaskDiagnosticState")]
public struct DurableTaskDiagnosticState
{
    [Id(0)]
    public DateTimeOffset? CreatedAt { get; set; }

    [Id(1)]
    public DateTimeOffset? CompletedAt { get; set; }

    [Id(2)]
    public string Status { get; set; }

    [Id(3)]
    public string? Request { get; set; }

    [Id(4)]
    public string? Response { get; set; }

    [Id(5)]
    public List<string> Waiters { get; set; }

    public override readonly string ToString() => $"[{Status}, Created: {CreatedAt}, Completed: {CompletedAt}, Request: {Request}, Response: {Response}, Waiters: {string.Join(", ", Waiters ?? [])}]";
}

// Intermediates requests from clients / grains to DurableTasks.
// Either Orleans-backed DurableTasks or other durable tasks.
// Part of the reason for this is to allow Orleans to intercept requests and ensure that results are stored locally.
// There are two implementations:
// 1. Grain proxy: stores results locally, supports subscribing. Only available in the context of a grain.
// 2. Client proxy: does not store results locally, does not support subscribing. Available outside the context of a grain.
internal interface IDurableTaskGrainRuntime
{
    DateTimeOffset UtcNow { get; }
    ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    ValueTask<DurableTaskResponse> ScheduleRemoteAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken);
    ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, TimeSpan duration, CancellationToken cancellationToken);
    ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, CancellationToken cancellationToken);
    ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken);
    IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId);
    //IScheduledTaskHandle OnCreateScheduledTaskHandle(IScheduledTaskHandle handle);
}
