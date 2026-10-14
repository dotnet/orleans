using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.DurableTasks;
using Orleans.DurableTasks.Runtime;
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

    /// <summary>Durably requests cancellation of a task of this grain, carrying <paramref name="reason"/>.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.CancellationReason"/> reports, or
    /// <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <remarks>The first cancellation request determines the reason.</remarks>
    [Alias("CancelWithReasonAsync")]
    ValueTask CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administratively aborts a task of this grain: commits the task and its execution tree as failed with
    /// <see cref="DurableTaskAbortedException"/> in one write, and stops their running attempts without running further
    /// task code.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    /// <remarks>A task which is terminal, or which this grain doesn't retain, is unchanged.</remarks>
    [Alias("AbortAsync")]
    ValueTask AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken = default);
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

    /// <summary>Delivers a signal to the invoked task <paramref name="taskId"/> of this grain.</summary>
    /// <param name="taskId">The invoked task's identifier.</param>
    /// <param name="name">The signal name.</param>
    /// <param name="value">A successful response which holds the signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>An operation which completes once the task durably holds the signal's value.</returns>
    /// <exception cref="DurableTaskNotFoundException">The grain retains no invoked task <paramref name="taskId"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The task is terminal and has no value for the signal, or it holds a different value for the signal.
    /// </exception>
    [Alias("SignalAsync")]
    ValueTask SignalAsync(TaskId taskId, string name, DurableTaskResponse value, CancellationToken cancellationToken = default);

    /// <summary>Delivers a signal to the invoked task <paramref name="taskId"/> of this grain, and returns how the task handled it.</summary>
    /// <param name="taskId">The invoked task's identifier.</param>
    /// <param name="name">The signal name.</param>
    /// <param name="value">A successful response which holds the signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>
    /// <see cref="DurableSignalResult.Accepted"/> or <see cref="DurableSignalResult.Duplicate"/> once the task durably
    /// holds the value, or the reason the task rejected it, which changes nothing.
    /// </returns>
    [Alias("TrySignalAsync")]
    ValueTask<DurableSignalResult> TrySignalAsync(TaskId taskId, string name, DurableTaskResponse value, CancellationToken cancellationToken = default);
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

    /// <summary>
    /// Gets or sets a summary of the stored result: its value, its exception, or its decoding failure. A
    /// <c>Canceling</c> or <c>Canceled</c> task reports its cancellation reason, if any.
    /// </summary>
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
    ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask taskDefinition, bool suppressCancellation, CancellationToken cancellationToken);
    ValueTask<DurableTaskResponse> ScheduleRemoteAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Starts <paramref name="request"/> as a detached root task under <paramref name="rootId"/>, recording the start under
    /// <paramref name="markerId"/> when it is not default, and waits for the journal write which commits both.
    /// </summary>
    ValueTask DetachAsync(TaskId rootId, IDurableTaskRequest request, TaskId markerId, CancellationToken cancellationToken);
    ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken);
    ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, string? reason, CancellationToken cancellationToken);
    ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken);
    IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId);

    /// <summary>Returns when a task's terminal result was recorded, or <see langword="null"/> when it has none.</summary>
    DateTimeOffset? GetCompletedAt(TaskId taskId);

    /// <summary>
    /// Stages the terminal response of the body which <paramref name="context"/> runs, from the work item which ran the
    /// body's final code on the activation, without writing.
    /// </summary>
    /// <remarks>
    /// The response then commits with the journaled state which the body's final code changed. The attempt still commits
    /// the response once the body's run returns it, which leaves an already staged response unchanged. A runtime which
    /// doesn't stage responses early keeps this default implementation, which does nothing.
    /// </remarks>
    void StageBodyResponse(GrainDurableTaskContext context, DurableTaskResponse response)
    {
    }

    /// <summary>
    /// Returns the outcome which <paramref name="effectId"/> records, or runs <paramref name="effect"/> and stages a record
    /// of its outcome, without writing.
    /// </summary>
    DurableTaskResponse RunEffect<TState>(TaskId effectId, TState state, Func<TState, DurableTaskResponse> effect) =>
        throw new NotSupportedException("The durable task runtime does not support recorded effects.");

    /// <summary>
    /// Throws when <paramref name="operationId"/> isn't recorded and an earlier run recorded <paramref name="otherKindId"/>,
    /// the identifier which the other kind of named operation receives for the same occurrence of the same name.
    /// </summary>
    /// <param name="operationId">The operation which this run requests.</param>
    /// <param name="otherKindId">The identifier of the other kind of operation for the same name and occurrence.</param>
    /// <param name="requested">A description of the requested operation, for the error.</param>
    /// <exception cref="InvalidOperationException">The earlier run recorded the other kind of operation.</exception>
    void ThrowIfRecordedAsOtherKind(TaskId operationId, TaskId otherKindId, string requested)
    {
    }

    /// <summary>
    /// Waits for the signal <paramref name="name"/> of the invoked task whose execution tree contains
    /// <paramref name="executionId"/>, and returns its committed value and the time it was recorded.
    /// </summary>
    ValueTask<(DurableTaskResponse Value, DateTimeOffset RecordedAt)> WaitForSignalAsync(
        TaskId executionId,
        string name,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The durable task runtime does not support signals.");

    /// <summary>
    /// Sends a signal to the invoked task <paramref name="taskId"/> on <paramref name="target"/>, recording the send under
    /// <paramref name="markerId"/>, and waits for the journal write which commits both.
    /// </summary>
    /// <param name="throwOnRejection">Whether a rejection by a task of this grain throws rather than being recorded and returned.</param>
    ValueTask<DurableSignalResult> SendSignalAsync(
        TaskId markerId,
        GrainId target,
        TaskId taskId,
        string name,
        DurableTaskResponse value,
        bool throwOnRejection,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The durable task runtime does not support signals.");
    //IScheduledTaskHandle OnCreateScheduledTaskHandle(IScheduledTaskHandle handle);
}
