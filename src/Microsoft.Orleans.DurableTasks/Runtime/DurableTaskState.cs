using System;
using System.Collections.Generic;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks;
using Orleans.Runtime;
using Orleans.Serialization;

namespace Orleans.DurableTasks.Runtime;

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
    /// A task cannot be retired until every destination has acknowledged its terminal result.
    /// Local nested tasks typically have no external completion destinations.
    /// </remarks>
    [Id(1)]
    public HashSet<IDurableTaskObserver> LegacyObservers { get; set; } = [];

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
    public DateTimeOffset? DueTime { get; set; }

    [Id(8)]
    public long ResumeGeneration { get; set; }

    [Id(9)]
    public string? RequestFingerprint { get; set; }

    [Id(10)]
    public DateTimeOffset? TombstonedAt { get; set; }

    [Id(11)]
    public GrainId RemoteTarget { get; set; }

    [Id(12)]
    public string? RemoteRequestFingerprint { get; set; }

    /// <inheritdoc cref="IDurableTaskState.CallerId"/>
    [Id(13)]
    public GrainId CallerId { get; set; }

    IReadOnlySet<GrainId> IDurableTaskState.CompletionDestinations => CompletionDestinations;
    IDurableTaskRequest? IDurableTaskState.Request => Request;
    DateTimeOffset? IDurableTaskState.CompletedAt => CompletedAt;
    DateTimeOffset IDurableTaskState.CreatedAt => CreatedAt;

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
