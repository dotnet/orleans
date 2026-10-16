using System.Globalization;

namespace Orleans.DurableTasks;

public abstract partial class ScheduledTask
{
    internal const string RaceOperation = "race";
    internal const string TimeoutOperation = "timeout";
    internal const string RaceLostReason = "Another task won the race.";

    /// <summary>The identifier segment of a timeout's delay beneath its operation identifier: its input index in a race.</summary>
    private const string TimeoutDelaySegment = "1";

    /// <summary>
    /// Waits for the first scheduled task to complete, requests durable cancellation of every other task, waits until every
    /// task is terminal, and returns the first task.
    /// </summary>
    /// <param name="tasks">The tasks, which are captured when this method is called.</param>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the tasks.</param>
    /// <returns>
    /// The task which completed first, including a task which failed or was canceled. Every task has observed its terminal
    /// response, so <see cref="Status"/>, <see cref="Exception"/>, and
    /// <see cref="ScheduledTask{TResult}.TryGetResult(out TResult)"/> inspect the winner and the other tasks.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The winner is selected like <see cref="WhenAny(IReadOnlyList{ScheduledTask}, CancellationToken)"/>'s. Within a durable
    /// execution, the tasks are children of that execution, and the host selects the task which completed first in its
    /// durable completion order, so every replay returns the same task. A host without a durable completion order records
    /// the selection, under the decision identifier <c>$winner</c> beneath an operation identifier which this method
    /// generates in execution order when called. Outside a durable execution, the winner is the task whose wait observes its
    /// response first.
    /// </para>
    /// <para>
    /// Every other task then receives a durable cancellation request carrying the reason <c>Another task won the race.</c>,
    /// which its <see cref="DurableTaskContext.TaskCancellationReason"/> reports when the request is the task's first.
    /// Cancellation is cooperative, so each other task decides its own outcome, and the returned task completes once every
    /// task is terminal. Caller cancellation and host wait failures are propagated.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    public static async Task<ScheduledTask> Race(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await RaceIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    /// <inheritdoc cref="Race(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static async Task<ScheduledTask<TResult>> Race<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await RaceIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// Waits for successful completion, and requests durable cancellation of the task once <paramref name="timeout"/>
    /// elapses.
    /// </summary>
    /// <param name="timeout">The time to wait before requesting cancellation, which is zero or positive.</param>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the task.</param>
    /// <returns>An operation which completes once the task is terminal.</returns>
    /// <remarks>
    /// <para>
    /// Within a durable execution, the task is a child of that execution, and the timeout is a durable delay which the
    /// execution schedules when this method is first called, identified as <c>1</c> beneath an operation identifier which
    /// this method generates in execution order. A replay keeps the delay's recorded due time, so the timeout counts from the
    /// first call. The host selects whichever of the task and the delay completed first, as
    /// <see cref="WhenAny(IReadOnlyList{ScheduledTask}, CancellationToken)"/> does, so every replay observes the same
    /// outcome. Outside a durable execution, the timeout elapses on the system clock, counted from the call.
    /// </para>
    /// <para>
    /// When the task completes first, the wait cancels the delay and returns the task's outcome: its result, failure, or
    /// cancellation. When the timeout elapses first, the wait requests cancellation of the task carrying the reason
    /// <c>The operation timed out after {timeout}.</c>, which the task's <see cref="DurableTaskContext.TaskCancellationReason"/>
    /// reports when the request is the task's first, and waits until the task is terminal. A task which then ends canceled
    /// fails the wait with a <see cref="TimeoutException"/> whose inner exception is the task's
    /// <see cref="OperationCanceledException"/>, and any other outcome, such as a result which the task returns after
    /// observing the request, is the wait's outcome. A delay which ends without elapsing, for example because durable
    /// cancellation of the execution reached it, also requests cancellation of the task, and the wait then throws the
    /// delay's exception once the task is terminal.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative, including <see cref="Timeout.InfiniteTimeSpan"/>; await the task to wait
    /// without a timeout. Outside a durable execution, <paramref name="timeout"/> also exceeds the longest timer interval
    /// which <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts.
    /// </exception>
    /// <exception cref="TimeoutException">The timeout elapsed, and the task ended canceled.</exception>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await WaitWithTimeoutAsync(timeout, cancellationToken).ConfigureAwait(false)).EnsureSuccessfulCompletion();

    /// <summary>
    /// Waits for successful completion, and requests durable cancellation of the task once <paramref name="dueTime"/>
    /// passes.
    /// </summary>
    /// <param name="dueTime">The time at which to request cancellation. A time which has passed times out promptly.</param>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the task.</param>
    /// <returns>An operation which completes once the task is terminal.</returns>
    /// <remarks>
    /// The wait behaves as <see cref="WaitAsync(TimeSpan, CancellationToken)"/>, with a durable delay until
    /// <paramref name="dueTime"/>, measured using host logical time, and the cancellation reason
    /// <c>The operation timed out because its deadline {dueTime} passed.</c>, which formats <paramref name="dueTime"/> with the
    /// round-trip format, <c>O</c>. Outside a durable execution, <paramref name="dueTime"/> is compared with the system clock.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Outside a durable execution, the time until <paramref name="dueTime"/> exceeds the longest timer interval which
    /// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts.
    /// </exception>
    /// <exception cref="TimeoutException">The due time passed, and the task ended canceled.</exception>
    public async Task WaitUntilAsync(DateTimeOffset dueTime, CancellationToken cancellationToken = default)
        => (await WaitWithDeadlineAsync(dueTime, cancellationToken).ConfigureAwait(false)).EnsureSuccessfulCompletion();

    /// <summary>
    /// Waits for the task's terminal response, requesting its cancellation once <paramref name="timeout"/> elapses, and
    /// returns the outcome which <see cref="WaitAsync(TimeSpan, CancellationToken)"/> describes.
    /// </summary>
    internal Task<DurableTaskResponse> WaitWithTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        var reason = GetTimeoutReason(timeout);
        return DurableTaskContext.Ambient is { } context
            ? WaitWithDurableTimeoutAsync(context, context.CreateOperationId(TimeoutOperation), DurableTask.Delay(timeout), reason, cancellationToken)
            : WaitWithSystemTimeoutAsync(timeout, reason, cancellationToken);
    }

    /// <summary>
    /// Waits for the task's terminal response, requesting its cancellation once <paramref name="dueTime"/> passes, and
    /// returns the outcome which <see cref="WaitUntilAsync(DateTimeOffset, CancellationToken)"/> describes.
    /// </summary>
    internal Task<DurableTaskResponse> WaitWithDeadlineAsync(DateTimeOffset dueTime, CancellationToken cancellationToken)
    {
        var reason = GetDeadlineReason(dueTime);
        if (DurableTaskContext.Ambient is { } context)
        {
            return WaitWithDurableTimeoutAsync(context, context.CreateOperationId(TimeoutOperation), DurableTask.DelayUntil(dueTime), reason, cancellationToken);
        }

        var remaining = dueTime - TimeProvider.System.GetUtcNow();
        return WaitWithSystemTimeoutAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, reason, cancellationToken);
    }

    internal static string GetTimeoutReason(TimeSpan timeout)
        => string.Create(CultureInfo.InvariantCulture, $"The operation timed out after {timeout}.");

    internal static string GetDeadlineReason(DateTimeOffset dueTime)
        => string.Create(CultureInfo.InvariantCulture, $"The operation timed out because its deadline {dueTime:O} passed.");

    /// <summary>
    /// Races this task, a child of <paramref name="context"/>, with <paramref name="delay"/>, which the execution schedules as
    /// the timeout's delay beneath <paramref name="operationId"/>.
    /// </summary>
    private async Task<DurableTaskResponse> WaitWithDurableTimeoutAsync(
        DurableTaskContext context,
        TaskId operationId,
        DurableTask delay,
        string reason,
        CancellationToken cancellationToken)
    {
        using var linked = CreateLinkedWaitCancellation(context, cancellationToken);
        var waitToken = linked?.Token ?? context.AttemptCancellationToken;
        try
        {
            var delayHandle = await DurableTask.ScheduleChildAsync(delay, context, operationId.Child(TimeoutDelaySegment), waitToken)
                .ConfigureAwait(false);
            ScheduledTask[] tasks = [this, new ScheduledTaskHandle(delayHandle)];
            var index = await SelectIndexAsync(context, operationId, tasks, waitToken).ConfigureAwait(false);
            return await SettleTimeoutAsync(tasks, index, reason, waitToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private async Task<DurableTaskResponse> WaitWithSystemTimeoutAsync(TimeSpan timeout, string reason, CancellationToken cancellationToken)
    {
        using (var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeoutCancellation.CancelAfter(timeout);
            try
            {
                return await ObserveTerminalAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                // The timeout elapsed, so the task is canceled below.
            }
        }

        await CancelAsync(reason, cancellationToken).ConfigureAwait(false);
        return ToTimeoutOutcome(await ObserveTerminalAsync(cancellationToken).ConfigureAwait(false), reason);
    }

    /// <summary>
    /// Settles a race between a task, at index 0, and its timeout's delay, at index 1, whose winner is at
    /// <paramref name="winnerIndex"/>, and returns the outcome which <see cref="WaitAsync(TimeSpan, CancellationToken)"/>
    /// describes.
    /// </summary>
    internal static async Task<DurableTaskResponse> SettleTimeoutAsync(
        ScheduledTask[] tasks,
        int winnerIndex,
        string reason,
        CancellationToken cancellationToken)
    {
        var elapsed = winnerIndex == 1
            && (await tasks[1].ObserveTerminalAsync(cancellationToken).ConfigureAwait(false)).Status is DurableTaskStatus.CompletedSuccessfully;
        var responses = await SettleRaceAsync(tasks, winnerIndex, elapsed ? reason : null, cancellationToken).ConfigureAwait(false);
        return elapsed ? ToTimeoutOutcome(responses[0], reason) : responses[winnerIndex];
    }

    /// <summary>
    /// Requests cancellation of every task other than the winner at <paramref name="winnerIndex"/>, carrying
    /// <paramref name="reason"/>, then waits until every task is terminal and returns their responses in input order.
    /// </summary>
    /// <remarks>
    /// Every request is made before any wait, so the other tasks stop concurrently. A request which fails synchronously
    /// fails only its own operation, so the other requests are still made.
    /// </remarks>
    internal static async Task<DurableTaskResponse[]> SettleRaceAsync(
        ScheduledTask[] tasks,
        int winnerIndex,
        string? reason,
        CancellationToken cancellationToken)
    {
        var cancellations = new Task[tasks.Length - 1];
        for (int index = 0, next = 0; index < tasks.Length; index++)
        {
            if (index != winnerIndex)
            {
                cancellations[next++] = CancelLoserAsync(tasks[index], reason, cancellationToken);
            }
        }

        await Task.WhenAll(cancellations).ConfigureAwait(false);
        var responses = new DurableTaskResponse[tasks.Length];
        for (var index = 0; index < responses.Length; index++)
        {
            responses[index] = await tasks[index].ObserveTerminalAsync(cancellationToken).ConfigureAwait(false);
        }

        return responses;

        static async Task CancelLoserAsync(ScheduledTask task, string? reason, CancellationToken cancellationToken)
            => await task.CancelAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the outcome of a wait whose timeout elapsed: a <see cref="TimeoutException"/> for a task which ended canceled,
    /// and otherwise the task's <paramref name="response"/>.
    /// </summary>
    private static DurableTaskResponse ToTimeoutOutcome(DurableTaskResponse response, string reason)
        => response is CanceledDurableTaskResponse { Exception: var canceled }
            ? DurableTaskResponse.FromException(new TimeoutException(reason, canceled))
            : response;

    /// <summary>
    /// Races <paramref name="tasks"/> through the current execution's host, or by observing the waits outside a durable
    /// execution, and returns the winner's index once every task is terminal.
    /// </summary>
    /// <remarks>
    /// The operation identifier is generated before the first await, so calls which begin in deterministic order receive
    /// the same identifiers on replay.
    /// </remarks>
    private static Task<int> RaceIndexAsync(ScheduledTask[] tasks, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Length, nameof(tasks));
        var context = DurableTaskContext.Ambient;
        return RaceIndexAsync(context, context?.CreateOperationId(RaceOperation) ?? default, tasks, cancellationToken);
    }

    private static async Task<int> RaceIndexAsync(
        DurableTaskContext? context,
        TaskId operationId,
        ScheduledTask[] tasks,
        CancellationToken cancellationToken)
    {
        using var linked = context is null ? null : CreateLinkedWaitCancellation(context, cancellationToken);
        var waitToken = context is null ? cancellationToken : linked?.Token ?? context.AttemptCancellationToken;
        try
        {
            var index = context is null
                ? await WaitForAnyIndexAsync(tasks, waitToken).ConfigureAwait(false)
                : await SelectIndexAsync(context, operationId, tasks, waitToken).ConfigureAwait(false);
            _ = await SettleRaceAsync(tasks, index, RaceLostReason, waitToken).ConfigureAwait(false);
            return index;
        }
        catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}

public abstract partial class ScheduledTask<TResult>
{
    /// <summary>
    /// Waits for and returns the successful result, and requests durable cancellation of the task once
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <returns>The task's result, including a result which it returns after observing the timeout's cancellation request.</returns>
    /// <inheritdoc cref="ScheduledTask.WaitAsync(TimeSpan, CancellationToken)"/>
    public new async Task<TResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await WaitWithTimeoutAsync(timeout, cancellationToken).ConfigureAwait(false)).GetResult<TResult>();

    /// <summary>
    /// Waits for and returns the successful result, and requests durable cancellation of the task once
    /// <paramref name="dueTime"/> passes.
    /// </summary>
    /// <returns>The task's result, including a result which it returns after observing the deadline's cancellation request.</returns>
    /// <inheritdoc cref="ScheduledTask.WaitUntilAsync(DateTimeOffset, CancellationToken)"/>
    public new async Task<TResult> WaitUntilAsync(DateTimeOffset dueTime, CancellationToken cancellationToken = default)
        => (await WaitWithDeadlineAsync(dueTime, cancellationToken).ConfigureAwait(false)).GetResult<TResult>();
}
