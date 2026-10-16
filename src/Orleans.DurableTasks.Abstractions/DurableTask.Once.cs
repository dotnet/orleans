using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    internal const string OnceSegmentPrefix = "$once:";

    private const string OnceMustBeSynchronous =
        "DurableTask.Once runs a synchronous effect. Run asynchronous work as a durable child, such as an async DurableTask step or DurableTask.Run, and await it.";

    /// <summary>
    /// Runs <paramref name="effect"/> once for the running durable task and returns its result, which every later run of
    /// the task's body returns without running <paramref name="effect"/> again.
    /// </summary>
    /// <typeparam name="TResult">The type of the effect's result, which the host stores with the task.</typeparam>
    /// <param name="name">
    /// The effect's name within the running task. A name repeated in one run identifies the effect's later occurrences by
    /// their order, so an effect in a loop keeps one name; names which <c>WithId</c> assigns share the same count. Names
    /// don't contain <c>$</c>.
    /// </param>
    /// <param name="effect">The synchronous effect, which returns every value the body uses.</param>
    /// <returns>The result which the effect returned when it ran.</returns>
    /// <remarks>
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
    /// which runs the effect. It doesn't await, schedule durable operations, or block on tasks.
    /// </para>
    /// <para>
    /// The record applies only within a durable task. Outside one, the effect runs every time, so helpers which ordinary
    /// methods share with durable task bodies can call it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var summary = DurableTask.Once("pay", () =&gt; SetStatus(OrderStatus.Paid));
    /// var paymentId = DurableTask.Once("payment-id", Guid.NewGuid);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>, or an
    /// earlier run recorded another kind of operation under this name and occurrence.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TResult"/> is an asynchronous operation, such as a <see cref="Task"/>, a
    /// <see cref="ValueTask"/>, or a <see cref="DurableTask"/>, or the host doesn't support recorded effects.
    /// </exception>
    public static TResult Once<TResult>(string name, Func<TResult> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (AsynchronousResult<TResult>.Value)
        {
            throw new NotSupportedException(OnceMustBeSynchronous);
        }

        return GetEffectContext(name) is { } context
            ? RunEffect(context, context.CreateNamedChildId(name, OnceSegmentPrefix), effect, static effect => DurableTaskResponse.FromResult(effect())).GetResult<TResult>()
            : effect();
    }

    /// <summary>
    /// Runs <paramref name="effect"/> once for the running durable task; later runs of the task's body don't run it again.
    /// </summary>
    /// <param name="name">
    /// The effect's name within the running task. A name repeated in one run identifies the effect's later occurrences by
    /// their order. Names don't contain <c>$</c>.
    /// </param>
    /// <param name="effect">The synchronous effect.</param>
    /// <remarks>
    /// The effect runs and records its outcome as <see cref="Once{TResult}(string, Func{TResult})"/> describes. A replay
    /// returns, or throws the exception which the effect threw, without running it. Outside a durable task, the effect
    /// runs every time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>, or an
    /// earlier run recorded another kind of operation under this name and occurrence.
    /// </exception>
    /// <exception cref="NotSupportedException">The host doesn't support recorded effects.</exception>
    public static void Once(string name, Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (GetEffectContext(name) is not { } context)
        {
            effect();
            return;
        }

        RunEffect(
            context,
            context.CreateNamedChildId(name, OnceSegmentPrefix),
            effect,
            static effect =>
            {
                effect();
                return DurableTaskResponse.Completed;
            }).EnsureSuccessfulCompletion();
    }

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.Once</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Once(string name, Func<Task> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.Once</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult Once<TResult>(string name, Func<Task<TResult>> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Once(string name, Func<DurableTask> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult Once<TResult>(string name, Func<DurableTask<TResult>> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>
    /// Runs <paramref name="effect"/> once for the running durable task and returns its result, which every later run of
    /// the task's body returns without running <paramref name="effect"/> again.
    /// </summary>
    /// <typeparam name="TResult">The type of the effect's result, which the host stores with the task.</typeparam>
    /// <param name="effect">The synchronous effect, which returns every value the body uses.</param>
    /// <returns>The result which the effect returned when it ran.</returns>
    /// <remarks>
    /// <para>
    /// The effect runs and records its outcome as <see cref="Once{TResult}(string, Func{TResult})"/> describes, under the
    /// execution's next generated child identifier, such as <c>$child-3</c>, instead of a name.
    /// </para>
    /// <para>
    /// Unnamed effects and unnamed children share one count of generated identifiers, in the order in which a run of the
    /// body creates them, as <see cref="DurableTaskContext.CreateChildTaskId(string)"/> describes. A replay therefore
    /// finds each recorded outcome when the body creates its unnamed operations in the same order. Name an effect when
    /// the body's unnamed operations can change order or number between runs, for example across deployments.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var paymentId = DurableTask.Once(Guid.NewGuid);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>, or an
    /// earlier run recorded another kind of operation under the effect's generated identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TResult"/> is an asynchronous operation, such as a <see cref="Task"/>, a
    /// <see cref="ValueTask"/>, or a <see cref="DurableTask"/>, or the host doesn't support recorded effects.
    /// </exception>
    public static TResult Once<TResult>(Func<TResult> effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (AsynchronousResult<TResult>.Value)
        {
            throw new NotSupportedException(OnceMustBeSynchronous);
        }

        return DurableTaskContext.Ambient is { } context
            ? RunEffect(context, context.CreateChildTaskId(null), effect, static effect => DurableTaskResponse.FromResult(effect())).GetResult<TResult>()
            : effect();
    }

    /// <summary>
    /// Runs <paramref name="effect"/> once for the running durable task; later runs of the task's body don't run it again.
    /// </summary>
    /// <param name="effect">The synchronous effect.</param>
    /// <remarks>
    /// The effect runs and records its outcome as <see cref="Once{TResult}(string, Func{TResult})"/> describes, under the
    /// execution's next generated child identifier, which <see cref="Once{TResult}(Func{TResult})"/> describes. A replay
    /// returns, or throws the exception which the effect threw, without running it. Outside a durable task, the effect
    /// runs every time.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="effect"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the effect from the calling code, for example after <c>ConfigureAwait(false)</c>, or an
    /// earlier run recorded another kind of operation under the effect's generated identifier.
    /// </exception>
    /// <exception cref="NotSupportedException">The host doesn't support recorded effects.</exception>
    public static void Once(Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        if (DurableTaskContext.Ambient is not { } context)
        {
            effect();
            return;
        }

        RunEffect(
            context,
            context.CreateChildTaskId(null),
            effect,
            static effect =>
            {
                effect();
                return DurableTaskResponse.Completed;
            }).EnsureSuccessfulCompletion();
    }

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.Once</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Once(Func<Task> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects an asynchronous effect, which <c>DurableTask.Once</c> can't record.</summary>
    /// <remarks>An asynchronous lambda also converts to a durable task, and this overload takes precedence.</remarks>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [OverloadResolutionPriority(1)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult Once<TResult>(Func<Task<TResult>> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static void Once(Func<DurableTask> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Rejects a durable task effect, which runs as a durable child when it is awaited.</summary>
    [Obsolete(OnceMustBeSynchronous, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static TResult Once<TResult>(Func<DurableTask<TResult>> effect) => throw new NotSupportedException(OnceMustBeSynchronous);

    /// <summary>Validates <paramref name="name"/> and returns the running task's context, or <see langword="null"/>.</summary>
    private static DurableTaskContext? GetEffectContext(string name)
    {
        DurableTaskContext.ValidateName(name, nameof(name));
        return DurableTaskContext.Ambient;
    }

    private static DurableTaskResponse RunEffect<TEffect>(
        DurableTaskContext context,
        TaskId effectId,
        TEffect effect,
        Func<TEffect, DurableTaskResponse> run)
    {
        context.ThrowIfAttemptCanceled();
        return context.RunEffect(effectId, (effect, run), static state => Invoke(state));
    }

    // A canceled attempt records no effects, so its cancellation propagates instead of becoming the effect's outcome.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The effect's failure is recorded as its outcome.")]
    private static DurableTaskResponse Invoke<TEffect>((TEffect Effect, Func<TEffect, DurableTaskResponse> Run) state)
    {
        try
        {
            return state.Run(state.Effect);
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
