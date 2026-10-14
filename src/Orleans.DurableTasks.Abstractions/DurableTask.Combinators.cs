using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    internal const string WhenAnyOperation = "when-any";
    private const string WhenAllOperation = "when-all";

    /// <summary>Creates a durable task which completes once every input task has completed.</summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>A combinator which, awaited within a durable execution, runs as part of that execution.</returns>
    /// <remarks>
    /// <para>
    /// Like <see cref="Task.WhenAll(IEnumerable{Task})"/>, the combined task completes only after every input is terminal,
    /// including inputs which durable cancellation reaches. Awaiting it then throws the first failure in input order when
    /// any input failed; otherwise it throws the first input's <see cref="OperationCanceledException"/> in input order when
    /// any input was canceled; otherwise it completes successfully. Input order selects the exception, so a replay observes
    /// the same one.
    /// </para>
    /// <para>
    /// Each input runs as a child identified by its input index under the combinator's operation identifier: the segment
    /// assigned with <c>WithId</c>, or a segment generated in execution order.
    /// </para>
    /// </remarks>
    public static DurableTask WhenAll(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask(WhenAllOperation, tasks.ToArray(), WhenAllCoreAsync);
    }

    /// <inheritdoc cref="WhenAll(IReadOnlyList{DurableTask})"/>
    public static DurableTask WhenAll(IEnumerable<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask(WhenAllOperation, tasks.ToArray(), WhenAllCoreAsync);
    }

    /// <summary>Creates a durable task which returns the results of every input task, in input order.</summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>
    /// A combinator which, awaited within a durable execution, runs as part of that execution and returns an array with
    /// each input's result at its input index.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Like <see cref="Task.WhenAll{TResult}(IEnumerable{Task{TResult}})"/>, the combined task completes only after every
    /// input is terminal, including inputs which durable cancellation reaches. Awaiting it then throws the first failure in
    /// input order when any input failed; otherwise it throws the first input's <see cref="OperationCanceledException"/> in
    /// input order when any input was canceled; otherwise it returns the results. Input order selects the exception, so a
    /// replay observes the same one.
    /// </para>
    /// <para>
    /// Each input runs as a child identified by its input index under the combinator's operation identifier: the segment
    /// assigned with <c>WithId</c>, or a segment generated in execution order.
    /// </para>
    /// </remarks>
    public static DurableTask<TResult[]> WhenAll<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<TResult[]>(WhenAllOperation, tasks.ToArray<DurableTask>(), WhenAllCoreAsync<TResult>);
    }

    /// <inheritdoc cref="WhenAll{TResult}(IReadOnlyList{DurableTask{TResult}})"/>
    public static DurableTask<TResult[]> WhenAll<TResult>(IEnumerable<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<TResult[]>(WhenAllOperation, tasks.ToArray<DurableTask>(), WhenAllCoreAsync<TResult>);
    }

    /// <summary>Creates a durable task which returns the input index of the first input task to complete.</summary>
    /// <param name="tasks">The input tasks.</param>
    /// <returns>A combinator which, awaited within a durable execution, runs as part of that execution.</returns>
    /// <remarks>
    /// The host records the winner, so replay selects the same input. A winner which failed or was canceled fails the
    /// combined task. The other inputs keep running. Each input runs as a child identified by its input index under the
    /// combinator's operation identifier, which also identifies the recorded winner: the segment assigned with
    /// <c>WithId</c>, or a segment generated in execution order.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    public static DurableTask<int> WhenAny(IReadOnlyList<DurableTask> tasks)
        => new CombinatorDurableTask<int>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync);

    /// <inheritdoc cref="WhenAny(IReadOnlyList{DurableTask})"/>
    public static DurableTask<int> WhenAny(IEnumerable<DurableTask> tasks)
        => new CombinatorDurableTask<int>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync);

    /// <summary>Creates a durable task which returns the input index and result of the first input task to complete.</summary>
    /// <param name="tasks">The input tasks.</param>
    /// <returns>A combinator which, awaited within a durable execution, runs as part of that execution.</returns>
    /// <remarks>
    /// The host records the winner, so replay selects the same input. A winner which failed or was canceled fails the
    /// combined task. The other inputs keep running. Each input runs as a child identified by its input index under the
    /// combinator's operation identifier, which also identifies the recorded winner: the segment assigned with
    /// <c>WithId</c>, or a segment generated in execution order.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync<TResult>);

    /// <inheritdoc cref="WhenAny{TResult}(IReadOnlyList{DurableTask{TResult}})"/>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(IEnumerable<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync<TResult>);

    // Cancellation reaches the inputs, which are attached children, so each combinator waits for their outcomes and stops
    // waiting only when the host aborts the attempt.
    private static async Task WhenAllCoreAsync(DurableTask[] inputs, DurableExecutionContext context, TaskId operationId)
        => await WaitForAllAsync(inputs, context, operationId).ConfigureAwait(false);

    private static async Task<TResult[]> WhenAllCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableExecutionContext context,
        TaskId operationId)
    {
        var responses = await WaitForAllAsync(inputs, context, operationId).ConfigureAwait(false);
        var results = new TResult[responses.Length];
        for (var index = 0; index < results.Length; index++)
        {
            results[index] = responses[index].GetResult<TResult>();
        }

        return results;
    }

    /// <summary>
    /// Waits for every input's terminal response, then throws the first failure in input order or, when no input failed,
    /// the first cancellation in input order.
    /// </summary>
    /// <remarks>Input order, rather than completion order, selects the exception, so a replay observes the same one.</remarks>
    private static async Task<DurableTaskResponse[]> WaitForAllAsync(
        DurableTask[] inputs,
        DurableExecutionContext context,
        TaskId operationId)
    {
        var cancellationToken = context.ExecutionAbortToken;
        var tasks = await ScheduleInputsAsync(inputs, operationId, cancellationToken).ConfigureAwait(false);
        var responses = new DurableTaskResponse[tasks.Length];
        DurableTaskResponse? canceled = null;
        for (var index = 0; index < responses.Length; index++)
        {
            var response = responses[index] = await tasks[index].WaitAsyncCore(cancellationToken).ConfigureAwait(false);
            if (response.Status is DurableTaskStatus.Canceled)
            {
                canceled ??= response;
            }
        }

        foreach (var response in responses)
        {
            if (response.Status is DurableTaskStatus.Failed)
            {
                response.EnsureSuccessfulCompletion();
            }
        }

        canceled?.EnsureSuccessfulCompletion();
        return responses;
    }

    private static async Task<int> WhenAnyCoreAsync(DurableTask[] inputs, DurableExecutionContext context, TaskId operationId)
    {
        var cancellationToken = context.ExecutionAbortToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        await tasks[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return index;
    }

    private static async Task<(int Index, TResult Result)> WhenAnyCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableExecutionContext context,
        TaskId operationId)
    {
        var cancellationToken = context.ExecutionAbortToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        return (index, (await tasks[index].WaitAsyncCore(cancellationToken).ConfigureAwait(false)).GetResult<TResult>());
    }

    /// <summary>
    /// Schedules <paramref name="inputs"/> and returns them with the index of the winner, which the host records under
    /// <paramref name="operationId"/>.
    /// </summary>
    internal static async Task<(ScheduledTask[] Tasks, int Index)> SelectAsync(
        DurableTask[] inputs,
        DurableExecutionContext context,
        TaskId operationId,
        CancellationToken cancellationToken)
    {
        var tasks = await ScheduleInputsAsync(inputs, operationId, cancellationToken).ConfigureAwait(false);
        var candidates = new TaskId[tasks.Length];
        for (var index = 0; index < candidates.Length; index++)
        {
            candidates[index] = tasks[index].Id;
        }

        var winner = await context.SelectCompletionAsync(operationId.Child("$winner"), candidates, cancellationToken).ConfigureAwait(false);
        var winnerIndex = Array.IndexOf(candidates, winner);
        if (winnerIndex < 0)
        {
            throw new InvalidOperationException($"The host selected '{winner}', which is not a candidate of '{operationId}'.");
        }

        return (tasks, winnerIndex);
    }

    private static Task<ScheduledTask[]> ScheduleInputsAsync(
        DurableTask[] inputs,
        TaskId operationId,
        CancellationToken cancellationToken)
    {
        // Inputs are scheduled concurrently, so a host can commit the whole batch together. Their indexed identifiers keep
        // them replay-stable in any scheduling order.
        var scheduling = new Task<ScheduledTask>[inputs.Length];
        for (var index = 0; index < inputs.Length; index++)
        {
            scheduling[index] = new ConfiguredDurableTask(inputs[index], operationId.Child(index.ToString(CultureInfo.InvariantCulture)))
                .ScheduleAsync(cancellationToken);
        }

        return Task.WhenAll(scheduling);
    }

    private static DurableTask[] CreateCandidates<TTask>(IEnumerable<TTask> tasks)
        where TTask : DurableTask
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray<DurableTask>();
        ArgumentOutOfRangeException.ThrowIfZero(candidates.Length, nameof(tasks));
        return candidates;
    }
}

/// <summary>
/// A definition which runs as part of the execution which awaits it, using the identifier assigned with <c>WithId</c> as
/// its operation identifier.
/// </summary>
/// <remarks>
/// A scheduled definition runs as a child task with its own execution, which generates its operation identifier.
/// </remarks>
internal interface IInlineOperation
{
    /// <summary>
    /// Runs the operation in <paramref name="context"/> under <paramref name="operationId"/>, or under an identifier
    /// generated in <paramref name="context"/> when <paramref name="operationId"/> is <see cref="TaskId.None"/>.
    /// </summary>
    ValueTask<DurableTaskResponse> RunInlineAsync(DurableExecutionContext context, TaskId operationId);
}

/// <summary>A combinator without a result.</summary>
/// <param name="operationKind">The kind which names a generated operation identifier.</param>
/// <param name="inputs">The combinator's inputs.</param>
/// <param name="body">The combinator's body, which receives the inputs, the execution, and the operation identifier.</param>
internal sealed class CombinatorDurableTask(
    string operationKind,
    DurableTask[] inputs,
    Func<DurableTask[], DurableExecutionContext, TaskId, Task> body) : DurableTask, IInlineOperation
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => RunInlineAsync(context, TaskId.None);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public async ValueTask<DurableTaskResponse> RunInlineAsync(DurableExecutionContext context, TaskId operationId)
    {
        using var scope = DurableExecutionContext.Enter(context);
        try
        {
            await body(inputs, context, CombinatorOperation.Resolve(context, operationKind, operationId)).ConfigureAwait(false);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>A combinator with a result.</summary>
/// <param name="operationKind">The kind which names a generated operation identifier.</param>
/// <param name="inputs">The combinator's inputs.</param>
/// <param name="body">The combinator's body, which receives the inputs, the execution, and the operation identifier.</param>
internal sealed class CombinatorDurableTask<TResult>(
    string operationKind,
    DurableTask[] inputs,
    Func<DurableTask[], DurableExecutionContext, TaskId, Task<TResult>> body) : DurableTask<TResult>, IInlineOperation
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => RunInlineAsync(context, TaskId.None);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public async ValueTask<DurableTaskResponse> RunInlineAsync(DurableExecutionContext context, TaskId operationId)
    {
        using var scope = DurableExecutionContext.Enter(context);
        try
        {
            return DurableTaskResponse.FromResult(
                await body(inputs, context, CombinatorOperation.Resolve(context, operationKind, operationId)).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

internal static class CombinatorOperation
{
    // A generated identifier is allocated before the body's first await, so combinators which begin in deterministic
    // order receive the same identifiers on replay.
    public static TaskId Resolve(DurableExecutionContext context, string operationKind, TaskId operationId)
        => operationId.IsDefault ? context.CreateOperationId(operationKind) : operationId;
}
