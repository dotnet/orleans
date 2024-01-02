using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;

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
    public DurableTaskResponse Result { get; }

    /// <summary>
    /// The set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This task cannot be retired until all clients have acknowledged the task's result.
    /// If the task has a parent task (determined using the task's hierarchical identifier), then the result will not be retired until that
    /// In the case of nested tasks (eg, defined by local methods), there will typically be no clients.
    /// In that case, the result will not be 
    /// </remarks>
    public IReadOnlySet<IDurableTaskObserver> Observers { get; }

    /// <summary>
    /// The invokable request.
    /// </summary>
    public IDurableTaskRequest Request { get; }

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
}

