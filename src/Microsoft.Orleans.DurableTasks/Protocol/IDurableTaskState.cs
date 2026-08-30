using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Protocol;

/*
 * Grain activates
 * Grain enumerates stored pending tasks and re-invokes any which are not completed.
 *   * Some tasks will not be directly invokable since they represent local methods on a grain (not remote requests to the grain)
     * Those tasks do not need to be invoked.
 */

public interface IDurableTaskState
{
    /// <summary>
    /// The result of the task, which will be <see langword="null"/> if the task has not yet completed.
    /// </summary>
    public DurableTaskResponse? Result { get; }

    /// <summary>
    /// The set of grains which must receive the result of this task.
    /// </summary>
    /// <remarks>
    /// A task cannot be retired until every destination has acknowledged its terminal result.
    /// Local nested tasks typically have no external completion destinations.
    /// </remarks>
    public IReadOnlySet<GrainId> CompletionDestinations { get; }

    /// <summary>
    /// The invokable request.
    /// </summary>
    public IDurableTaskRequest? Request { get; }

    /// <summary>
    /// The time at which the task completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// The time at which cancellation was requested.
    /// </summary>
    public DateTimeOffset? CancellationRequestedAt { get; }

    /// <summary>
    /// The time at which the task was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? DueTime { get; }

    TimeSpan? DelayDuration { get; }

    public long ResumeGeneration { get; }

    public string? RequestFingerprint { get; }

    public DateTimeOffset? TombstonedAt { get; }

    public GrainId RemoteTarget { get; }

    public string? RemoteRequestFingerprint { get; }

    /// <summary>
    /// Gets the grain which created this task through durable messaging.
    /// </summary>
    public GrainId CallerId { get; }
}
