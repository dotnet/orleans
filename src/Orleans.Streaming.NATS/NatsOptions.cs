using System.Text.Json;
using Orleans.Runtime;
using NATS.Client.Core;
using NATS.Client.JetStream.Models;

namespace Orleans.Streaming.NATS;

/// <summary>
/// Configures the NATS JetStream stream provider.
/// </summary>
public class NatsOptions
{
    internal INatsConnection? Connection { get; set; }

    /// <summary>
    /// Gets or sets the name of the NATS JetStream stream used by the provider.
    /// </summary>
    public string StreamName { get; set; } = default!;

    /// <summary>
    /// Gets or sets the NATS client options.
    /// </summary>
    /// <remarks>
    /// When this value is <see langword="null"/>, the provider creates a client named
    /// <c>Orleans-{providerName}</c> which connects to <c>localhost:4222</c>.
    /// </remarks>
    public NatsOpts? NatsClientOptions { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of messages to fetch in a single batch.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the number of partitions in the NATS JetStream stream.
    /// This value determines the deterministic subject partitioning scheme and must match the number of
    /// Orleans stream queues configured for the provider.
    /// </summary>
    /// <remarks>
    /// The provider creates the stream and its subject mapping when it starts for the first time.
    /// When the configured partition count changes, the provider attempts to update the existing stream definition
    /// during startup. Startup fails if the NATS server rejects the requested update.
    /// For details, see
    /// <see href="https://docs.nats.io/nats-concepts/subject_mapping#deterministic-subject-token-partitioning">
    /// deterministic subject token partitioning
    /// </see>.
    /// </remarks>
    public int PartitionCount { get; set; } = 8;

    /// <summary>
    /// Gets or sets the number of connections used to send stream messages to NATS JetStream.
    /// </summary>
    public int ProducerCount { get; set; } = 8;

    /// <summary>
    /// Gets or sets the JSON serializer options used by the provider.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// Gets or sets the number of stream replicas in the NATS JetStream cluster.
    /// </summary>
    /// <remarks>
    /// Higher values improve availability during node restarts. Odd values are recommended for quorum,
    /// and the value cannot exceed the cluster size. A value of <c>3</c> tolerates a single-node failure
    /// in a three-node cluster.
    /// </remarks>
    public int NumReplicas { get; set; } = 1;

    /// <summary>
    /// Gets or sets the storage backend used by the NATS JetStream stream.
    /// </summary>
    /// <remarks>
    /// <see cref="StreamConfigStorage.File"/> provides durability across NATS server restarts.
    /// <see cref="StreamConfigStorage.Memory"/> stores messages in memory. The selected storage type
    /// must be enabled on the NATS server.
    /// </remarks>
    public StreamConfigStorage StorageType { get; set; } = StreamConfigStorage.File;
}

/// <summary>
/// Validates <see cref="NatsOptions"/> for a named NATS stream provider.
/// </summary>
/// <param name="options">The options to validate.</param>
/// <param name="name">The stream provider name.</param>
public class NatsStreamOptionsValidator(NatsOptions options, string? name = null) : IConfigurationValidator
{
    /// <inheritdoc />
    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(options.StreamName))
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.StreamName)} is required for the NATS stream provider '{name}'.");
        }

        if (options.NumReplicas < 1)
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.NumReplicas)} must be at least 1 for the NATS stream provider '{name}'.");
        }

        if (options.BatchSize < 1)
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.BatchSize)} must be at least 1 for the NATS stream provider '{name}'.");
        }

        if (options.PartitionCount < 1)
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.PartitionCount)} must be at least 1 for the NATS stream provider '{name}'.");
        }

        if (options.ProducerCount < 1)
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.ProducerCount)} must be at least 1 for the NATS stream provider '{name}'.");
        }

        if (!Enum.IsDefined(typeof(StreamConfigStorage), options.StorageType))
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.StorageType)} value '{options.StorageType}' is not valid for the NATS stream provider '{name}'. " +
                $"Valid values are {nameof(StreamConfigStorage.File)} and {nameof(StreamConfigStorage.Memory)}.");
        }
    }
}
