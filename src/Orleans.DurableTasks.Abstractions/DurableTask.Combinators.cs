using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    internal const string WhenAnyOperation = "when-any";
    private const string WhenAllOperation = "when-all";
    private const string ScheduleAllOperation = "schedule-all";
    private const string WhenAllCompletedOperation = "when-all-completed";

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
    /// The winner is the input which completed first, and the host selects the same winner on replay, from its durable
    /// completion order or from a record of the selection. A winner which failed or was canceled fails the combined task.
    /// The other inputs keep running; <see cref="Race(IReadOnlyList{DurableTask})"/> also cancels them. Each input runs
    /// as a child identified by its input index under the combinator's operation identifier, which also identifies a
    /// recorded selection: the segment assigned with <c>WithId</c>, or a segment generated in execution order.
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
    /// The winner is the input which completed first, and the host selects the same winner on replay, from its durable
    /// completion order or from a record of the selection. A winner which failed or was canceled fails the combined task.
    /// The other inputs keep running; <see cref="Race{TResult}(IReadOnlyList{DurableTask{TResult}})"/> also cancels them.
    /// Each input runs as a child identified by its input index under the combinator's operation identifier, which also
    /// identifies a recorded selection: the segment assigned with <c>WithId</c>, or a segment generated in execution order.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync<TResult>);

    /// <inheritdoc cref="WhenAny{TResult}(IReadOnlyList{DurableTask{TResult}})"/>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(IEnumerable<DurableTask<TResult>> tasks)
        => new CombinatorDurableTask<(int Index, TResult Result)>(WhenAnyOperation, CreateCandidates(tasks), WhenAnyCoreAsync<TResult>);

    /// <summary>Creates a durable task which schedules every input task and returns their handles, in input order.</summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>
    /// A combinator which, awaited within a durable execution, runs as part of that execution and returns each input's
    /// handle at its input index once the inputs are scheduled, without waiting for their results.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The inputs are scheduled concurrently, so a host can commit the batch together. Each input runs as a child identified
    /// by its input index under the combinator's operation identifier: the segment assigned with <c>WithId</c>, or a
    /// segment generated in execution order.
    /// </para>
    /// <para>
    /// The handles belong to the awaiting execution, so the combinator runs only when awaited within a durable execution.
    /// Scheduling it as a task of its own throws <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    public static DurableTask<ScheduledTask[]> ScheduleAll(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask[]>(ScheduleAllOperation, tasks.ToArray(), ScheduleAllCoreAsync, inlineOnly: true);
    }

    /// <inheritdoc cref="ScheduleAll(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask[]> ScheduleAll(IEnumerable<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask[]>(ScheduleAllOperation, tasks.ToArray(), ScheduleAllCoreAsync, inlineOnly: true);
    }

    /// <inheritdoc cref="ScheduleAll(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask<TResult>[]> ScheduleAll<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask<TResult>[]>(
            ScheduleAllOperation,
            tasks.ToArray<DurableTask>(),
            ScheduleAllCoreAsync<TResult>,
            inlineOnly: true);
    }

    /// <inheritdoc cref="ScheduleAll(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask<TResult>[]> ScheduleAll<TResult>(IEnumerable<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask<TResult>[]>(
            ScheduleAllOperation,
            tasks.ToArray<DurableTask>(),
            ScheduleAllCoreAsync<TResult>,
            inlineOnly: true);
    }

    /// <summary>
    /// Creates a durable task which schedules every input task, waits until each is terminal, and returns their handles, in
    /// input order.
    /// </summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>
    /// A combinator which, awaited within a durable execution, runs as part of that execution and returns each input's
    /// handle at its input index. Each handle has observed its terminal response, so <see cref="ScheduledTask.Status"/>,
    /// <see cref="ScheduledTask.Exception"/>, and <see cref="ScheduledTask{TResult}.TryGetResult(out TResult)"/> inspect it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// An input's failure or cancellation doesn't fail the combinator, so the awaiting execution can act on each input's
    /// outcome, for example by compensating exactly the inputs which succeeded. Durable cancellation reaches the inputs,
    /// which are attached children, and the combinator waits for their outcomes; the cancellation of the attempt fails it with <see cref="DurableTaskAttemptCanceledException"/>.
    /// </para>
    /// <para>
    /// The inputs are scheduled concurrently and identified like <see cref="ScheduleAll(IReadOnlyList{DurableTask})"/>
    /// inputs. The combinator runs only when awaited within a durable execution; scheduling it as a task of its own throws
    /// <see cref="NotSupportedException"/>.
    /// </para>
    /// </remarks>
    public static DurableTask<ScheduledTask[]> WhenAllCompleted(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask[]>(WhenAllCompletedOperation, tasks.ToArray(), WhenAllCompletedCoreAsync, inlineOnly: true);
    }

    /// <inheritdoc cref="WhenAllCompleted(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask[]> WhenAllCompleted(IEnumerable<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask[]>(WhenAllCompletedOperation, tasks.ToArray(), WhenAllCompletedCoreAsync, inlineOnly: true);
    }

    /// <inheritdoc cref="WhenAllCompleted(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask<TResult>[]> WhenAllCompleted<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask<TResult>[]>(
            WhenAllCompletedOperation,
            tasks.ToArray<DurableTask>(),
            WhenAllCompletedCoreAsync<TResult>,
            inlineOnly: true);
    }

    /// <inheritdoc cref="WhenAllCompleted(IReadOnlyList{DurableTask})"/>
    public static DurableTask<ScheduledTask<TResult>[]> WhenAllCompleted<TResult>(IEnumerable<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new CombinatorDurableTask<ScheduledTask<TResult>[]>(
            WhenAllCompletedOperation,
            tasks.ToArray<DurableTask>(),
            WhenAllCompletedCoreAsync<TResult>,
            inlineOnly: true);
    }

    /// <summary>
    /// Schedules every input task and yields each input's handle once, in completion order, as the input reaches a
    /// terminal state.
    /// </summary>
    /// <param name="tasks">The input tasks, which are captured when this method is called.</param>
    /// <returns>
    /// The inputs' handles in completion order. Each yielded handle has observed its terminal response, including a failure
    /// or a cancellation, which doesn't fail the enumeration.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Call this method within a durable execution, which schedules the inputs when the enumeration begins, concurrently, as
    /// children identified by their input indexes under an operation identifier generated in execution order when this
    /// method is called. To name the inputs, await <see cref="ScheduleAll{TResult}(IReadOnlyList{DurableTask{TResult}})"/>
    /// with <c>WithId</c> and pass its handles to <see cref="ScheduledTask.WhenEach{TResult}(IReadOnlyList{ScheduledTask{TResult}})"/>.
    /// </para>
    /// <para>
    /// The host selects each next input: the remaining input which completed first in the host's durable completion order,
    /// so every replay yields the same order, including a replay in which more inputs have already completed. Inputs which
    /// the enumeration doesn't reach keep running, and durable cancellation reaches the inputs, which are attached
    /// children. The enumeration's cancellation token abandons scheduling the inputs and waiting for them, without
    /// canceling the inputs.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The method is called outside a durable execution.</exception>
    public static IAsyncEnumerable<ScheduledTask> WhenEach(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore(tasks.ToArray(), static handle => new ScheduledTaskHandle(handle));
    }

    /// <inheritdoc cref="WhenEach(IReadOnlyList{DurableTask})"/>
    public static IAsyncEnumerable<ScheduledTask> WhenEach(IEnumerable<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore(tasks.ToArray(), static handle => new ScheduledTaskHandle(handle));
    }

    /// <inheritdoc cref="WhenEach(IReadOnlyList{DurableTask})"/>
    public static IAsyncEnumerable<ScheduledTask<TResult>> WhenEach<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore(tasks.ToArray<DurableTask>(), static handle => (ScheduledTask<TResult>)new ScheduledTaskHandle<TResult>(handle));
    }

    /// <inheritdoc cref="WhenEach(IReadOnlyList{DurableTask})"/>
    public static IAsyncEnumerable<ScheduledTask<TResult>> WhenEach<TResult>(IEnumerable<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCore(tasks.ToArray<DurableTask>(), static handle => (ScheduledTask<TResult>)new ScheduledTaskHandle<TResult>(handle));
    }

    private static IAsyncEnumerable<TTask> WhenEachCore<TTask>(DurableTask[] inputs, Func<IScheduledTaskHandle, TTask> createTask)
        where TTask : ScheduledTask
    {
        var context = DurableTaskContext.Ambient
            ?? throw new InvalidOperationException("DurableTask.WhenEach schedules its inputs in a durable execution, so it is called within one.");
        return WhenEachAsync(inputs, context, context.CreateOperationId(ScheduledTask.WhenEachOperation), createTask, CancellationToken.None);
    }

    private static async IAsyncEnumerable<TTask> WhenEachAsync<TTask>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId,
        Func<IScheduledTaskHandle, TTask> createTask,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        TTask[] tasks;
        using (var linked = ScheduledTask.CreateLinkedWaitCancellation(context, cancellationToken))
        {
            tasks = await ScheduleInputsAsync(inputs, context, operationId, createTask, linked?.Token ?? context.AttemptCancellationToken)
                .ConfigureAwait(false);
        }

        await foreach (var task in ScheduledTask.WhenEachSelectedAsync(context, operationId, tasks, cancellationToken).ConfigureAwait(false))
        {
            yield return task;
        }
    }

    private static Task<ScheduledTask[]> ScheduleAllCoreAsync(DurableTask[] inputs, DurableTaskContext context, TaskId operationId)
        => ScheduleInputsAsync(inputs, context, operationId, static handle => (ScheduledTask)new ScheduledTaskHandle(handle), context.AttemptCancellationToken);

    private static Task<ScheduledTask<TResult>[]> ScheduleAllCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
        => ScheduleInputsAsync(
            inputs,
            context,
            operationId,
            static handle => (ScheduledTask<TResult>)new ScheduledTaskHandle<TResult>(handle),
            context.AttemptCancellationToken);

    private static async Task<ScheduledTask[]> WhenAllCompletedCoreAsync(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
        => await WaitForEveryCompletionAsync(
            await ScheduleAllCoreAsync(inputs, context, operationId).ConfigureAwait(false),
            context.AttemptCancellationToken).ConfigureAwait(false);

    private static async Task<ScheduledTask<TResult>[]> WhenAllCompletedCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
        => await WaitForEveryCompletionAsync(
            await ScheduleAllCoreAsync<TResult>(inputs, context, operationId).ConfigureAwait(false),
            context.AttemptCancellationToken).ConfigureAwait(false);

    private static async Task<TTask[]> WaitForEveryCompletionAsync<TTask>(TTask[] tasks, CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        foreach (var task in tasks)
        {
            _ = await task.ObserveTerminalAsync(cancellationToken).ConfigureAwait(false);
        }

        return tasks;
    }

    // Cancellation reaches the inputs, which are attached children, so each combinator waits for their outcomes and stops
    // waiting only when the host cancels the attempt.
    private static async Task WhenAllCoreAsync(DurableTask[] inputs, DurableTaskContext context, TaskId operationId)
        => await WaitForAllAsync(inputs, context, operationId).ConfigureAwait(false);

    private static async Task<TResult[]> WhenAllCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableTaskContext context,
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
    /// <remarks>Input order selects the exception, so a replay observes the same one in any completion order.</remarks>
    private static async Task<DurableTaskResponse[]> WaitForAllAsync(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
    {
        var cancellationToken = context.AttemptCancellationToken;
        var tasks = await ScheduleInputsAsync(inputs, context, operationId, static handle => new ScheduledTaskHandle(handle), cancellationToken)
            .ConfigureAwait(false);
        var responses = new DurableTaskResponse[tasks.Length];
        for (var index = 0; index < responses.Length; index++)
        {
            responses[index] = await tasks[index].ObserveTerminalAsync(cancellationToken).ConfigureAwait(false);
        }

        ScheduledTask.EnsureSuccessfulCompletion(responses);
        return responses;
    }

    private static async Task<int> WhenAnyCoreAsync(DurableTask[] inputs, DurableTaskContext context, TaskId operationId)
    {
        var cancellationToken = context.AttemptCancellationToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        await tasks[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return index;
    }

    private static async Task<(int Index, TResult Result)> WhenAnyCoreAsync<TResult>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId)
    {
        var cancellationToken = context.AttemptCancellationToken;
        var (tasks, index) = await SelectAsync(inputs, context, operationId, cancellationToken).ConfigureAwait(false);
        return (index, (await tasks[index].WaitAsyncCore(cancellationToken).ConfigureAwait(false)).GetResult<TResult>());
    }

    /// <summary>
    /// Schedules <paramref name="inputs"/> and returns them with the index of the winner, which the host selects under the
    /// decision identifier <c>$winner</c> beneath <paramref name="operationId"/>.
    /// </summary>
    internal static async Task<(ScheduledTask[] Tasks, int Index)> SelectAsync(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId,
        CancellationToken cancellationToken)
    {
        var tasks = await ScheduleInputsAsync(inputs, context, operationId, static handle => new ScheduledTaskHandle(handle), cancellationToken)
            .ConfigureAwait(false);
        return (tasks, await ScheduledTask.SelectIndexAsync(context, operationId, tasks, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<TTask[]> ScheduleInputsAsync<TTask>(
        DurableTask[] inputs,
        DurableTaskContext context,
        TaskId operationId,
        Func<IScheduledTaskHandle, TTask> createTask,
        CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        // Inputs are scheduled concurrently, so a host can commit the whole batch together. Their indexed identifiers keep
        // them replay-stable in any scheduling order.
        var scheduling = new Task<IScheduledTaskHandle>[inputs.Length];
        for (var index = 0; index < inputs.Length; index++)
        {
            scheduling[index] = ScheduleChildAsync(inputs[index], context, operationId.Child(index.ToString(CultureInfo.InvariantCulture)), cancellationToken);
        }

        var handles = await Task.WhenAll(scheduling).ConfigureAwait(false);
        var tasks = new TTask[handles.Length];
        for (var index = 0; index < tasks.Length; index++)
        {
            tasks[index] = createTask(handles[index]);
        }

        return tasks;
    }

    /// <summary>Schedules <paramref name="task"/> as the child <paramref name="taskId"/> of <paramref name="context"/>.</summary>
    internal static Task<IScheduledTaskHandle> ScheduleChildAsync(
        DurableTask task,
        DurableTaskContext context,
        TaskId taskId,
        CancellationToken cancellationToken)
    {
        return ScheduleAsync(new ConfiguredDurableTaskCore<DurableTask>(task, context).WithId(taskId), cancellationToken);

        static async Task<IScheduledTaskHandle> ScheduleAsync(
            ConfiguredDurableTaskCore<DurableTask> child,
            CancellationToken cancellationToken)
        {
            var (_, response, handle) = await child.StartAsync(DurableTaskStartOptions.None, cancellationToken).ConfigureAwait(false);
            Debug.Assert(response is null && handle is not null, "A child of an execution is scheduled through a handle.");
            return handle;
        }
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
    /// Gets whether the operation runs only inline, because its result holds objects of the awaiting execution, such as
    /// scheduled task handles, so it can't run as a task of its own.
    /// </summary>
    bool InlineOnly { get; }

    /// <summary>
    /// Runs the operation in <paramref name="context"/> under <paramref name="operationId"/>, or under an identifier
    /// generated in <paramref name="context"/> when <paramref name="operationId"/> is <see cref="TaskId.None"/>.
    /// </summary>
    ValueTask<DurableTaskResponse> RunInlineAsync(DurableTaskContext context, TaskId operationId);

    /// <summary>Creates the error for scheduling an inline-only operation as a task of its own.</summary>
    static NotSupportedException CreateInlineOnlyException() =>
        new("This combinator returns scheduled task handles, which belong to the awaiting execution, so it runs only when awaited within a durable execution and can't be scheduled as a task of its own.");
}

/// <summary>A combinator without a result.</summary>
/// <param name="operationKind">The kind which names a generated operation identifier.</param>
/// <param name="inputs">The combinator's inputs.</param>
/// <param name="body">The combinator's body, which receives the inputs, the execution, and the operation identifier.</param>
internal sealed class CombinatorDurableTask(
    string operationKind,
    DurableTask[] inputs,
    Func<DurableTask[], DurableTaskContext, TaskId, Task> body) : DurableTask, IInlineOperation
{
    public bool InlineOnly => false;

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) => RunInlineAsync(context, TaskId.None);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public async ValueTask<DurableTaskResponse> RunInlineAsync(DurableTaskContext context, TaskId operationId)
    {
        using var scope = DurableTaskContext.Enter(context);
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
/// <param name="inlineOnly">Whether the combinator runs only inline; see <see cref="IInlineOperation.InlineOnly"/>.</param>
internal sealed class CombinatorDurableTask<TResult>(
    string operationKind,
    DurableTask[] inputs,
    Func<DurableTask[], DurableTaskContext, TaskId, Task<TResult>> body,
    bool inlineOnly = false) : DurableTask<TResult>, IInlineOperation
{
    public bool InlineOnly => inlineOnly;

    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) =>
        inlineOnly
            ? new(DurableTaskResponse.FromException(IInlineOperation.CreateInlineOnlyException()))
            : RunInlineAsync(context, TaskId.None);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public async ValueTask<DurableTaskResponse> RunInlineAsync(DurableTaskContext context, TaskId operationId)
    {
        using var scope = DurableTaskContext.Enter(context);
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
    public static TaskId Resolve(DurableTaskContext context, string operationKind, TaskId operationId)
        => operationId.IsDefault ? context.CreateOperationId(operationKind) : operationId;
}
