namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    /// <summary>
    /// Creates a durable task which returns the input index of the first input task to complete, after requesting
    /// cancellation of every other input and waiting until each is terminal.
    /// </summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>A combinator which, awaited within a durable execution, runs as part of that execution.</returns>
    /// <remarks>
    /// <para>
    /// The inputs are scheduled concurrently, and the winner is selected like
    /// <see cref="WhenAny(IReadOnlyList{DurableTask})"/>'s: the input which completed first, which the host selects again on
    /// replay. Every other input then receives a durable cancellation request carrying the reason
    /// <c>Another task won the race.</c>, which its <see cref="DurableTaskContext.TaskCancellationReason"/> reports when the
    /// request is the input's first. Cancellation is cooperative, so each other input decides its own outcome, and the
    /// combinator completes once every input is terminal: no input outlives the race.
    /// </para>
    /// <para>
    /// A winner which failed or was canceled fails the combined task with its exception. The other inputs' outcomes don't
    /// affect the combined task. Durable cancellation of the awaiting execution reaches the inputs, which are attached
    /// children. Each input runs as a child identified by its input index under the combinator's operation identifier,
    /// which also identifies a recorded selection: the segment assigned with <c>WithId</c>, or a segment generated in
    /// execution order.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    public static DurableTask<int> Race(IReadOnlyList<DurableTask> tasks)
        => new CombinatorDurableTask<int>(ScheduledTask.RaceOperation, CreateCandidates(tasks), RaceCoreAsync);

    /// <inheritdoc cref="Race(IReadOnlyList{DurableTask})"/>
    public static DurableTask<int> Race(IEnumerable<DurableTask> tasks)
        => new CombinatorDurableTask<int>(ScheduledTask.RaceOperation, CreateCandidates(tasks), RaceCoreAsync);

    /// <summary>
    /// Creates a durable task which returns the input index and result of the first input task to complete, after
    /// requesting cancellation of every other input and waiting until each is terminal.
    /// </summary>
    /// <inheritdoc cref="Race(IReadOnlyList{DurableTask})"/>
    public static DurableTask<(int Index, TResult Result)> Race<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(ScheduledTask.RaceOperation, CreateCandidates(tasks), RaceCoreAsync<TResult>);

    /// <inheritdoc cref="Race{TResult}(IReadOnlyList{DurableTask{TResult}})"/>
    public static DurableTask<(int Index, TResult Result)> Race<TResult>(IEnumerable<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(ScheduledTask.RaceOperation, CreateCandidates(tasks), RaceCoreAsync<TResult>);

    /// <summary>
    /// Creates a combinator which races <paramref name="task"/>, at input index 0, with <paramref name="delay"/>, its
    /// timeout, at input index 1, and completes with the outcome which <see cref="ScheduledTask.SettleTimeoutAsync"/>
    /// derives.
    /// </summary>
    internal static DurableTask WithTimeout(DurableTask task, DurableTask delay, string reason)
        => new CombinatorDurableTask(
            ScheduledTask.TimeoutOperation,
            [task, delay],
            async (inputs, context, operationId) =>
                (await WaitWithTimeoutAsync(inputs, context, operationId, reason).ConfigureAwait(false)).EnsureSuccessfulCompletion());

    /// <inheritdoc cref="WithTimeout(DurableTask, DurableTask, string)"/>
    internal static DurableTask<TResult> WithTimeout<TResult>(DurableTask<TResult> task, DurableTask delay, string reason)
        => new CombinatorDurableTask<TResult>(
            ScheduledTask.TimeoutOperation,
            [task, delay],
            async (inputs, context, operationId) =>
                (await WaitWithTimeoutAsync(inputs, context, operationId, reason).ConfigureAwait(false)).GetResult<TResult>());

    private static async Task<int> RaceCoreAsync(DurableTask[] inputs, DurableTaskContext context, TaskId operationId)
    {
        var (index, response) = await RaceAsync(inputs, context, operationId).ConfigureAwait(false);
        response.EnsureSuccessfulCompletion();
        return index;
    }

    private static async Task<(int Index, TResult Result)> RaceCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
    {
        var (index, response) = await RaceAsync(inputs, context, operationId).ConfigureAwait(false);
        return (index, response.GetResult<TResult>());
    }

    // Cancellation reaches the inputs, which are attached children, so the race waits for their outcomes and stops waiting
    // only when the host cancels the attempt.
    internal static async Task<(int Index, DurableTaskResponse Response)> RaceAsync(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
    {
        var cancellationToken = context.AttemptCancellationToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        var responses = await ScheduledTask.SettleRaceAsync(tasks, index, ScheduledTask.RaceLostReason, cancellationToken)
            .ConfigureAwait(false);
        return (index, responses[index]);
    }

    private static async Task<DurableTaskResponse> WaitWithTimeoutAsync(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId,
        string reason)
    {
        var cancellationToken = context.AttemptCancellationToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        return await ScheduledTask.SettleTimeoutAsync(tasks, index, reason, cancellationToken).ConfigureAwait(false);
    }
}
