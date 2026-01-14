using System.Text.Json;
using Orleans.Runtime;
using NATS.Client.Core;

namespace Orleans.Streaming.NATS;

/// <summary>
/// Configuration options for the NATS JetStream stream provider
/// </summary>
public class NatsOptions
{
    /// <summary>
    /// The NATS JetStream stream name
    /// </summary>
    public string StreamName { get; set; } = default!;

    /// <summary>
    /// Configuration options for the NATS client.
    /// If not provided, a default client will be created with the name Orleans-{providerName}
    /// and will connect to the NATS server at localhost:4222
    /// </summary>
    public NatsOpts? NatsClientOptions { get; set; }

    /// <summary>
    /// The maximum number of messages to fetch in a single batch.
    /// Defaults to 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// The number of partitions in the stream.
    /// This determines the number of pooling agents that will be created on this Orleans Cluster.
    /// This is mapped to a deterministic partitioning scheme of the NATS JetStream stream.
    /// The partitions are mapped from <remarks>"[Provider-Name].*.*"</remarks> to <remarks>"[Provider-Name].{{partition([PartitionCount],1,2)}}.{{wildcard(1)}}.{{wildcard(2)}}"</remarks>.
    /// For details on how partitioning works in NATS JetStream, see <see ref="https://docs.nats.io/nats-concepts/subject_mapping#deterministic-subject-token-partitioning"/>
    /// Defaults to 8. Increase it if you need more parallelism.
    /// <remarks>
    /// Deterministic partition scheme is a NATS server construct.
    /// This provider when started at the first time will create the stream with the partition scheme.
    /// If you need to change the partition count, you need to modify the value of this property and, you need to manually modify it on NATS Server first since the provider will not make updates to the JetStream stream definition.
    /// </remarks>
    /// </summary>
    public int PartitionCount { get; set; } = 8;

    /// <summary>
    /// The number of connections used to send stream messages to NATS JetStream.
    /// </summary>
    public int ProducerCount { get; set; } = 8;

    /// <summary>
    /// System.Text.Json serializer options to be used by the NATS provider.
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }
}

public class NatsStreamOptionsValidator(NatsOptions options, string? name = null) : IConfigurationValidator
{
    public void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(options.StreamName))
        {
            throw new OrleansConfigurationException(
                $"The {nameof(NatsOptions.StreamName)} is required for the NATS stream provider '{name}'.");
        }
    }
}
