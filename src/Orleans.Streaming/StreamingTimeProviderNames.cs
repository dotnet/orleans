namespace Orleans.Streams;

/// <summary>
/// Service key used to resolve the streaming subsystem's <see cref="System.TimeProvider"/> from keyed dependency
/// injection. See <see cref="Orleans.Runtime.TimeProviderNames"/> for the core runtime areas.
/// </summary>
public static class StreamingTimeProviderNames
{
    /// <summary>
    /// The clock used by the streaming subsystem (pulling agents and managers, queue balancers).
    /// </summary>
    public const string Streaming = "Orleans.Streaming";
}
