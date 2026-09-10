namespace Orleans.Configuration;

/// <summary>
/// Configures the cache for a named memory stream provider.
/// </summary>
public class MemoryStreamCacheOptions
{
    /// <summary>
    /// Gets or sets the maximum number of queue records requested in each dequeue operation.
    /// </summary>
    /// <remarks>
    /// The value must be greater than zero. Each record contains one published batch of events.
    /// Smaller values reduce the number of records serialized into a dequeue response, while larger
    /// values amortize the cost of queue calls over more records. The limit is measured in records;
    /// response size in bytes depends on the serialized size of those records.
    /// </remarks>
    public int MaxAddCount { get; set; } = DefaultMaxAddCount;

    /// <summary>
    /// The default value of <see cref="MaxAddCount"/>.
    /// </summary>
    public const int DefaultMaxAddCount = 100;
}
