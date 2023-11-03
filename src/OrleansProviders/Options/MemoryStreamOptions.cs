namespace OrleansProviders.Options;

/// <summary>
/// Options specific to the Memory Streams provider.
/// </summary>
public class MemoryStreamOptions
{
    /// <summary>
    /// The maximum number of messages kept waiting for delivery in an individual in-memory partition.
    /// </summary>
    public int MaxEventCount { get; set; } = DefaultMaxEventCount;

    /// <summary>
    /// The default maximum number of messages kept waiting for delivery in an individual partition.
    /// </summary>
    public const int DefaultMaxEventCount = 16384;
}
