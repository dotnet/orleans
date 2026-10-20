using System;
using Orleans.Runtime;

namespace Orleans.DurableTasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime
{
    /// <summary>
    /// Throws when the calling code waits for <paramref name="taskId"/>, a task of this grain, from a request which the
    /// activation holds the task's segments behind, so the wait could only end when the request itself does.
    /// </summary>
    /// <remarks>
    /// The activation decides by its own admission rules, so a reentrant grain, a waiting method marked
    /// <c>[AlwaysInterleave]</c>, and a <c>[MayInterleave]</c> predicate which admits segments wait instead. Code which
    /// belongs to no running request, or whose request doesn't exclude segments, waits too.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The calling code's request holds the task's segments.</exception>
    internal void ThrowIfWaitHoldsSegments(TaskId taskId)
    {
        if (_grainContext?.GetComponent<IGrainRequestAdmission>() is not { } admission
            || !admission.IsBlockedByCurrentRequest(DurableTaskSegment.Probe, GrainDurableTaskContext.SegmentOptions))
        {
            return;
        }

        throw new InvalidOperationException(
            $"A request of grain {GrainId} waits for its durable task '{taskId}', whose code runs as requests to the same grain, which the grain holds until the waiting request completes, so the wait can't end. "
            + "Await the task from durable code, such as a method which returns DurableTask, return the task's handle instead of waiting for it, or let the waiting request interleave: mark its method [AlwaysInterleave] or [Reentrant], or the grain [Reentrant].");
    }
}
