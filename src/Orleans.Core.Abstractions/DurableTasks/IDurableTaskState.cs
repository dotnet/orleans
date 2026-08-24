using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

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
    /// Each entry identifies a grain awaiting the task result. The runtime removes entries as destinations acknowledge completion.
    /// The task remains available while this set is non-empty and is retired only when the configured cleanup policy permits it.
    /// Nested local tasks typically have no grain completion destinations because their continuations execute within the parent task.
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

    /// <summary>
    /// Gets the remote grain which owns this task, or the default grain id for local tasks.
    /// </summary>
    public GrainId RemoteTarget { get; }

    /// <summary>
    /// Gets a value indicating whether this state records cancellation received before its invocation request.
    /// </summary>
    public bool IsCancellationTombstone { get; set; }

    /// <summary>
    /// Gets or sets the remote destination which must acknowledge a persisted cancellation request.
    /// </summary>
    public GrainId PendingCancellationDestination { get; set; }
}
