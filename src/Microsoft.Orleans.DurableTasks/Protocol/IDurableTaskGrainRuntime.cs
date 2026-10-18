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
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
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

    /// <summary>Gets the grain whose executions the runtime runs.</summary>
    GrainId GrainId { get; }

    /// <summary>
    /// Starts or reattaches to the child <paramref name="taskId"/>, and waits for the journal write which commits it, and,
    /// when <paramref name="waitForAcceptance"/> is set, for the acceptance of the child's host.
    /// </summary>
    ValueTask<IScheduledTaskHandle> ScheduleChildAsync(
        TaskId taskId,
        DurableTask taskDefinition,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken);

    /// <summary>Stages the start of the child <paramref name="taskId"/> on the activation, without writing.</summary>
    IScheduledTaskHandle PostChild(TaskId taskId, DurableTask taskDefinition, bool shielded);

    /// <summary>
    /// Starts <paramref name="request"/> as the detached root task <paramref name="rootId"/>, recorded and subscribed under
    /// <paramref name="recordId"/>, and waits for the journal write which commits the start, and, when
    /// <paramref name="waitForAcceptance"/> is set, for the acceptance of the task's grain.
    /// </summary>
    ValueTask<IScheduledTaskHandle> StartDetachedAsync(
        TaskId recordId,
        TaskId rootId,
        IDurableTaskRequest request,
        bool shielded,
        bool waitForAcceptance,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stages the start of the detached root task <paramref name="rootId"/>, recorded and subscribed under
    /// <paramref name="recordId"/>, on the activation, without writing.
    /// </summary>
    IScheduledTaskHandle PostDetached(TaskId recordId, TaskId rootId, IDurableTaskRequest request, bool shielded);

    /// <summary>
    /// Stages the start of the root task <paramref name="rootId"/>, from code which runs outside a durable execution, on
    /// the activation, without writing. The start commits with the grain's next journal write.
    /// </summary>
    void PostRoot(TaskId rootId, IDurableTaskRequest request);

    ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken);
    ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, string? reason, CancellationToken cancellationToken);
    ValueTask<TaskId> SelectCompletionAsync(TaskId decisionId, IReadOnlyList<TaskId> candidates, CancellationToken cancellationToken);
    IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId);

    /// <summary>
    /// Returns the identifier under which its host stores the task which the operation <paramref name="taskId"/> of an
    /// execution started or observes, and the grain which stores it when other parties address the task.
    /// </summary>
    /// <param name="taskId">The operation's identifier.</param>
    /// <param name="addressedHost">
    /// The grain which stores a detached or subscribed task, or <see langword="default"/> for a task which the
    /// execution's tree owns.
    /// </param>
    TaskId GetTargetTaskId(TaskId taskId, out GrainId addressedHost);

    /// <summary>Returns when a task's terminal result was recorded, or <see langword="null"/> when it has none.</summary>
    DateTimeOffset? GetCompletedAt(TaskId taskId);

    /// <summary>
    /// Returns the operations which earlier attempts of <paramref name="executionId"/> recorded and which a replay
    /// reaches, such as its children, markers, and recorded effects, or <see langword="null"/> when there are none.
    /// </summary>
    /// <remarks>The caller owns the returned set.</remarks>
    HashSet<TaskId>? GetRecordedOperations(TaskId executionId) => null;

    /// <summary>Notes that the attempt of <paramref name="executionId"/> which replayed its recorded operations runs live.</summary>
    void OnReplayEnded(TaskId executionId)
    {
    }

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
    /// Returns the version of <paramref name="name"/> which <paramref name="executionId"/> or its nearest enclosing
    /// execution within the same invoked task recorded, or stages a record of <paramref name="version"/> beneath
    /// <paramref name="executionId"/>, without writing, and returns it.
    /// </summary>
    int GetOrRecordVersion(TaskId executionId, string name, int version) =>
        throw new NotSupportedException("The durable task runtime does not support versions.");

    /// <summary>
    /// Cancels the attempt which <paramref name="context"/> runs for
    /// <see cref="DurableTaskAttemptCancellationReason.UnsupportedVersion"/>, keeping its task pending for a later
    /// activation to replay.
    /// </summary>
    void StallAttempt(GrainDurableTaskContext context, DurableTaskVersionException exception) =>
        throw new NotSupportedException("The durable task runtime does not support versions.");

    /// <summary>
    /// Throws when an earlier run recorded a <c>DurableTask.RunInline</c> effect under <paramref name="effectId"/>, which holds
    /// <paramref name="operationId"/>, such as a named combinator's input whose combinator has the effect's name.
    /// </summary>
    /// <param name="operationId">The operation which this run requests.</param>
    /// <param name="effectId">The operation's ancestor beneath its execution, which an effect would have recorded.</param>
    /// <param name="requested">A description of the requested operation, for the error.</param>
    /// <exception cref="InvalidOperationException">The earlier run recorded an effect under <paramref name="effectId"/>.</exception>
    void ThrowIfRecordedAsEffect(TaskId operationId, TaskId effectId, string requested)
    {
    }

    /// <summary>
    /// Notes that an attempt waits for an event which a message delivers, such as a child's result, a signal, or a delay's
    /// resume, until the matching <see cref="OnAttemptUnparked"/>.
    /// </summary>
    /// <remarks>An attempt which isn't parked is runnable, so its grain needs a wake-up to resume it after a deactivation.</remarks>
    void OnAttemptParked()
    {
    }

    /// <summary>Notes that an attempt which <see cref="OnAttemptParked"/> reported runs again.</summary>
    void OnAttemptUnparked()
    {
    }

    /// <summary>
    /// Resumes <paramref name="wait"/> when the runtime completes <paramref name="awaited"/>, a task or a signal record,
    /// until <see cref="UnregisterParkedWait"/>.
    /// </summary>
    void RegisterParkedWait(TaskId awaited, ParkedWait wait)
    {
    }

    /// <summary>Removes a registration which <see cref="RegisterParkedWait"/> added.</summary>
    void UnregisterParkedWait(TaskId awaited, ParkedWait wait)
    {
    }

    /// <summary>
    /// Returns the record of the signal <paramref name="name"/> of the invoked task whose execution tree contains
    /// <paramref name="executionId"/>, or <see langword="null"/> when the runtime doesn't record signals.
    /// </summary>
    TaskId? GetSignalRecordId(TaskId executionId, string name) => null;

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
