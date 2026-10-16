namespace Orleans.DurableTasks;

/// <summary>
/// Describes a named signal which carries a value of type <typeparamref name="T"/> into a durable task, such as an
/// external event or a human approval.
/// </summary>
/// <typeparam name="T">The type of the signal's value.</typeparam>
/// <remarks>
/// <para>
/// Senders and waiters share one descriptor, typically a static field:
/// <c>static readonly DurableSignal&lt;Shipment&gt; Shipped = new("shipped");</c>. A signal addresses one task which a
/// client or another grain invoked, and a wait anywhere in that task's execution observes the task's signals.
/// </para>
/// <para>
/// Each name latches one value per task: the host records the first value it receives, and every wait for that name
/// returns the recorded value, including waits which begin before the signal arrives, waits which begin after it, and
/// waits during replay. A repeated signal with an equal value leaves the task unchanged, and one with a different value
/// is rejected. Use <see cref="For(string)"/> to derive a distinct name for each occurrence of a repeated event.
/// </para>
/// </remarks>
public sealed class DurableSignal<T>
{
    /// <summary>Initializes a signal descriptor named <paramref name="name"/>.</summary>
    /// <param name="name">The signal name, unique among the signals of one task.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public DurableSignal(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
    }

    /// <summary>Gets the signal name.</summary>
    public string Name { get; }

    /// <summary>Gets a descriptor for one occurrence of this signal, named <c>{Name}/{suffix}</c>.</summary>
    /// <param name="suffix">The occurrence's stable identifier, such as an approval round or a line number.</param>
    /// <returns>A descriptor with the derived name and the same value type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="suffix"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="suffix"/> is empty.</exception>
    public DurableSignal<T> For(string suffix)
    {
        ArgumentException.ThrowIfNullOrEmpty(suffix);
        return new($"{Name}/{suffix}");
    }

    /// <summary>Creates a definition which waits for this signal and returns its value.</summary>
    /// <returns>
    /// A definition which, run within a durable execution, completes with the signal's recorded value. Awaited directly,
    /// it runs as part of the awaiting execution; as an input to <see cref="DurableTask.WhenAny(IReadOnlyList{DurableTask})"/>,
    /// it races other inputs, such as a <see cref="DurableTask.DelayUntil(DateTimeOffset)"/> timeout.
    /// </returns>
    /// <remarks>
    /// Durable cancellation of the waiting execution cancels the wait, unless the wait is shielded with
    /// <see cref="DurableTaskExtensions.Shielded{TResult}(DurableTask{TResult})"/>: a shielded wait receives a signal which
    /// arrives after the cancellation, and only an abort ends it. The value is the one the sender supplied for this
    /// signal's type; a different value type fails the wait with <see cref="InvalidCastException"/>.
    /// </remarks>
    public DurableTask<T> WaitAsync() => new SignalWaitDurableTask<T>(Name);

    /// <summary>Creates a definition which waits for this signal until <paramref name="deadline"/>.</summary>
    /// <param name="deadline">The time, measured using host logical time, at which the wait stops.</param>
    /// <returns>
    /// A definition which, awaited within a durable execution, runs as part of that execution and completes with
    /// <c>(true, value)</c> when the signal wins the race with <paramref name="deadline"/>, and with
    /// <c>(false, default)</c> when the deadline wins.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The definition is a <see cref="DurableTask.Race(IReadOnlyList{DurableTask})"/> between <see cref="WaitAsync"/>, the
    /// input <c>0</c>, and <see cref="DurableTask.DelayUntil(DateTimeOffset)"/>, the input <c>1</c>, which are identified
    /// under its operation identifier: the segment assigned with <c>WithId</c>, or a segment generated in execution order.
    /// The winner is the input which completed first, and the host selects the same winner on replay, so replay returns the
    /// same outcome. The other input then receives a durable cancellation request carrying the reason
    /// <c>Another task won the race.</c>, and the definition completes once both inputs are terminal, so neither the wait nor
    /// the deadline's delay outlives it.
    /// </para>
    /// <para>
    /// After the deadline wins, the signal keeps its latch: a later <see cref="WaitAsync"/> returns a value which arrives
    /// after the deadline. Durable cancellation of the waiting execution cancels the race, unless the race is shielded with
    /// <see cref="DurableTaskExtensions.Shielded{TResult}(DurableTask{TResult})"/>, which shields both inputs.
    /// </para>
    /// </remarks>
    public DurableTask<(bool Received, T Value)> TryWaitAsync(DateTimeOffset deadline)
        => new CombinatorDurableTask<(bool Received, T Value)>(
            ScheduledTask.RaceOperation,
            [WaitAsync(), DurableTask.DelayUntil(deadline)],
            static async (inputs, context, operationId) =>
            {
                var (index, response) = await DurableTask.RaceAsync(inputs, context, operationId).ConfigureAwait(false);
                if (index == 0)
                {
                    return (true, response.GetResult<T>());
                }

                response.EnsureSuccessfulCompletion();
                return (false, default!);
            });

    /// <inheritdoc />
    public override string ToString() => Name;
}

internal sealed class SignalWaitDurableTask<T>(string name) : DurableTask<T>
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(context.WaitForSignalAsync(name, context.InlineOperationCancellationToken));
    }

    internal override bool RunsInlineInParent => true;
}
