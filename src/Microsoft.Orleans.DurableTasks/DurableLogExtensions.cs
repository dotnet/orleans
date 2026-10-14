using System;
using Orleans.Journaling;

namespace Orleans.DurableTasks;

/// <summary>
/// Provides progress reports, which a durable task appends to a grain's <see cref="IDurableLog{T}"/> exactly once.
/// </summary>
public static class DurableLogExtensions
{
    /// <summary>
    /// Appends <paramref name="item"/> to <paramref name="log"/> once for the running durable task, and returns the
    /// entry's sequence number.
    /// </summary>
    /// <typeparam name="T">The type of entries in the log.</typeparam>
    /// <param name="log">A log of the grain which runs the durable task.</param>
    /// <param name="item">The entry which reports the task's progress, such as a snapshot of its state.</param>
    /// <returns>The sequence number which the entry received when it was appended.</returns>
    /// <remarks>
    /// <para>
    /// A report is an unnamed recorded effect, <c>DurableTask.Once(() =&gt; log.Append(item))</c>: it takes the
    /// execution's next generated child identifier, and appends exactly once per position within the task. The entry and
    /// the report's record commit together with the task's next durable write, without a write of their own. A replay
    /// returns the recorded sequence number without appending again, so observers of the log see each report once, in
    /// the order in which the task made them. A run which ends before a durable write commits the record also loses the
    /// entry, so the next run appends it again.
    /// </para>
    /// <para>
    /// A report made in code which every replay runs again therefore publishes the task's progress safely: compute
    /// <paramref name="item"/> from the task's arguments, recorded results, and logical time, and make the body's
    /// unnamed operations in the same order in every run, so each replay reports the same entries at the same positions.
    /// A failed append, for example to a sealed log, is recorded as that failure, so every run observes the same failure.
    /// </para>
    /// <para>
    /// Readers of the log, such as <see cref="IDurableLog{T}.ReadAsync"/> with <see cref="DurableWatchMode.Committed"/>,
    /// receive each report once it is durable.
    /// </para>
    /// <para>
    /// Outside a durable task, such as in an ordinary grain method, the report calls <see cref="IDurableLog{T}.Append"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// order = order.Validating();
    /// _history.Report(order);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The log rejects the entry, for example because it is sealed; the host can't record the report from the calling
    /// code, for example after <c>ConfigureAwait(false)</c>; or an earlier run recorded another kind of operation at the
    /// report's position.
    /// </exception>
    /// <exception cref="NotSupportedException">The durable task host doesn't support recorded effects.</exception>
    public static long Report<T>(this IDurableLog<T> log, T item)
    {
        ArgumentNullException.ThrowIfNull(log);
        return DurableTask.Once(() => log.Append(item));
    }
}
