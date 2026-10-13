using System;
using System.Collections.Generic;
using Orleans.DurableTasks;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Protocol;

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
    /// Each destination has a completion pending in the outbox until the outbox retires it. A task whose completions were
    /// all delivered is pruned at once; otherwise the terminal result is retained for the configured retention period.
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

    public long ResumeGeneration { get; }

    public string? RequestFingerprint { get; }

    public DateTimeOffset? TombstonedAt { get; }

    /// <summary>
    /// Gets when the terminal result's retention restarted because its completion was dead-lettered, or
    /// <see langword="null"/> when retention starts at <see cref="CompletedAt"/>.
    /// </summary>
    public DateTimeOffset? RetainedSince { get; }

    public GrainId RemoteTarget { get; }

    public string? RemoteRequestFingerprint { get; }

    /// <summary>
    /// Gets the grain which created this task through durable messaging.
    /// </summary>
    public GrainId CallerId { get; }
}
