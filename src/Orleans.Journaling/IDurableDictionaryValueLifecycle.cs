namespace Orleans.Journaling;

/// <summary>
/// Defines the independent resource ownership of values stored in durable dictionaries.
/// </summary>
/// <typeparam name="TValue">The dictionary value type.</typeparam>
/// <remarks>
/// Register this service for values whose resources require explicit retention and release.
/// Live mutations retain the caller's value. Replay transfers ownership of decoded values.
/// Replacement, removal, reset, and dictionary disposal release the dictionary's owners.
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
