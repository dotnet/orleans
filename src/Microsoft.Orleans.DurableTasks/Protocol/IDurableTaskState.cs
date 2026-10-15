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
    /// The tasks of other grains, or of this grain, which must receive the result of this task.
    /// </summary>
    /// <remarks>
    /// The invoker subscribes under this task's own identifier, and a durable execution which awaits this task with
    /// <c>GetDurableTask</c> subscribes under its own child's identifier. Each subscriber of a terminal task on another
    /// grain has a completion pending in the outbox until the outbox retires it, and the task is retained until then;
    /// a subscriber on this grain receives the result in the write which stores it. Local nested tasks have no
    /// subscribers.
    /// </remarks>
    public IReadOnlySet<DurableTaskSubscriber> CompletionDestinations { get; }

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
    /// Gets whether the task's parent started it shielded, so its ancestors' durable cancellation doesn't reach it.
    /// </summary>
    /// <remarks>Its own cancellation, an abort of its tree, and deletion of its tree still reach it.</remarks>
    public bool Shielded { get; }

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
    /// Gets whether the host of the task which this record starts on another grain accepted the start,
    /// <see langword="true"/>, or rejected it, <see langword="false"/>, or <see langword="null"/> until its reply arrives.
    /// </summary>
    /// <remarks>
    /// A record of a durable call or of a detached task receives the reply which its target sends when it stores the
    /// task, or when it rejects the start, such as a start whose identifier already identifies another request. A
    /// rejection also stores the record's result, which fails with the rejection.
    /// </remarks>
    public bool? Accepted { get; }

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
