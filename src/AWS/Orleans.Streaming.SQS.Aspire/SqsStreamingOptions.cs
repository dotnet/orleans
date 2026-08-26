namespace Aspire.Hosting;

/// <summary>
/// Configures an Orleans SQS stream provider and its AWS CDK queue resources.
/// </summary>
public sealed class SqsStreamingOptions
{
    /// <summary>
    /// Gets or sets the stable Orleans service identifier used to prefix every physical queue name.
    /// </summary>
    public required string ServiceId { get; init; }

    /// <summary>
    /// Gets or sets the number of SQS queues in the stream provider topology.
    /// </summary>
    public int PartitionCount { get; init; } = 8;

    /// <summary>
    /// Gets or sets a value indicating whether the provider uses FIFO queues.
    /// </summary>
    public bool FifoQueue { get; init; }

    /// <summary>
    /// Gets or sets the SQS long-poll duration, in seconds.
    /// </summary>
    public int? ReceiveWaitTimeSeconds { get; init; }

    /// <summary>
    /// Gets or sets the SQS visibility timeout, in seconds.
    /// </summary>
    public int? VisibilityTimeoutSeconds { get; init; }

    /// <summary>
    /// Gets or sets the silo queue-cache size.
    /// </summary>
    public int? CacheSize { get; init; }

    /// <summary>
    /// Gets or sets the keyed dependency injection key for the SQS data adapter.
    /// </summary>
    public string? DataAdapterKey { get; init; }

    /// <summary>
    /// Gets or sets application-defined message attributes requested when receiving messages.
    /// </summary>
    public IReadOnlyList<string> ReceiveMessageAttributes { get; init; } = [];

    /// <summary>
    /// Gets or sets SQS system attributes requested when receiving messages.
    /// </summary>
    public IReadOnlyList<string> ReceiveMessageSystemAttributes { get; init; } = [];
}
