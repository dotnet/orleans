using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    private const string RunInlineMustBeSynchronous =
        "DurableTask.RunInline stores a synchronous result. For asynchronous work, use await DurableTask.Run(async () => ...).";

    private const string RunInlineAwaitsDurableTasks =
        "DurableTask.RunInline stores a synchronous result, and a durable task runs as a step. Await the durable task directly.";

    /// <summary>
    /// Runs <paramref name="effect"/> in the caller's current work item before returning, with no suspension and no child
    /// execution, and records its outcome in the same journal write as the journaled state which it changes. A replay
    /// returns the recorded outcome without running <paramref name="effect"/>.
    /// </summary>
    /// <typeparam name="TResult">The type of the effect's result, which the host stores with the task.</typeparam>
    /// <param name="name">
    /// The effect's name within the running task, which identifies the effect as <c>WithId</c> identifies a step: the
    /// first occurrence of a name in one run is <c>name</c>, and later occurrences append their order, such as
    /// <c>name$2</c>, so an effect in a loop keeps one name. Effects and steps share one count per name, and an earlier
    /// run's step under the effect's identifier is a divergence. Names don't contain <c>$</c>.
    /// </param>
    /// <param name="effect">The synchronous effect, which returns every value the body uses.</param>
    /// <returns>The result which the effect returned when it ran.</returns>
    /// <remarks>
    /// <para>
    /// The effect runs after task cancellation too, so compensation records its effects. It doesn't offload work to the
    /// thread pool. Returning doesn't mean that the record has committed: it commits with the caller's next journal write.
    /// </para>
    /// <para>
    /// The first run of the body runs the effect and records its outcome, its result or the exception it threw, with the
    /// task. The record commits with the next durable write together with the state changes the effect made, such as
    /// changes to journaled grain state, without a write of its own. A replay returns the recorded result, or throws the
    /// recorded exception, and finds the effect's state changes applied. An effect therefore changes state, reads state
    /// whose later changes the body mustn't observe, and produces values such as identifiers, random numbers, and
    /// wall-clock times, exactly once.
    /// </para>
    /// <para>
    /// A run which ends before a durable write commits the record, for example because its host crashed, also loses the
    /// state changes which the effect made, so the next run runs the effect again. A result which the host can't store,
    /// for example because it doesn't serialize, is recorded as that failure, so every run observes the same failure.
    /// </para>
    /// <para>
    /// The effect passes data to the body through its result: assignments to the body's variables happen only in the run
    /// which runs the effect. It doesn't await or block on tasks. A <c>RunInline</c> call within the effect runs as part of
    /// it, and the effect's record holds its outcome. Every other durable operation within the effect, such as starting a
    /// step or a child, a combinator, or <see cref="GetVersion(string, int, int)"/>, throws
    /// <see cref="InvalidOperationException"/>, which the effect records as its outcome: a replay doesn't run the effect,
    /// so the operation would be missing from it.
    /// </para>
    /// <para>
    /// The effect runs immediately, in the calling work item, where <see cref="DurableTask.Run{TResult}(Func{Task{TResult}})"/>
    /// creates a step which runs when it is awaited, scheduled, or posted, potentially in a later turn. Consequently,
    /// consecutive effects in one synchronous block, and the state which they change, commit in the same journal write,
    /// and no other turn of the grain runs between them; the effect observes the calling execution's
    /// <see cref="Current"/> and cancellation; and its record commits with the caller's next journal write rather than a
    /// write of its own. An effect and a step with the same name share an identifier, and a replay which finds the other
    /// kind recorded under it is a divergence, so a deployment which changes one into the other branches on
    /// <see cref="GetVersion(string, int, int)"/>.
    /// </para>
    /// <para>
    /// The record applies only within a durable task. Outside one, the effect runs every time, so helpers which ordinary
    /// methods share with durable task bodies can call it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var summary = DurableTask.RunInline("pay", () =&gt; SetStatus(OrderStatus.Paid));
    /// var paymentId = DurableTask.RunInline("payment-id", Guid.NewGuid);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example with <see cref="DurableTaskAttemptCancellationReason.Diverged"/>
    /// because an earlier run recorded another kind of operation, such as a step, under the effect's identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TResult"/> is an asynchronous operation, such as a <see cref="Task"/>, a
    /// <see cref="ValueTask"/>, or a <see cref="DurableTask"/>, or the host doesn't support recorded effects.
    /// </exception>
    public static TResult RunInline<TResult>(string name, Func<TResult> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (AsynchronousResult<TResult>.Value)
        {
            throw new NotSupportedException(RunInlineMustBeSynchronous);
        }

        return GetEffectContext(name) is { } context
            ? RunEffect(context, name, effect, static effect => DurableTaskResponse.FromResult(effect())).GetResult<TResult>()
            : effect();
    }

    /// <summary>
    /// Runs <paramref name="effect"/> in the caller's current work item before returning, and records its outcome; later
    /// runs of the task's body don't run it again.
    /// </summary>
    /// <param name="name">
    /// The effect's name within the running task. A name repeated in one run identifies the effect's later occurrences by
    /// their order. Names don't contain <c>$</c>.
    /// </param>
    /// <param name="effect">The synchronous effect.</param>
    /// <remarks>
    /// The effect runs and records its outcome as <see cref="RunInline{TResult}(string, Func{TResult})"/> describes. A replay
    /// returns, or throws the exception which the effect threw, without running it. Outside a durable task, the effect
    /// runs every time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example with <see cref="DurableTaskAttemptCancellationReason.Diverged"/>
    /// because an earlier run recorded another kind of operation, such as a step, under the effect's identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">The host doesn't support recorded effects.</exception>
    public static void RunInline(string name, Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (GetEffectContext(name) is not { } context)
        {
            effect();
            return;
        }

        RunEffect(
            context,
            name,
            effect,
            static effect =>
            {
                effect();
                return DurableTaskResponse.Completed;
            }).EnsureSuccessfulCompletion();
    }

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(string name, Func<Task> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(string name, Func<Task<TResult>> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunInlineAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(string name, Func<DurableTask> effect) => throw new NotSupportedException(RunInlineAwaitsDurableTasks);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunInlineAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(string name, Func<DurableTask<TResult>> effect) => throw new NotSupportedException(RunInlineAwaitsDurableTasks);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(string name, Func<ValueTask> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(string name, Func<ValueTask<TResult>> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>
    /// Runs <paramref name="effect"/> in the caller's current work item before returning, and records its outcome, which
    /// every later run of the task's body returns without running <paramref name="effect"/> again.
    /// </summary>
    /// <typeparam name="TResult">The type of the effect's result, which the host stores with the task.</typeparam>
    /// <param name="effect">The synchronous effect, which returns every value the body uses.</param>
    /// <returns>The result which the effect returned when it ran.</returns>
    /// <remarks>
    /// <para>
    /// The effect runs and records its outcome as <see cref="RunInline{TResult}(string, Func{TResult})"/> describes, under the
    /// execution's next generated child identifier, such as <c>$child-3</c>, instead of a name.
    /// </para>
    /// <para>
    /// Unnamed effects and unnamed children share one count of generated identifiers, in the order in which a run of the
    /// body creates them. A replay therefore
    /// finds each recorded outcome when the body creates its unnamed operations in the same order. Name an effect when
    /// the body's unnamed operations can change order or number between runs, for example across deployments.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var paymentId = DurableTask.RunInline(Guid.NewGuid);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example with <see cref="DurableTaskAttemptCancellationReason.Diverged"/>
    /// because an earlier run recorded another kind of operation, such as a step, under the effect's generated identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TResult"/> is an asynchronous operation, such as a <see cref="Task"/>, a
    /// <see cref="ValueTask"/>, or a <see cref="DurableTask"/>, or the host doesn't support recorded effects.
    /// </exception>
    public static TResult RunInline<TResult>(Func<TResult> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (AsynchronousResult<TResult>.Value)
        {
            throw new NotSupportedException(RunInlineMustBeSynchronous);
        }

        return DurableTaskContext.Ambient is { } context
            ? RunEffect(context, name: null, effect, static effect => DurableTaskResponse.FromResult(effect())).GetResult<TResult>()
            : effect();
    }

    /// <summary>
    /// Runs <paramref name="effect"/> in the caller's current work item before returning, and records its outcome; later
    /// runs of the task's body don't run it again.
    /// </summary>
    /// <param name="effect">The synchronous effect.</param>
    /// <remarks>
    /// The effect runs and records its outcome as <see cref="RunInline{TResult}(string, Func{TResult})"/> describes, under the
    /// execution's next generated child identifier, which <see cref="RunInline{TResult}(Func{TResult})"/> describes. A replay
    /// returns, or throws the exception which the effect threw, without running it. Outside a durable task, the effect
    /// runs every time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example with <see cref="DurableTaskAttemptCancellationReason.Diverged"/>
    /// because an earlier run recorded another kind of operation, such as a step, under the effect's generated identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">The host doesn't support recorded effects.</exception>
    public static void RunInline(Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (DurableTaskContext.Ambient is not { } context)
        {
            effect();
            return;
        }

        RunEffect(
            context,
            name: null,
            effect,
            static effect =>
            {
                effect();
                return DurableTaskResponse.Completed;
            }).EnsureSuccessfulCompletion();
    }

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(Func<Task> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(Func<Task<TResult>> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunInlineAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(Func<DurableTask> effect) => throw new NotSupportedException(RunInlineAwaitsDurableTasks);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunInlineAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(Func<DurableTask<TResult>> effect) => throw new NotSupportedException(RunInlineAwaitsDurableTasks);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void RunInline(Func<ValueTask> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.RunInline</c> can't record.</summary>
    [Obsolete(RunInlineMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult RunInline<TResult>(Func<ValueTask<TResult>> effect) => throw new NotSupportedException(RunInlineMustBeSynchronous);

    /// <summary>Validates <paramref name="name"/> and returns the running task's context, or <see langword="null"/>.</summary>
    private static DurableTaskContext? GetEffectContext(string name)
    {
        DurableTaskContext.ValidateName(name, nameof(name));
        return DurableTaskContext.Ambient;
    }

    /// <summary>
    /// Records <paramref name="effect"/>'s outcome under its identifier, or runs it as part of the enclosing effect when the
    /// calling code already runs in one.
    /// </summary>
    /// <remarks>
    /// The enclosing effect's record holds the nested effect's outcome, so the nested effect takes no identifier: a replay
    /// returns the enclosing effect's outcome without running either, and the body's later operations keep their
    /// identifiers.
    /// </remarks>
    private static DurableTaskResponse RunEffect<TEffect>(
        DurableTaskContext context,
        string? name,
        TEffect effect,
        Func<TEffect, DurableTaskResponse> run)
    {
        if (context.IsRunningEffect)
        {
            return run(effect);
        }

        context.ThrowIfCannotRunOperation();
        var effectId = context.CreateChildTaskId(name);
        return context.RunEffect(effectId, (Context: context, Effect: effect, Run: run), static state => Invoke(state));
    }

    // A canceled attempt records no effects, so its cancellation propagates instead of becoming the effect's outcome.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The effect's failure is recorded as its outcome.")]
    private static DurableTaskResponse Invoke<TEffect>((DurableTaskContext Context, TEffect Effect, Func<TEffect, DurableTaskResponse> Run) state)
    {
        try
        {
            return state.Context.RunAsEffect(state, static state => state.Run(state.Effect));
        }
        catch (Exception exception) when (exception is not DurableTaskAttemptCanceledException)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }

    /// <summary>Gets whether a result type is an asynchronous operation, whose completion an effect can't record.</summary>
    private static class AsynchronousResult<TResult>
    {
        public static readonly bool Value =
            typeof(Task).IsAssignableFrom(typeof(TResult))
            || typeof(DurableTask).IsAssignableFrom(typeof(TResult))
            || typeof(TResult) == typeof(ValueTask)
            || (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(ValueTask<>));
    }
}
