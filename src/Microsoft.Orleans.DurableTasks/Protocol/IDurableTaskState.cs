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
    /// all delivered is deleted with its execution tree at once; otherwise the terminal result is retained for the
    /// configured retention period. Local nested tasks have no completion destinations.
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
    /// The reason which the task's cancellation request carries, or <see langword="null"/> when cancellation hasn't been
    /// requested or its request carries no reason.
    /// </summary>
    /// <remarks>The first cancellation request determines the reason.</remarks>
    public string? CancellationReason { get; }

    /// <summary>
    /// Gets whether the task's parent scheduled it in a cancellation suppression scope, so its ancestors' durable
    /// cancellation doesn't reach it.
    /// </summary>
    /// <remarks>Its own cancellation, an abort of its tree, and deletion of its tree still reach it.</remarks>
    public bool CancellationSuppressed { get; }

    /// <summary>
    /// The time at which the task was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? DueTime { get; }

    public long ResumeGeneration { get; }

    public string? RequestFingerprint { get; }

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

    /// <summary>
    /// Gets the grain-wide completion sequence which stamped the task's terminal result when it was staged, or zero when
    /// the task has no result or its storage doesn't order completions.
    /// </summary>
    /// <remarks>
    /// Storage stamps each stored result with a sequence higher than every stored task's, in the same journal write as the
    /// result, so results staged later have higher sequences. A combinator which selects the earliest completion, such as
    /// <c>WhenAny</c> or <c>WhenEach</c>, selects the completed candidate with the lowest sequence, which is the same in
    /// every replay. Candidates without a sequence are selected by a recorded decision.
    /// </remarks>
    public long CompletionSequence { get; }
}
