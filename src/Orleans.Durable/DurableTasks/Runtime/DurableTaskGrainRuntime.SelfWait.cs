using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Durable.Tasks.Protocol;
using Orleans.Runtime;

namespace Orleans.Durable.Tasks.Runtime;

internal sealed partial class DurableTaskGrainRuntime
{
    /// <summary>
    /// Completes once the calling code's wait for <paramref name="taskId"/>, a task of this grain, can end, and throws when
    /// its own activation, its own request, or its own exclusive section holds back what completes the task, so the wait
    /// could only end when that activation, request, or section does.
    /// </summary>
    /// <remarks>
    /// The activation decides by its own admission rules. A wait for a task which the grain doesn't hold proceeds, so its
    /// lookup reports the missing task, and a task whose terminal result is already staged completes once its journal
    /// write commits, which needs no request, so a wait for it proceeds too. The activation runs no request while it
    /// activates, so otherwise a wait from its activation code, such as <c>OnActivateAsync</c>, throws for any of its
    /// tasks. Once it has activated, only an invoked task or a local step completes by running its code as the grain's
    /// segments, and a subscription to, or a detached start of, such a task of this grain completes once that task does;
    /// inbox delivery completes the other records, such as a delay which
    /// recorded its due time, a remote call's result, or a signal's value, alongside
    /// the waiting code. A reentrant grain, a waiting method marked <c>[AlwaysInterleave]</c>, and a
    /// <c>[MayInterleave]</c> predicate which admits segments wait for such a task, unless the waiting code holds an open
    /// exclusive section, such as a transaction, which admits segments only once it closes. Code which belongs to no
    /// running request and holds no section, or whose request and section don't exclude segments, waits too. A segment of
    /// the task's own attempt which already runs, other than the calling code's, continues regardless, so the check waits
    /// for that segment to end and decides again: the segment can complete the task, or park it.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The calling code's activation, request, or section holds back what completes the task.</exception>
    internal async ValueTask EnsureWaitCanEndAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        while (GetRunningSegmentToAwait(taskId) is { } segment)
        {
            // The task can also complete without the segment, such as when an abort of it commits, which ends the wait.
            var response = GetOrAddTaskHandle(taskId).ResponseTask;
            await Task.WhenAny(segment, response).WaitAsync(cancellationToken);
            if (response.IsCompleted)
            {
                return;
            }
        }
    }

    // Returns null once nothing which the calling code holds keeps the task from completing, or the running segment of the
    // task's own attempt, which can still complete it, and throws when neither holds.
    private Task? GetRunningSegmentToAwait(TaskId taskId)
    {
        if (_grainContext?.GetComponent<IGrainRequestAdmission>() is not { } admission
            || !_storage.TryGetTask(taskId, out var state)
            || state.Result is not null)
        {
            return null;
        }

        if (admission.IsCurrentCodeActivating())
        {
            throw new InvalidOperationException(
                $"Code of grain {GrainId} waits for its durable task '{taskId}' while the grain activates, such as from OnActivateAsync. The task's code runs as requests to the same grain, which the grain runs only once it has activated, so the wait can't end. "
                + "Schedule the task without waiting for it, and wait for it from a grain method or from durable code once the grain has activated.");
        }

        var executionId = taskId;
        if (TryGetSubscription(state, out var target, out var observedId) && target == GrainId)
        {
            if (!_storage.TryGetTask(observedId, out state) || state.Result is not null)
            {
                return null;
            }

            executionId = observedId;
        }

        // Durable code of this grain which waits directly, rather than at a durable wait, holds its own segment meanwhile.
        var caller = DurableTaskContext.TryGetCurrent(out var current)
            && TryGetExecutionContext(current.Id, out var registered)
            && ReferenceEquals(registered, current)
                ? registered
                : null;

        // A delay, or an invoked task whose body is a delay, records its due time once its segment runs, and inbox delivery
        // completes it then.
        if (state.DueTime is not null
            || state.Kind is not (DurableTaskRecordKind.Invocation or DurableTaskRecordKind.LocalStep or DurableTaskRecordKind.Delay)
            || !admission.IsBlockedByCurrentRequest(
                DurableTaskSegment.Probe,
                GrainDurableTaskContext.SegmentOptions,
                holdsSuchRequest: caller?.HoldsSegmentWhileWaiting is true))
        {
            return null;
        }

        if (TryGetExecutionContext(executionId, out var attempt)
            && attempt.RunningSegment is { } segment
            && !ReferenceEquals(caller, attempt))
        {
            return segment;
        }

        throw new InvalidOperationException(
            $"Code of grain {GrainId} waits for its durable task '{taskId}', whose code runs as requests to the same grain, which the grain holds until the waiting request completes or the exclusive section which the waiting code holds, such as a transaction, closes, so the wait can't end. "
            + "Await the task from durable code, such as a method which returns DurableTask, return the task's handle instead of waiting for it, or let the waiting request interleave: mark its interface method [AlwaysInterleave] or the grain class [Reentrant], and wait outside any transaction.");
    }
}
