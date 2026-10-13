using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
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
    // A poll waits for the result without changing state, so it interleaves: the grain keeps processing the requests and
    // messages which complete the task.
    [Alias("SubscribeOrPollAsync"), AlwaysInterleave]
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

    /// <summary>
    /// Gets or sets the diagnostic status, or <see langword="null"/> when the value was default-initialized
    /// or received from a version which did not provide this member.
    /// </summary>
    /// <remarks>
    /// A task with a stored result is <c>Completed</c>, <c>Faulted</c>, <c>Canceled</c>, or <c>Unreadable</c> when the result
    /// no longer decodes. A task without one is <c>Canceling</c>, <c>Delayed</c>, or <c>Pending</c>.
    /// </remarks>
    [Id(2)]
    public string? Status { get; set; }

    /// <summary>Gets or sets the local request, formatted as a method call.</summary>
    [Id(3)]
    public string? Request { get; set; }

    /// <summary>Gets or sets a summary of the stored result: its value, its exception, or its decoding failure.</summary>
    [Id(4)]
    public string? Response { get; set; }

    /// <summary>
    /// Gets or sets the diagnostic completion waiters, or <see langword="null"/> when the value was
    /// default-initialized or received from a version which did not provide this member.
    /// </summary>
    [Id(5)]
    public List<string>? Waiters { get; set; }

    /// <summary>Gets or sets the due time of a pending delay.</summary>
    [Id(6)]
    public DateTimeOffset? DueTime { get; set; }

    /// <summary>Gets or sets the grain which executes a remote child task.</summary>
    [Id(7)]
    public string? RemoteTarget { get; set; }

    public override readonly string ToString() => $"[{Status}, Created: {CreatedAt}, Completed: {CompletedAt}, Due: {DueTime}, Request: {Request}, Remote: {RemoteTarget}, Response: {Response}, Waiters: {string.Join(", ", Waiters ?? [])}]";
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

    /// <summary>
    /// Starts <paramref name="request"/> as a detached root task under <paramref name="rootId"/>, recording the start under
    /// <paramref name="markerId"/> when it is not default, and waits for the journal write which commits both.
    /// </summary>
    ValueTask DetachAsync(TaskId rootId, IDurableTaskRequest request, TaskId markerId, CancellationToken cancellationToken);
    ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken);
    ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, CancellationToken cancellationToken);
    ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken);
    IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId);

    /// <summary>Returns when a task's terminal result was recorded, or <see langword="null"/> when it has none.</summary>
    DateTimeOffset? GetCompletedAt(TaskId taskId);
    //IScheduledTaskHandle OnCreateScheduledTaskHandle(IScheduledTaskHandle handle);
}
