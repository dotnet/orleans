using Orleans.Configuration;
using RabbitMQ.Stream.Client;

namespace Orleans.Streaming.RabbitMQ.RabbitMQ;

/// <summary>
///     Configuration options to connect to the RabbitMQ Cluster
/// </summary>
public record RabbitMQClientOptions
{
    private static readonly TimeSpan DEFAULT_INTERVAL_TO_UPDATE_OFFSET = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The default maximum length of each RabbitMQ stream, in bytes.
    /// </summary>
    public const ulong DEFAULT_STREAM_MAX_LENGTH_BYTES = 200UL * 1024 * 1024;

    public TimeSpan IntervalToUpdateOffset { get; set; } = DEFAULT_INTERVAL_TO_UPDATE_OFFSET;

    public List<string> QueueNames { get; set; } = [];

    /// <summary>
    ///     Configures the StreamSystem to connect with the RabbitMQ Cluster
    /// </summary>
    public StreamSystemConfig StreamSystemConfig { get; set; } = new();

    /// <summary>
    ///     Gets or sets the retry options used when connecting to the RabbitMQ cluster.
    ///     <example>
    ///         Example:
    ///         <code>
    ///         options.ConnectionRetry = new RabbitMQConnectionRetryOptions
    ///         {
    ///             MaxAttempts = 4,
    ///             Delay = TimeSpan.FromSeconds(5)
    ///         };
    ///     </code>
    ///     </example>
    /// </summary>
    public RabbitMQConnectionRetryOptions ConnectionRetry { get; set; } = new();

    /// <summary>
    /// Gets or sets the options used to create RabbitMQ streams.
    /// </summary>
    /// <remarks>
    /// Each stream is limited to <see cref="DEFAULT_STREAM_MAX_LENGTH_BYTES"/> by default.
    /// Set <see cref="StreamSpec.MaxLengthBytes"/> to select a different retention capacity.
    /// </remarks>
    public StreamSpec StreamOptions { get; set; } = new(string.Empty)
    {
        MaxLengthBytes = DEFAULT_STREAM_MAX_LENGTH_BYTES
    };
}

public record RabbitMQQueueCacheOptions
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

internal sealed class RabbitMQStreamOptionsValidator(StreamPullingAgentOptions options, string name)
    : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        if (options.BatchContainerBatchSize != 1)
        {
            throw new OrleansConfigurationException(
                $"The RabbitMQ stream provider '{name}' requires " +
                $"{nameof(StreamPullingAgentOptions.BatchContainerBatchSize)} to be 1.");
        }
    }
}