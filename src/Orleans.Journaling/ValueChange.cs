namespace Orleans.Journaling;

/// <summary>
/// Describes the latest committed value of an <see cref="IDurableValue{T}"/>.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>
/// Value watches deliver the latest committed value. A reader which falls behind receives the newest value and skips
/// intermediate values.
/// </remarks>
[GenerateSerializer]
public readonly record struct ValueChange<T>
{
    /// <summary>
    /// Gets the value.
    /// </summary>
    [Id(0)]
    public T? Value { get; init; }

    /// <summary>
    /// Gets the activation-local version of the journal write which made <see cref="Value"/> durable.
    /// </summary>
    /// <remarks>Versions increase within an activation and are not comparable across activations.</remarks>
    [Id(1)]
    public long Version { get; init; }
}
