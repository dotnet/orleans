using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Represents a host-scheduled durable task.</summary>
/// <remarks>
/// A handle caches the task's terminal response once a wait, a poll, or a combinator observes it. A terminal response
/// never changes, so <see cref="IsCompleted"/>, <see cref="Status"/>, and <see cref="Exception"/> then inspect the task
/// synchronously, without contacting the host.
/// </remarks>
public abstract partial class ScheduledTask
{
    private DurableTaskResponse? _response;

    internal ScheduledTask() { }

    /// <summary>Gets the stable durable task identifier.</summary>
    /// <remarks>
    /// In a durable execution, the identifier names the execution's operation which started or observes the task, such as
    /// the record of a detached task, so combinators and replays address the operation by it.
    /// </remarks>
    public abstract TaskId Id { get; }

    /// <summary>
    /// Gets the identifier under which the task's host stores the task which this handle observes, which other parties use
    /// to address the task.
    /// </summary>
    /// <remarks>
    /// The identifier equals <see cref="Id"/> unless the host stores the task under another one: in a durable execution,
    /// the handle of a detached task observes the task's root identifier on its target, and a host can scope the
    /// identifier of a call to another host by the caller.
    /// </remarks>
    public virtual TaskId TargetTaskId => Id;

    /// <summary>
    /// Returns the feature <typeparamref name="TFeature"/> through which the task's host describes the task, or
    /// <see langword="null"/> when the host provides none.
    /// </summary>
    /// <typeparam name="TFeature">The feature's type, which the host defines or documents.</typeparam>
    /// <returns>The feature, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A host exposes host-specific information through features, such as the address under which other parties reach
    /// the task, while the handle stays independent of the host.
    /// </remarks>
    public virtual TFeature? GetFeature<TFeature>()
        where TFeature : class => null;

    /// <summary>Gets whether this handle has observed the task's terminal response.</summary>
    /// <remarks>
    /// The handle observes the terminal response when a wait, such as awaiting the handle or
    /// <see cref="GetResponseAsync(CancellationToken)"/>, or a poll returns it, or when a combinator such as
    /// <see cref="WhenAllCompleted(IReadOnlyList{ScheduledTask}, CancellationToken)"/> returns or yields the handle. Use
    /// <see cref="IsCompletedAsync"/> to poll the host.
    /// </remarks>
    public bool IsCompleted => Volatile.Read(ref _response) is not null;

    /// <summary>
    /// Gets the terminal status which this handle observed, or <see cref="DurableTaskStatus.Pending"/> until it observes
    /// the terminal response.
    /// </summary>
    /// <remarks>Use <see cref="GetStatusAsync"/> to poll the host.</remarks>
    public DurableTaskStatus Status => Volatile.Read(ref _response)?.Status ?? DurableTaskStatus.Pending;

    /// <summary>
    /// Gets the recorded failure or cancellation of a task which failed or was canceled, once this handle has observed its
    /// terminal response; otherwise <see langword="null"/>.
    /// </summary>
    public Exception? Exception => Volatile.Read(ref _response)?.Exception;

    /// <summary>Gets the terminal response which this handle observed, or <see langword="null"/>.</summary>
    internal DurableTaskResponse? Response => Volatile.Read(ref _response);

    /// <summary>Gets an awaiter which waits for successful completion.</summary>
    public ScheduledTaskAwaiter GetAwaiter() => new(this, CancellationToken.None);

    /// <summary>Polls whether the task has reached a terminal state.</summary>
    public async Task<bool> IsCompletedAsync(PollingOptions options = default, CancellationToken cancellationToken = default)
        => (await PollAsync(options, cancellationToken).ConfigureAwait(false)).IsCompleted;

    /// <summary>Polls the current task status.</summary>
    public async Task<DurableTaskStatus> GetStatusAsync(PollingOptions options = default, CancellationToken cancellationToken = default)
        => (await PollAsync(options, cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>Waits for and returns the terminal response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(CancellationToken cancellationToken = default)
        => ObserveAsync(cancellationToken).AsTask();

    /// <summary>Polls and returns the current response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(PollingOptions options, CancellationToken cancellationToken = default)
        => PollAsync(options, cancellationToken).AsTask();

    /// <summary>Waits for successful completion.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
        => (await ObserveAsync(cancellationToken).ConfigureAwait(false)).EnsureSuccessfulCompletion();

    /// <summary>Caches <paramref name="response"/> when it is terminal, and returns it.</summary>
    internal DurableTaskResponse Observe(DurableTaskResponse response)
    {
        if (response.IsCompleted)
        {
            Interlocked.CompareExchange(ref _response, response, null);
        }

        return response;
    }

    /// <summary>Waits for the terminal response and caches it.</summary>
    /// <remarks>A host failure which <see cref="WaitAsyncCore"/> throws synchronously propagates synchronously.</remarks>
    internal ValueTask<DurableTaskResponse> ObserveAsync(CancellationToken cancellationToken)
    {
        var wait = WaitAsyncCore(cancellationToken);
        return wait.IsCompletedSuccessfully ? new(Observe(wait.Result)) : ObserveAsync(wait);
    }

    private ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken)
    {
        var poll = PollAsyncCore(options, cancellationToken);
        return poll.IsCompletedSuccessfully ? new(Observe(poll.Result)) : ObserveAsync(poll);
    }

    private async ValueTask<DurableTaskResponse> ObserveAsync(ValueTask<DurableTaskResponse> response)
        => Observe(await response.ConfigureAwait(false));

    /// <summary>
    /// Waits for the terminal response and caches it, rejecting an incomplete response from the host with an
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// Selection and aggregation helpers use this wait, since they return tasks which have observed their terminal
    /// responses. A host failure which <see cref="WaitAsyncCore"/> throws synchronously propagates synchronously.
    /// </remarks>
    internal ValueTask<DurableTaskResponse> ObserveTerminalAsync(CancellationToken cancellationToken)
    {
        var wait = WaitAsyncCore(cancellationToken);
        return wait.IsCompletedSuccessfully ? new(ObserveTerminal(wait.Result)) : ObserveTerminalAsync(wait);
    }

    private DurableTaskResponse ObserveTerminal(DurableTaskResponse response)
        => response.IsCompleted ? Observe(response) : throw new InvalidOperationException("The durable task has not completed.");

    private async ValueTask<DurableTaskResponse> ObserveTerminalAsync(ValueTask<DurableTaskResponse> response)
        => ObserveTerminal(await response.ConfigureAwait(false));

    /// <summary>Durably requests cancellation.</summary>
    /// <remarks>
    /// The operation completes once the host has durably recorded the request, without waiting for the task to finish.
    /// The task's body observes the request and decides its outcome: the task is canceled when the cancellation escapes
    /// its body, and otherwise completes with the body's result or failure. Await the task to observe the outcome.
    /// </remarks>
    public abstract ValueTask CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Durably requests cancellation carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <remarks>
    /// The request behaves as <see cref="CancelAsync(CancellationToken)"/>. The first request determines the reason, so the
    /// task's body, for example in a <see langword="catch"/> block, can tell why it was canceled.
    /// </remarks>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default)
        => CancelAsyncCore(reason, cancellationToken);

    /// <summary>Asks the host to cancel the task carrying <paramref name="reason"/>.</summary>
    /// <remarks>
    /// Hosts which record reasons override this default implementation, which requests cancellation through
    /// <see cref="CancelAsync(CancellationToken)"/> without the reason.
    /// </remarks>
    protected virtual ValueTask CancelAsyncCore(string? reason, CancellationToken cancellationToken)
        => CancelAsync(cancellationToken);

    /// <summary>
    /// Administratively aborts the task: the host terminalizes it and its attached descendants as failed with a
    /// <see cref="DurableTaskAbortedException"/> carrying <paramref name="reason"/>, without running further task code.
    /// </summary>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    /// <remarks>
    /// Abort is for operators, for example when a task's body ignores cancellation or its cleanup is stuck. It runs no
    /// compensation: the host cancels the attempt which runs the task, and records nothing which the attempt does
    /// afterwards. The operation completes once the host has durably recorded the abort. Aborting a terminal task leaves
    /// it unchanged. Detached tasks are independent roots, which are aborted separately.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="NotSupportedException">The host doesn't support aborting tasks.</exception>
    public ValueTask AbortAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return AbortAsyncCore(reason, cancellationToken);
    }

    /// <summary>Asks the host to abort the task for a validated <paramref name="reason"/>.</summary>
    /// <remarks>Hosts which support abort override this default implementation, which throws <see cref="NotSupportedException"/>.</remarks>
    protected virtual ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable task host does not support aborting tasks.");

    /// <summary>Waits for a terminal host response.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken);

    /// <summary>Polls the current host response.</summary>
    protected abstract ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken);

    /// <summary>Waits for every scheduled task to complete successfully.</summary>
    /// <remarks>
    /// The returned task completes only after every input is terminal. It then throws the first failure in input order
    /// when any input failed; otherwise it throws the first input's <see cref="OperationCanceledException"/> in input
    /// order when any input was canceled. Input order selects the exception, so a replay observes the same one in any
    /// completion order. Wait cancellation and host wait failures are propagated.
    /// </remarks>
    public static async Task WhenAll(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        EnsureSuccessfulCompletion(await WaitForAllAsync(tasks.ToArray(), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc cref="WhenAll(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static async Task WhenAll<TResult>(IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        EnsureSuccessfulCompletion(await WaitForAllAsync(tasks.ToArray<ScheduledTask>(), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Waits until every scheduled task is terminal, and returns the tasks in input order.</summary>
    /// <param name="tasks">The tasks, which are captured when this method is called.</param>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the tasks.</param>
    /// <returns>
    /// The tasks, in input order. Each has observed its terminal response, so <see cref="Status"/>,
    /// <see cref="Exception"/>, and <see cref="ScheduledTask{TResult}.TryGetResult(out TResult)"/> inspect it.
    /// </returns>
    /// <remarks>
    /// A task's failure or cancellation doesn't fail the wait; wait cancellation and host wait failures are propagated.
    /// </remarks>
    public static async Task<ScheduledTask[]> WhenAllCompleted(
        IReadOnlyList<ScheduledTask> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        _ = await WaitForAllAsync(candidates, cancellationToken).ConfigureAwait(false);
        return candidates;
    }

    /// <inheritdoc cref="WhenAllCompleted(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static async Task<ScheduledTask<TResult>[]> WhenAllCompleted<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        _ = await WaitForAllAsync(candidates, cancellationToken).ConfigureAwait(false);
        return candidates;
    }

    /// <summary>Yields each scheduled task once, in completion order, as it reaches a terminal state.</summary>
    /// <param name="tasks">The tasks, which are captured when this method is called.</param>
    /// <returns>
    /// The tasks in completion order. Each yielded task has observed its terminal response, including a failure or a
    /// cancellation, which doesn't fail the enumeration.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Called within a durable execution, the tasks are children of that execution, and the host selects each next task:
    /// the remaining task which completed first in the host's durable completion order, so every replay yields the same
    /// order, including a replay in which more tasks have already completed. A host without a durable completion order
    /// records each selection, under a decision identifier beneath an operation identifier which this method generates in
    /// execution order when called. Outside a durable execution, tasks are yielded in the order their waits observe their
    /// responses.
    /// </para>
    /// <para>
    /// The enumeration's cancellation token abandons the wait without canceling the tasks, and host wait failures are
    /// propagated. Tasks which the enumeration doesn't reach keep running.
    /// </para>
    /// </remarks>
    public static IAsyncEnumerable<ScheduledTask> WhenEach(IReadOnlyList<ScheduledTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCoreAsync(tasks.ToArray(), DurableTaskContext.Ambient);
    }

    /// <inheritdoc cref="WhenEach(IReadOnlyList{ScheduledTask})"/>
    public static IAsyncEnumerable<ScheduledTask<TResult>> WhenEach<TResult>(IReadOnlyList<ScheduledTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return WhenEachCoreAsync(tasks.ToArray(), DurableTaskContext.Ambient);
    }

    private static IAsyncEnumerable<TTask> WhenEachCoreAsync<TTask>(TTask[] tasks, DurableTaskContext? context)
        where TTask : ScheduledTask
        => context is null
            ? WhenEachObservedAsync(tasks, CancellationToken.None)
            : WhenEachSelectedAsync(context, context.CreateOperationId(WhenEachOperation), tasks, CancellationToken.None);

    internal const string WhenEachOperation = "when-each";

    /// <summary>
    /// Yields <paramref name="tasks"/> in the order in which the host of <paramref name="context"/> selects their
    /// completions, each under a decision identifier beneath <paramref name="operationId"/>.
    /// </summary>
    internal static async IAsyncEnumerable<TTask> WhenEachSelectedAsync<TTask>(
        DurableTaskContext context,
        TaskId operationId,
        TTask[] tasks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        using var linked = CreateLinkedWaitCancellation(context, cancellationToken);
        var waitToken = linked?.Token ?? context.AttemptCancellationToken;
        var remaining = new List<TTask>(tasks);
        var candidates = new List<TaskId>(tasks.Length);
        foreach (var task in tasks)
        {
            candidates.Add(task.Id);
        }

        for (var step = 0; remaining.Count > 0; step++)
        {
            var decisionId = operationId.Child($"$next-{step.ToString(CultureInfo.InvariantCulture)}");
            context.ThrowIfAttemptCanceled();
            var selected = await context.ObserveAttempt(context.SelectCompletionAsync(decisionId, candidates, waitToken)).ConfigureAwait(false);
            var index = candidates.IndexOf(selected);
            if (index < 0)
            {
                throw new InvalidOperationException($"The host selected '{selected}', which is not a remaining candidate of '{operationId}'.");
            }

            var next = remaining[index];
            remaining.RemoveAt(index);
            candidates.RemoveAt(index);
            _ = await next.ObserveTerminalAsync(waitToken).ConfigureAwait(false);
            yield return next;
        }
    }

    private static async IAsyncEnumerable<TTask> WhenEachObservedAsync<TTask>(
        TTask[] tasks,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = new List<TTask>(tasks.Length);
        var waits = new List<Task<DurableTaskResponse>>(tasks.Length);
        try
        {
            foreach (var task in tasks)
            {
                remaining.Add(task);
                waits.Add(task.ObserveTerminalAsync(waitCancellation.Token).AsTask());
            }

            while (waits.Count > 0)
            {
                var completed = await Task.WhenAny(waits).ConfigureAwait(false);
                var index = waits.IndexOf(completed);
                _ = await completed.ConfigureAwait(false);
                var next = remaining[index];
                remaining.RemoveAt(index);
                waits.RemoveAt(index);
                yield return next;
            }
        }
        finally
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits for the terminal response of every task, caching it in the task, and returns the responses in input order.
    /// </summary>
    /// <remarks>
    /// Every wait completes before the first failed wait, in input order, is propagated.
    /// </remarks>
    private static async Task<DurableTaskResponse[]> WaitForAllAsync(ScheduledTask[] tasks, CancellationToken cancellationToken)
    {
        var waits = new Task<DurableTaskResponse>[tasks.Length];
        for (var index = 0; index < waits.Length; index++)
        {
            waits[index] = ObserveTaskAsync(tasks[index], cancellationToken);
        }

        await ((Task)Task.WhenAll(waits)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var responses = new DurableTaskResponse[waits.Length];
        for (var index = 0; index < responses.Length; index++)
        {
            responses[index] = await waits[index].ConfigureAwait(false);
        }

        return responses;

        // A wait which fails synchronously fails only its own observation, so the other waits still start.
        static async Task<DurableTaskResponse> ObserveTaskAsync(ScheduledTask task, CancellationToken cancellationToken)
            => await task.ObserveTerminalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws the first failure in input order or, when no response failed, the first cancellation in input order.
    /// </summary>
    internal static void EnsureSuccessfulCompletion(DurableTaskResponse[] responses)
    {
        DurableTaskResponse? canceled = null;
        foreach (var response in responses)
        {
            if (response.Status is DurableTaskStatus.Failed)
            {
                response.EnsureSuccessfulCompletion();
            }

            if (response.Status is DurableTaskStatus.Canceled)
            {
                canceled ??= response;
            }
        }

        canceled?.EnsureSuccessfulCompletion();
    }

    /// <summary>
    /// Returns the first scheduled task whose response completes, including failed or canceled durable
    /// responses. Wait cancellation and transport failures are propagated, and losing waits are canceled
    /// without canceling the durable tasks. Candidates are captured when this method is called.
    /// </summary>
    /// <remarks>
    /// Called within a durable execution, the tasks are children of that execution, and the host selects the task which
    /// completed first in its durable completion order, so every replay returns the same task, including a replay in which
    /// more tasks have already completed. A host without a durable completion order records the selection, under the
    /// decision identifier <c>$winner</c> beneath an operation identifier which this method generates in execution order
    /// when called. Outside a durable execution, the method returns the task whose wait observes its response first. The
    /// returned task has observed its terminal response.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tasks"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The host's wait for the selected task returned an incomplete response.</exception>
    public static async Task<ScheduledTask> WhenAny(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await SelectAnyIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    /// <inheritdoc cref="WhenAny(IReadOnlyList{ScheduledTask}, CancellationToken)"/>
    public static async Task<ScheduledTask<TResult>> WhenAny<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await SelectAnyIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// Selects the first completion of <paramref name="tasks"/> through the current execution's host, or by observing the
    /// waits outside a durable execution.
    /// </summary>
    /// <remarks>
    /// The operation identifier is generated before the first await, so calls which begin in deterministic order receive
    /// the same identifiers on replay.
    /// </remarks>
    private static Task<int> SelectAnyIndexAsync<TTask>(TTask[] tasks, CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Length, nameof(tasks));
        return DurableTaskContext.Ambient is { } context
            ? SelectAnyIndexAsync(context, context.CreateOperationId(DurableTask.WhenAnyOperation), tasks, cancellationToken)
            : WaitForAnyIndexAsync(tasks, cancellationToken);
    }

    private static async Task<int> SelectAnyIndexAsync<TTask>(
        DurableTaskContext context,
        TaskId operationId,
        TTask[] tasks,
        CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        using var linked = CreateLinkedWaitCancellation(context, cancellationToken);
        var waitToken = linked?.Token ?? context.AttemptCancellationToken;
        try
        {
            var winnerIndex = await SelectIndexAsync(context, operationId, tasks, waitToken).ConfigureAwait(false);
            _ = await tasks[winnerIndex].ObserveTerminalAsync(waitToken).ConfigureAwait(false);
            return winnerIndex;
        }
        catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Returns the index of the task among <paramref name="tasks"/> which the host of <paramref name="context"/> selects as
    /// the first to complete, under the decision identifier <c>$winner</c> beneath <paramref name="operationId"/>.
    /// </summary>
    internal static async Task<int> SelectIndexAsync(
        DurableTaskContext context,
        TaskId operationId,
        ScheduledTask[] tasks,
        CancellationToken cancellationToken)
    {
        var candidates = new TaskId[tasks.Length];
        for (var index = 0; index < candidates.Length; index++)
        {
            candidates[index] = tasks[index].Id;
        }

        context.ThrowIfAttemptCanceled();
        var selected = await context.ObserveAttempt(context.SelectCompletionAsync(operationId.Child("$winner"), candidates, cancellationToken))
            .ConfigureAwait(false);
        var winnerIndex = Array.IndexOf(candidates, selected);
        if (winnerIndex < 0)
        {
            throw new InvalidOperationException($"The host selected '{selected}', which is not a candidate of '{operationId}'.");
        }

        return winnerIndex;
    }

    /// <summary>
    /// Links the caller's wait cancellation with the attempt's cancellation token, or returns <see langword="null"/> when the
    /// caller's token can't be canceled, so the attempt's token alone applies.
    /// </summary>
    internal static CancellationTokenSource? CreateLinkedWaitCancellation(DurableTaskContext context, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.AttemptCancellationToken)
            : null;

    private static async Task<int> WaitForAnyIndexAsync<TTask>(
        IReadOnlyList<TTask> tasks,
        CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count, nameof(tasks));
        using var waitCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var waits = new List<Task<DurableTaskResponse>>(tasks.Count);
        try
        {
            foreach (var task in tasks)
            {
                waits.Add(task.ObserveTerminalAsync(waitCancellation.Token).AsTask());
            }
        }
        catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits).ConfigureAwait(false);
            throw;
        }

        var completed = await Task.WhenAny(waits).ConfigureAwait(false);
        var winnerIndex = waits.IndexOf(completed);
        try
        {
            _ = await completed.ConfigureAwait(false);
            return winnerIndex;
        }
        catch (OperationCanceledException exception) when (DurableTaskContext.IsCallerCancellation(exception, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The original wait-construction failure takes precedence over best-effort cleanup failures.")]
    private static async Task CancelAndDrainWaitsPreservingFailureAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits)
    {
        try
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex: -1).ConfigureAwait(false);
        }
        catch
        {
            // The failure which interrupted wait construction takes precedence over cleanup failures.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Canceling losing wait observations is best-effort cleanup.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "CancellationTokenSource.Cancel is used to synchronously notify every losing wait before they are drained.")]
    private static async Task CancelAndDrainWaitsAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits,
        int winnerIndex)
    {
        try
        {
            waitCancellation.Cancel();
        }
        catch
        {
            // Canceling losing wait observations is best-effort cleanup.
        }

        await DrainLosingWaitsAsync(waits, winnerIndex).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Losing observation failures do not determine the WhenAny result.")]
    private static async Task DrainLosingWaitsAsync(
        IReadOnlyList<Task<DurableTaskResponse>> waits,
        int winnerIndex)
    {
        for (var index = 0; index < waits.Count; index++)
        {
            if (index == winnerIndex)
            {
                continue;
            }

            try
            {
                _ = await waits[index].ConfigureAwait(false);
            }
            catch
            {
                // Losing observations are canceled and do not determine the result.
            }
        }
    }
}

/// <summary>Represents a host-scheduled durable task with a result.</summary>
public abstract partial class ScheduledTask<TResult> : ScheduledTask
{
    /// <summary>Gets an awaiter which returns the successful result.</summary>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this, CancellationToken.None);

    /// <summary>Configures a typed wait with a wait-cancellation token.</summary>
    public new ConfiguredScheduledTaskAwaitable<TResult> WaitAsync(CancellationToken cancellationToken = default)
        => new(this, cancellationToken);

    /// <summary>
    /// Returns the result which this handle observed, or throws the recorded failure or cancellation, as awaiting the task
    /// does.
    /// </summary>
    /// <returns>The task's successful result.</returns>
    /// <exception cref="InvalidOperationException">This handle hasn't observed the task's terminal response.</exception>
    public TResult GetResult()
        => (Response ?? throw new InvalidOperationException(
            $"Durable task '{Id}' has no observed terminal response. Await the task, or a combinator which returns it, first."))
            .GetResult<TResult>();

    /// <summary>Gets the result, when this handle has observed that the task completed successfully.</summary>
    /// <param name="value">The successful result, or <see langword="default"/>.</param>
    /// <returns>
    /// <see langword="true"/> when this handle observed a successful completion; otherwise <see langword="false"/>, for a
    /// task which failed, was canceled, or whose terminal response this handle hasn't observed.
    /// </returns>
    public bool TryGetResult([MaybeNullWhen(false)] out TResult value)
    {
        if (Response is { Status: DurableTaskStatus.CompletedSuccessfully } response)
        {
            value = response.GetResult<TResult>();
            return true;
        }

        value = default;
        return false;
    }
}

internal sealed class ScheduledTaskHandle(IScheduledTaskHandle handle) : ScheduledTask
{
    public override TaskId Id => handle.TaskId;

    public override TaskId TargetTaskId => handle.TargetTaskId;

    public override TFeature? GetFeature<TFeature>()
        where TFeature : class => handle.GetFeature<TFeature>();

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => HostHandleOperations.CancelAsync(handle, cancellationToken);

    protected override ValueTask CancelAsyncCore(string? reason, CancellationToken cancellationToken)
        => HostHandleOperations.CancelAsync(handle, reason, cancellationToken);

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken)
        => HostHandleOperations.AbortAsync(handle, reason, cancellationToken);

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
        => HostHandleOperations.WaitAsync(handle, cancellationToken);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken)
        => HostHandleOperations.PollAsync(handle, options, cancellationToken);
}

internal sealed class ScheduledTaskHandle<TResult>(IScheduledTaskHandle handle) : ScheduledTask<TResult>
{
    public override TaskId Id => handle.TaskId;

    public override TaskId TargetTaskId => handle.TargetTaskId;

    public override TFeature? GetFeature<TFeature>()
        where TFeature : class => handle.GetFeature<TFeature>();

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => HostHandleOperations.CancelAsync(handle, cancellationToken);

    protected override ValueTask CancelAsyncCore(string? reason, CancellationToken cancellationToken)
        => HostHandleOperations.CancelAsync(handle, reason, cancellationToken);

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken)
        => HostHandleOperations.AbortAsync(handle, reason, cancellationToken);

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
        => HostHandleOperations.WaitAsync(handle, cancellationToken);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken)
        => HostHandleOperations.PollAsync(handle, options, cancellationToken);
}

/// <summary>
/// Runs the operations of host handles for the durable execution which calls them: a canceled attempt's operations throw
/// <see cref="DurableTaskAttemptCanceledException"/>, including operations which the attempt's cancellation ends.
/// </summary>
/// <remarks>Outside a durable execution, the operations run unchanged.</remarks>
internal static class HostHandleOperations
{
    public static ValueTask<DurableTaskResponse> WaitAsync(IScheduledTaskHandle handle, CancellationToken cancellationToken)
    {
        if (DurableTaskContext.Ambient is not { } context)
        {
            return handle.WaitAsync(cancellationToken);
        }

        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(handle.WaitAsync(cancellationToken));
    }

    public static ValueTask<DurableTaskResponse> PollAsync(IScheduledTaskHandle handle, PollingOptions options, CancellationToken cancellationToken)
    {
        if (DurableTaskContext.Ambient is not { } context)
        {
            return handle.PollAsync(options, cancellationToken);
        }

        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(handle.PollAsync(options, cancellationToken));
    }

    public static ValueTask CancelAsync(IScheduledTaskHandle handle, CancellationToken cancellationToken)
    {
        if (DurableTaskContext.Ambient is not { } context)
        {
            return handle.CancelAsync(cancellationToken);
        }

        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(handle.CancelAsync(cancellationToken));
    }

    public static ValueTask CancelAsync(IScheduledTaskHandle handle, string? reason, CancellationToken cancellationToken)
    {
        if (DurableTaskContext.Ambient is not { } context)
        {
            return handle.CancelAsync(reason, cancellationToken);
        }

        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(handle.CancelAsync(reason, cancellationToken));
    }

    public static ValueTask AbortAsync(IScheduledTaskHandle handle, string reason, CancellationToken cancellationToken)
    {
        if (DurableTaskContext.Ambient is not { } context)
        {
            return handle.AbortAsync(reason, cancellationToken);
        }

        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(handle.AbortAsync(reason, cancellationToken));
    }
}

internal sealed class CompletedScheduledTask : ScheduledTask
{
    private readonly TaskId _id;
    private readonly DurableTaskResponse _response;

    public CompletedScheduledTask(TaskId id, DurableTaskResponse response)
    {
        _id = id;
        _response = Observe(response);
    }

    public override TaskId Id => _id;

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(_response);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken) => new(_response);
}

internal sealed class CompletedScheduledTask<TResult> : ScheduledTask<TResult>
{
    private readonly TaskId _id;
    private readonly DurableTaskResponse _response;

    public CompletedScheduledTask(TaskId id, DurableTaskResponse response)
    {
        _id = id;
        _response = Observe(response);
    }

    public override TaskId Id => _id;

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(_response);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken) => new(_response);
}

/// <summary>Provides an awaiter for a scheduled durable task without a result.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;

    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "The compiler consumes this awaiter once, and the stored awaiter preserves allocation-free IValueTaskSource implementations.")]
    internal ScheduledTaskAwaiter(ScheduledTask task, CancellationToken cancellationToken)
        => _awaiter = task.WaitAsync(cancellationToken).GetAwaiter();

    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

    /// <summary>Waits for successful completion.</summary>
    public void GetResult() => _awaiter.GetResult();
}

/// <summary>Provides an awaiter for a scheduled durable task with a result.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ScheduledTask<TResult> _task;
    private readonly ValueTaskAwaiter<DurableTaskResponse> _awaiter;

    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "The compiler consumes this awaiter once, and the stored awaiter preserves allocation-free IValueTaskSource implementations.")]
    internal ScheduledTaskAwaiter(ScheduledTask<TResult> task, CancellationToken cancellationToken)
    {
        _task = task;
        _awaiter = task.WaitAsyncCore(cancellationToken).GetAwaiter();
    }

    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

    /// <summary>Returns the successful result.</summary>
    public TResult GetResult() => _task.Observe(_awaiter.GetResult()).GetResult<TResult>();
}

/// <summary>Provides an awaitable scheduled durable task with a wait-cancellation token.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Configured awaitables are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ConfiguredScheduledTaskAwaitable<TResult>(
    ScheduledTask<TResult> task,
    CancellationToken cancellationToken)
{
    /// <summary>Gets the configured awaiter.</summary>
    public ScheduledTaskAwaiter<TResult> GetAwaiter() => new(task, cancellationToken);
}
