using System;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime
{
    /// <summary>
    /// Throws when the calling code waits for <paramref name="taskId"/>, a task of this grain, while its own activation, its
    /// own request, or its own exclusive section holds the task's segments back, so the wait could only end when that
    /// activation, request, or section does.
    /// </summary>
    /// <remarks>
    /// The activation decides by its own admission rules. It runs no segment while it activates, so a wait from its
    /// activation code, such as <c>OnActivateAsync</c>, throws. Otherwise a reentrant grain, a waiting method marked
    /// <c>[AlwaysInterleave]</c>, and a <c>[MayInterleave]</c> predicate which admits segments wait instead, unless the
    /// waiting code holds an open exclusive section, such as a transaction, which admits segments only once it closes. Code
    /// which belongs to no running request and holds no section, or whose request and section don't exclude segments,
    /// waits too.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The calling code's activation, request, or section holds the task's segments.</exception>
    internal void ThrowIfWaitHoldsSegments(TaskId taskId)
    {
        if (_grainContext?.GetComponent<IGrainRequestAdmission>() is not { } admission)
        {
            return;
        }

        if (admission.IsCurrentCodeActivating())
        {
            throw new InvalidOperationException(
                $"Code of grain {GrainId} waits for its durable task '{taskId}' while the grain activates, such as from OnActivateAsync. The task's code runs as requests to the same grain, which the grain runs only once it has activated, so the wait can't end. "
                + "Schedule the task without waiting for it, and wait for it from a grain method or from durable code once the grain has activated.");
        }

        if (!admission.IsBlockedByCurrentRequest(DurableTaskSegment.Probe, GrainDurableTaskContext.SegmentOptions))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Code of grain {GrainId} waits for its durable task '{taskId}', whose code runs as requests to the same grain, which the grain holds until the waiting request completes or the exclusive section which the waiting code holds, such as a transaction, closes, so the wait can't end. "
            + "Await the task from durable code, such as a method which returns DurableTask, return the task's handle instead of waiting for it, or let the waiting request interleave: mark its interface method [AlwaysInterleave] or the grain class [Reentrant], and wait outside any transaction.");
    }
}
