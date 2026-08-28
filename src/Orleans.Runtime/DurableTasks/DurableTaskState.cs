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
    public DurableTaskResponse? Result { get; set; }

    /// <summary>
    /// Gets or sets the set of clients which are interested in the result of this task.
    /// </summary>
    /// <remarks>
    /// This legacy collection stores grain observers which are awaiting the task result.
    /// During recovery, grain references are migrated to <see cref="CompletionDestinations"/> and this collection is cleared.
    /// The task remains available until its completion destinations acknowledge the result and the cleanup policy permits retirement.
    /// </remarks>
    [Id(1)]
    public HashSet<IDurableTaskObserver> LegacyObservers { get; set; } = [];

    /// <inheritdoc cref="IDurableTaskState.CompletionDestinations"/>
    [Id(6)]
    public HashSet<GrainId> CompletionDestinations { get; set; } = [];

    /// <inheritdoc cref="IDurableTaskState.Request"/>
    [Id(2)]
    public IDurableTaskRequest? Request { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CompletedAt"/>
    [Id(3)]
    public DateTimeOffset? CompletedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CreatedAt"/>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CancellationRequestedAt"/>
    [Id(5)]
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    [Id(7)]
    internal GrainId PendingCancellationDestination { get; set; }

    [Id(8)]
    internal GrainId RemoteTarget { get; set; }

    [Id(9)]
    internal bool IsCancellationTombstone { get; set; }

    /// <inheritdoc cref="IDurableTaskState.Kind"/>
    [Id(10)]
    public DurableTaskKind Kind { get; set; }

    IReadOnlySet<GrainId> IDurableTaskState.CompletionDestinations => CompletionDestinations;
    IDurableTaskRequest? IDurableTaskState.Request => Request;
    DateTimeOffset? IDurableTaskState.CompletedAt => CompletedAt;
    DateTimeOffset IDurableTaskState.CreatedAt => CreatedAt;
    GrainId IDurableTaskState.RemoteTarget => RemoteTarget;
    bool IDurableTaskState.IsCancellationTombstone
    {
        get => IsCancellationTombstone;
        set => IsCancellationTombstone = value;
    }
    GrainId IDurableTaskState.PendingCancellationDestination
    {
        get => PendingCancellationDestination;
        set => PendingCancellationDestination = value;
    }

    internal bool MigrateLegacyObservers()
    {
        var changed = false;
        foreach (var observer in LegacyObservers)
        {
            if (observer is GrainReference reference)
            {
                changed |= CompletionDestinations.Add(reference.GrainId);
            }
        }

        if (LegacyObservers.Count > 0)
        {
            LegacyObservers.Clear();
            changed = true;
        }

        return changed;
    }
}
