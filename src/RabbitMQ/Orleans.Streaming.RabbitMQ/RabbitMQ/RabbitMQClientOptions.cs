using RabbitMQ.Stream.Client;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

/// <summary>
///     Configuration options to connect to the RabbitMQ Cluster
/// </summary>
public record RabbitMQClientOptions
{
    private static readonly TimeSpan DEFAULT_INTERVAL_TO_UPDATE_OFFSET = TimeSpan.FromMinutes(30);

    public TimeSpan IntervalToUpdateOffset { get; set; } = DEFAULT_INTERVAL_TO_UPDATE_OFFSET;

    public List<string> QueueNames { get; set; }

    /// <summary>
    ///     Configures the StreamSystem to connect with the RabbitMQ Cluster
    /// </summary>
    public StreamSystemConfig StreamSystemConfig { get; set; } = new();

    /// <summary>
    ///     Optional Circuit Break configuration used to connect with the RabbitMQ Cluster.
    ///     When not provided it will retry for 4 times and wait 5 seconds to retry again.
    ///     <example>
    ///         Example:
    ///         <code>
    ///         options.CircuitBreakConnectionConfig = new CircuitBreakConnectionConfig { RetryTimesUntilBreak = 4, WaitingTime = TimeSpan.FromSeconds(5) }
    ///     </code>
    ///     </example>
    /// </summary>
    public CircuitBreakConnectionConfig CircuitBreakConnectionConfig { get; set; } = new();

    /// <summary>
    ///     The Stream Options used to create streams.
    ///     Default queue max length 200mb
    ///     <see cref="StreamOptions" />
    /// </summary>
    public StreamSpec StreamOptions { get; set; } = new(string.Empty);
}

public record RabbitMqQueueCacheOptions
{
    /// <summary>
    ///     The default value of <see cref="CacheSize" />.
    /// </summary>
    public const int DEFAULT_CACHE_SIZE = 4096;

    /// <summary>
    ///     Gets or sets the size of the cache.
    /// </summary>
    /// <value>The size of the cache.</value>
    public int CacheSize { get; set; } = DEFAULT_CACHE_SIZE;
}