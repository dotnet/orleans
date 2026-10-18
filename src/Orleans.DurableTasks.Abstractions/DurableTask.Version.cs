namespace Orleans.DurableTasks;

public abstract partial class DurableTask
{
    /// <summary>
    /// Returns the version of the change <paramref name="name"/> which the running durable task follows, so code which
    /// changes a workflow keeps replaying the executions which started before the change.
    /// </summary>
    /// <param name="name">
    /// The change's name within the running task, such as <c>"carrier-api"</c>. Every call with the same name in one
    /// execution returns the same version. Names don't contain <c>$</c>.
    /// </param>
    /// <param name="minSupported">
    /// The oldest version which the calling code still supports. Version <c>0</c> is the code before the change's first
    /// version.
    /// </param>
    /// <param name="maxSupported">The newest version, which executions that reach the call for the first time record.</param>
    /// <returns>A version from <paramref name="minSupported"/> to <paramref name="maxSupported"/>.</returns>
    /// <remarks>
    /// <para>
    /// The first call for <paramref name="name"/> in an execution decides the version and records it with the execution,
    /// and every later call and every replay returns the recorded version:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// An execution which reaches the call for the first time records <paramref name="maxSupported"/>.
    /// </description></item>
    /// <item><description>
    /// An execution which an earlier deployment ran past this point, without the call, records <c>0</c>: its replay
    /// finds operations which it hasn't reached yet, so the earlier run continued on the code before the change.
    /// </description></item>
    /// <item><description>
    /// A step inherits the version which an enclosing execution on the same grain recorded for
    /// <paramref name="name"/>.
    /// </description></item>
    /// </list>
    /// <para>
    /// Branch on the returned version, newest first, and keep each branch's operations and names while executions which
    /// follow it are pending. The record commits with the next durable write, as a
    /// <see cref="RunInline{TResult}(string, Func{TResult})"/> effect's does.
    /// </para>
    /// <para>
    /// To retire an old branch, raise <paramref name="minSupported"/> and remove the branch once no pending execution
    /// follows it. An execution whose recorded version lies outside the supported range stalls: the host cancels the
    /// attempt for <see cref="DurableTaskAttemptCancellationReason.UnsupportedVersion"/>, so the call throws
    /// <see cref="DurableTaskAttemptCanceledException"/> whose inner exception is a
    /// <see cref="DurableTaskVersionException"/>, discards the attempt's result, and keeps the task pending until a later
    /// activation replays it.
    /// </para>
    /// <para>
    /// Outside a durable task, the call returns <paramref name="maxSupported"/>, so helpers which ordinary methods share
    /// with durable task bodies can call it.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var version = DurableTask.GetVersion("carrier-api", minSupported: 0, maxSupported: 1);
    /// if (version &gt;= 1)
    /// {
    ///     await carrier.BookAsync(order).WithId("book");
    /// }
    /// else
    /// {
    ///     await legacyShipping.ShipAsync(order).WithId("ship");
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains <c>$</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minSupported"/> is negative, or <paramref name="maxSupported"/> is less than
    /// <paramref name="minSupported"/>.
    /// </exception>
    /// <exception cref="DurableTaskAttemptCanceledException">
    /// The host canceled the attempt, for example because the execution recorded a version outside the supported range,
    /// which stalls it; the exception's inner exception is then a <see cref="DurableTaskVersionException"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The host can't record the version from the calling code, for example after <c>ConfigureAwait(false)</c>.
    /// </exception>
    /// <exception cref="NotSupportedException">The host doesn't support versions.</exception>
    public static int GetVersion(string name, int minSupported, int maxSupported)
    {
        DurableTaskContext.ValidateName(name, nameof(name));
        ArgumentOutOfRangeException.ThrowIfNegative(minSupported);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSupported, minSupported);
        return DurableTaskContext.Ambient is { } context
            ? context.GetVersion(name, minSupported, maxSupported)
            : maxSupported;
    }
}
