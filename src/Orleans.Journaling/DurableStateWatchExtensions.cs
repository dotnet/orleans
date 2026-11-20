namespace Orleans.Journaling;

/// <summary>
/// Provides watch streams for durable values.
/// </summary>
/// <remarks>
/// <para>
/// A watch stream begins with the current value once it is durable, then delivers the latest value after each journal
/// write which changes it is acknowledged, so readers observe only durable state. The stream retains only the latest value.
/// A value which nobody watches publishes nothing.
/// </para>
/// <para>
/// Enumerate the stream in the owning activation's execution context. The stream completes when the owning manager
/// stops and faults when a failed journal operation stops the manager, since only a new activation can establish
/// which changes are durable.
/// </para>
/// </remarks>
public static class DurableStateWatchExtensions
{
    /// <summary>
    /// Watches the committed value of a durable value.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The durable value.</param>
    /// <param name="cancellationToken">The token which ends the watch.</param>
    /// <returns>A stream which begins with the current committed value and delivers the latest value after each committed change.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> is not a built-in durable value registered with a journaled state manager.</exception>
    public static IAsyncEnumerable<ValueChange<T>> WatchAsync<T>(this IDurableValue<T> value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value is DurableValue<T> durableValue
            ? durableValue.WatchAsync(cancellationToken)
            : throw new NotSupportedException($"State of type '{value.GetType()}' does not support watching. Watch a built-in durable value.");
    }
}
