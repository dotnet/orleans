using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

/// <summary>
/// Observes a runtime which replays recorded histories without advancing them, for the replay host of
/// <c>Microsoft.Orleans.DurableTasks.Testing</c>.
/// </summary>
/// <remarks>
/// <para>
/// Registering an observer puts every durable task runtime of the silo into replay mode, which only the replay host's
/// dedicated silo uses:
/// </para>
/// <list type="bullet">
/// <item><description>
/// An attempt which starts an operation that no earlier attempt recorded, such as a child, an effect, a delay, a detached
/// start, or a signal's send, has reached the end of its recorded history: the runtime reports the operation and cancels
/// the attempt before staging it, so no new work runs.
/// </description></item>
/// <item><description>
/// A body's outcome is reported and discarded instead of staged or committed.
/// </description></item>
/// </list>
/// <para>
/// Decisions which a replay derives from recorded outcomes, such as version decisions and completion selections, are
/// recorded as in a live run. The host keeps messages from advancing the history: it holds the silo's outgoing
/// messages, so no reply arrives, and its Durable Jobs clock doesn't advance, so no scheduled resume becomes due.
/// Callbacks run on the activation which raises them, and observers return promptly without calling back into the
/// runtime.
/// </para>
/// </remarks>
internal interface IDurableTaskReplayObserver
{
    /// <summary>Notes that a runtime recovered its grain's tasks and started the executions which were pending.</summary>
    void OnRecovered(DurableTaskGrainRuntime runtime);

    /// <summary>Notes that an attempt of <paramref name="executionId"/> started with the operations earlier attempts recorded.</summary>
    void OnAttemptStarted(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> recorded);

    /// <summary>Notes that an attempt reached the recorded operation <paramref name="operationId"/>.</summary>
    void OnOperationReached(GrainId grainId, TaskId executionId, TaskId operationId);

    /// <summary>
    /// Notes that an attempt started <paramref name="operationId"/>, which no earlier attempt recorded, and was canceled
    /// before staging it.
    /// </summary>
    void OnNewOperation(GrainId grainId, TaskId executionId, TaskId operationId, string description);

    /// <summary>Notes that an attempt started waiting for <paramref name="awaited"/>, which a message completes.</summary>
    void OnWaitStarted(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> awaited);

    /// <summary>Notes that a wait which <see cref="OnWaitStarted"/> reported ended.</summary>
    void OnWaitEnded(GrainId grainId, TaskId executionId, IReadOnlyCollection<TaskId> awaited);

    /// <summary>
    /// Notes the version of <paramref name="name"/> which an execution follows, and whether an earlier attempt recorded
    /// it.
    /// </summary>
    void OnVersion(GrainId grainId, TaskId executionId, string name, int version, bool recorded);

    /// <summary>
    /// Notes that an attempt ended with <paramref name="response"/>, which the runtime discarded, or with the runtime's
    /// <paramref name="cancellation"/> of the attempt.
    /// </summary>
    void OnAttemptEnded(
        GrainId grainId,
        TaskId executionId,
        DurableTaskResponse response,
        DurableTaskAttemptCanceledException? cancellation);
}
