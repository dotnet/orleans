using System;
using System.Collections.Generic;
using System.Distributed.DurableTasks;
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

[GenerateSerializer, SuppressReferenceTracking]
[Alias("DurableTaskState")]
public class DurableTaskState : IDurableTaskState
{
    /// <inheritdoc cref="IDurableTaskState.Result"/>
    [Id(0)]
    public DurableTaskResponse Result { get; set; }

    /// <summary>
    /// Gets or sets the set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This task cannot be retired until all clients have acknowledged the task's result.
    /// If the task has a parent task (determined using the task's hierarchical identifier), then the result will not be retired until that
    /// In the case of nested tasks (eg, defined by local methods), there will typically be no clients.
    /// In that case, the result will not be 
    /// </remarks>
    [Id(1)]
    public HashSet<IDurableTaskObserver> Observers { get; set; }

    /// <inheritdoc cref="IDurableTaskState.Request"/>
    [Id(2)]
    public IDurableTaskRequest Request { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CompletedAt"/>
    [Id(3)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CreatedAt"/>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CancellationRequestedAt"/>
    [Id(5)] 
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    IReadOnlySet<IDurableTaskObserver> IDurableTaskState.Observers => Observers;
    IDurableTaskRequest IDurableTaskState.Request => Request;
    DateTimeOffset? IDurableTaskState.CompletedAt => CompletedAt;
    DateTimeOffset IDurableTaskState.CreatedAt => CreatedAt;
}

