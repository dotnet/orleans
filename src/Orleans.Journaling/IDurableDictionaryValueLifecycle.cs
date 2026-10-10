namespace Orleans.Journaling;

/// <summary>
/// Defines independent resource ownership for values stored in durable dictionaries.
/// </summary>
/// <typeparam name="TValue">The dictionary value type.</typeparam>
/// <remarks>
/// Register this service for values whose resources require explicit retention and release.
/// Live mutations acquire an owner before encoding. Replay transfers ownership of decoded values.
/// Replacement, removal, clear, reset, journal deletion, and dictionary disposal release stored owners.
/// Values returned by dictionary reads are borrowed from the dictionary.
/// Implementations must support independent owners of the same resources and run synchronously
/// on the dictionary's logical execution thread. A failed retain leaves ownership with the caller;
/// release must complete without throwing. The activation scope disposes DI-created dictionaries.
/// Callers arrange disposal of manually constructed dictionaries and their dependencies.
/// </remarks>
public interface IDurableDictionaryValueLifecycle<TValue>
{
    /// <summary>
    /// Acquires an independent owner of the value's resources.
    /// </summary>
    /// <param name="value">The borrowed value.</param>
    /// <returns>The value with independently retained resources.</returns>
    TValue Retain(TValue value);

    /// <summary>
    /// Releases the independently owned resources of a value.
    /// </summary>
    /// <param name="value">The owned value being retired.</param>
    void Release(TValue value);
}
